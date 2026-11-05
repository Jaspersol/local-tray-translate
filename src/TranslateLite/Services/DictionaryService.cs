using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TranslateLite.Models;

namespace TranslateLite.Services;

/// <summary>
/// 词典访问层。内置词典（dictionary.db，随应用/安装包更新）与用户词条（user_dictionary.db）
/// 分库存放，用户数据不会在词典更新时被覆盖，因此两个连接各自持有、互不 JOIN。
/// </summary>
public sealed class DictionaryService : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteConnection _userConnection;

    public DictionaryService(string databasePath, string userDatabasePath)
    {
        _connection = CreateConnection(databasePath);
        _userConnection = CreateConnection(userDatabasePath);
    }

    private static SqliteConnection CreateConnection(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };

        return new SqliteConnection(builder.ToString());
    }

    // ---- 初始化：建表、FTS5 同步触发器、首启种子数据 ----

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();

        await ExecuteAsync("PRAGMA journal_mode=WAL;");
        await ExecuteAsync("PRAGMA synchronous=NORMAL;");
        await ExecuteAsync("PRAGMA temp_store=MEMORY;");

        await ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS entries (
                id INTEGER PRIMARY KEY,
                word TEXT NOT NULL,
                normalized_word TEXT NOT NULL,
                lang TEXT NOT NULL,
                phonetic TEXT,
                definition TEXT NOT NULL DEFAULT '',
                translation TEXT NOT NULL DEFAULT '',
                example TEXT,
                source TEXT NOT NULL DEFAULT 'local'
            );
            """);

        await ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_entries_normalized_word ON entries(normalized_word);");
        await ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_entries_lang ON entries(lang);");

        await ExecuteAsync("""
            CREATE VIRTUAL TABLE IF NOT EXISTS entries_fts USING fts5(
                word,
                translation,
                definition,
                content='entries',
                content_rowid='id'
            );
            """);

        await ExecuteAsync("""
            CREATE TRIGGER IF NOT EXISTS entries_ai AFTER INSERT ON entries BEGIN
                INSERT INTO entries_fts(rowid, word, translation, definition)
                VALUES (new.id, new.word, new.translation, new.definition);
            END;
            """);

        await ExecuteAsync("""
            CREATE TRIGGER IF NOT EXISTS entries_ad AFTER DELETE ON entries BEGIN
                INSERT INTO entries_fts(entries_fts, rowid, word, translation, definition)
                VALUES('delete', old.id, old.word, old.translation, old.definition);
            END;
            """);

        await ExecuteAsync("""
            CREATE TRIGGER IF NOT EXISTS entries_au AFTER UPDATE ON entries BEGIN
                INSERT INTO entries_fts(entries_fts, rowid, word, translation, definition)
                VALUES('delete', old.id, old.word, old.translation, old.definition);
                INSERT INTO entries_fts(rowid, word, translation, definition)
                VALUES (new.id, new.word, new.translation, new.definition);
            END;
            """);

        await SeedIfEmptyAsync();

        await _userConnection.OpenAsync();
        await ExecuteOnUserAsync("""
            CREATE TABLE IF NOT EXISTS user_entries (
                id INTEGER PRIMARY KEY,
                word TEXT NOT NULL,
                normalized_word TEXT NOT NULL,
                lang TEXT NOT NULL,
                phonetic TEXT,
                definition TEXT NOT NULL DEFAULT '',
                translation TEXT NOT NULL DEFAULT '',
                example TEXT,
                tags TEXT NOT NULL DEFAULT '[]',
                note TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """);
        await ExecuteOnUserAsync(
            "CREATE INDEX IF NOT EXISTS idx_user_entries_normalized_word ON user_entries(normalized_word);");
    }

    // ---- 用户词条 CRUD（独立库，加载时优先于内置词典命中） ----

    /// <summary>新增一条用户词条，返回新记录的 id。语言留空时按内容自动判定。</summary>
    /// <exception cref="ArgumentException"><paramref name="word"/> 规范化后为空。</exception>
    public async Task<int> AddUserEntryAsync(string word, string language, string? phonetic,
        string definition, string translation, string? example, string? note, IReadOnlyCollection<string>? tags)
    {
        var normalized = NormalizeInput(word);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("word 不能为空。", nameof(word));
        }

        var now = DateTime.UtcNow.ToString("o");
        var tagsJson = JsonSerializer.Serialize(tags ?? []);
        await using var command = _userConnection.CreateCommand();
        command.CommandText = """
            INSERT INTO user_entries(word, normalized_word, lang, phonetic, definition, translation,
                example, tags, note, created_at, updated_at)
            VALUES ($word, $normalized, $lang, $phonetic, $definition, $translation, $example, $tags, $note, $now, $now)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$word", word.Trim());
        command.Parameters.AddWithValue("$normalized", normalized);
        command.Parameters.AddWithValue(
            "$lang",
            string.IsNullOrWhiteSpace(language) ? DetectLanguage(normalized) : language);
        command.Parameters.AddWithValue("$phonetic", (object?)phonetic ?? DBNull.Value);
        command.Parameters.AddWithValue("$definition", definition ?? "");
        command.Parameters.AddWithValue("$translation", translation ?? "");
        command.Parameters.AddWithValue("$example", (object?)example ?? DBNull.Value);
        command.Parameters.AddWithValue("$tags", tagsJson);
        command.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    /// <summary>按 id 删除用户词条，返回是否真的删到了记录。</summary>
    public async Task<bool> DeleteUserEntryAsync(int id)
    {
        await using var command = _userConnection.CreateCommand();
        command.CommandText = "DELETE FROM user_entries WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync() > 0;
    }

    /// <summary>
    /// 全量更新一条用户词条（未传的字段会被写回空值）。语言列不更新：改语种等于换词典，
    /// 界面层已限制，这里保持字段不可变以免同一 id 的含义漂移。
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="word"/> 规范化后为空。</exception>
    public async Task<bool> UpdateUserEntryAsync(int id, string word, string? phonetic,
        string definition, string translation, string? example, string? note, IReadOnlyCollection<string>? tags)
    {
        var normalized = NormalizeInput(word);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("word 不能为空。", nameof(word));
        }

        await using var command = _userConnection.CreateCommand();
        command.CommandText = """
            UPDATE user_entries
            SET word = $word, normalized_word = $normalized, phonetic = $phonetic,
                definition = $definition, translation = $translation, example = $example,
                tags = $tags, note = $note, updated_at = $now
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$word", word.Trim());
        command.Parameters.AddWithValue("$normalized", normalized);
        command.Parameters.AddWithValue("$phonetic", (object?)phonetic ?? DBNull.Value);
        command.Parameters.AddWithValue("$definition", definition ?? "");
        command.Parameters.AddWithValue("$translation", translation ?? "");
        command.Parameters.AddWithValue("$example", (object?)example ?? DBNull.Value);
        command.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(tags ?? []));
        command.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        return await command.ExecuteNonQueryAsync() > 0;
    }

    /// <summary>按更新时间倒序列出用户词条；<paramref name="filter"/> 为空表示不过滤。</summary>
    /// <remarks>过滤是「词条 / 翻译 / 标签 JSON」三列的 contains 匹配，刻意不用 FTS：
    /// 用户词条量级很小，LIKE 足够且能顺带命中标签原文。</remarks>
    public async Task<IReadOnlyList<UserEntry>> ListUserEntriesAsync(string? filter = null, int limit = 500)
    {
        var results = new List<UserEntry>();
        await using var command = _userConnection.CreateCommand();
        command.CommandText = """
            SELECT id, word, lang, phonetic, definition, translation, example, tags, note, created_at, updated_at
            FROM user_entries
            WHERE ($filter IS NULL
                OR word LIKE '%' || $filter || '%'
                OR translation LIKE '%' || $filter || '%'
                OR tags LIKE '%' || $filter || '%')
            ORDER BY updated_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$filter", string.IsNullOrWhiteSpace(filter) ? DBNull.Value : filter.Trim());
        command.Parameters.AddWithValue("$limit", limit);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new UserEntry(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10)));
        }

        return results;
    }

    /// <summary>
    /// 从制表符分隔的文本导入用户词条（word / translation / 可选 definition / 可选 example，'#' 开头为注释）。
    /// </summary>
    /// <exception cref="InvalidDataException">文件里没有一行符合格式。</exception>
    public async Task ImportUserEntriesAsync(string filePath)
    {
        var lines = await File.ReadAllLinesAsync(filePath);
        var imported = 0;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split('\t');
            if (parts.Length < 2)
            {
                continue;
            }

            var word = parts[0].Trim();
            var translation = parts[1].Trim();
            var definition = parts.Length > 2 ? parts[2].Trim() : "";
            var example = parts.Length > 3 && parts[3].Trim().Length > 0 ? parts[3].Trim() : null;
            if (word.Length == 0)
            {
                continue;
            }

            await AddUserEntryAsync(
                word,
                DetectLanguage(word),
                null,
                definition,
                translation,
                example,
                note: null,
                tags: null);
            imported++;
        }

        if (imported == 0)
        {
            throw new InvalidDataException("文件中未找到有效的词条行（格式：word<TAB>translation[<TAB>definition[<TAB>example]]）。");
        }
    }

    // ---- 查询：分层召回，逐层补足 limit ----

    /// <summary>
    /// 分层查词：用户词典 → 精确 → 候选切分 → FTS → 编辑距离，每层只补剩余名额，最多返回 <paramref name="limit"/> 条。
    /// </summary>
    /// <remarks>
    /// 顺序按「命中可信度」排：用户自己录的词优先于内置词典，精确匹配优先于模糊匹配；
    /// 每层只取缺口数量，是为了让高可信层的结果不被低可信层挤掉，同时避免全表打分。
    /// FTS 放在编辑距离之前，因为倒排命中（词形/释义）比按距离猜更接近用户意图；
    /// 编辑距离最贵（要先取候选再 O(n*m) 计算），因此永远放在最后一层兜底。
    /// </remarks>
    public async Task<IReadOnlyList<DictionaryEntry>> LookupAsync(string input, int limit = 12)
    {
        var query = NormalizeInput(input);
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var userMatches = await QueryUserEntriesAsync(query, limit);
        var exact = await QueryExactAsync(query, limit);
        var merged = userMatches
            .Concat(exact.Where(item => userMatches.All(existing => !SameEntry(existing, item))))
            .Take(limit)
            .ToList();
        if (merged.Count >= limit)
        {
            return merged;
        }

        var candidateMatches = await QueryCandidatesAsync(query, limit - merged.Count);
        merged = merged
            .Concat(candidateMatches.Where(item => merged.All(existing => !SameEntry(existing, item))))
            .Take(limit)
            .ToList();
        if (merged.Count >= limit)
        {
            return merged;
        }

        var fuzzy = await QueryFtsAsync(query, limit - merged.Count);
        merged = merged
            .Concat(fuzzy.Where(item => merged.All(existing => !SameEntry(existing, item))))
            .Take(limit)
            .ToList();
        if (merged.Count >= limit)
        {
            return merged;
        }

        var didYouMean = await QueryEditDistanceAsync(query, limit - merged.Count);
        return merged
            .Concat(didYouMean.Where(item => merged.All(existing => !SameEntry(existing, item))))
            .Take(limit)
            .ToList();
    }

    public void Dispose()
    {
        _connection.Dispose();
        _userConnection.Dispose();
    }

    // ---- 查询实现：用户词典 / 精确 / 候选 / FTS / 编辑距离 / 切分工具 ----

    /// <summary>用户词典召回：先精确（按词形），再按释义/例句包含关键字模糊命中，两段各自去重后按 UNION ALL 拼接。</summary>
    private async Task<List<DictionaryEntry>> QueryUserEntriesAsync(string query, int limit)
    {
        // 两段 UNION ALL 而不是一段 OR，是因为排序规则不同：精确段按词长、时间排（短词更可能是本体），
        // 模糊段只按词长排（更新时间对“包含关键字”没有参考价值）；合成一段会逼两者共用一套 ORDER BY。
        var normalized = NormalizeWord(query);
        await using var command = _userConnection.CreateCommand();
        command.CommandText = """
            SELECT * FROM (
                SELECT word, lang, phonetic, definition, translation, example, 'user' AS source
                FROM user_entries
                WHERE normalized_word = $query
                ORDER BY length(word), updated_at DESC
                LIMIT $limit
            ) UNION ALL SELECT * FROM (
                SELECT word, lang, phonetic, definition, translation, example, 'user' AS source
                FROM user_entries
                WHERE normalized_word != $query
                  AND (translation LIKE '%' || $query || '%' OR example LIKE '%' || $query || '%')
                ORDER BY length(word)
                LIMIT $limit
            );
            """;
        command.Parameters.AddWithValue("$query", normalized);
        command.Parameters.AddWithValue("$limit", limit);
        return await ReadEntriesAsync(command);
    }

    private async Task<List<DictionaryEntry>> QueryEditDistanceAsync(string query, int limit)
    {
        if (limit <= 0)
        {
            return [];
        }

        // 编辑距离兜底：先用 LIKE '首字符%' 圈出候选（走 normalized_word 前缀索引），再在内存里精确算距离。
        // 候选上限 4096 是「一次查词最多算 4096 次 O(n*m)」的经验值，超过它查词会有肉眼可感的卡顿。
        var normalized = NormalizeWord(query);
        var candidates = new List<string>();
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT normalized_word
            FROM entries
            WHERE normalized_word LIKE $prefix
            ORDER BY ABS(length(normalized_word) - length($query)), length(normalized_word)
            LIMIT $candidateLimit;
            """;
        command.Parameters.AddWithValue("$prefix", ToLikePrefix(normalized));
        command.Parameters.AddWithValue("$query", normalized);
        command.Parameters.AddWithValue("$candidateLimit", 4096);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            candidates.Add(reader.GetString(0));
        }

        var userCandidates = new HashSet<string>();
        await using var userCommand = _userConnection.CreateCommand();
        userCommand.CommandText =
            "SELECT DISTINCT normalized_word FROM user_entries " +
            "WHERE normalized_word LIKE $prefix LIMIT 4096;";
        userCommand.Parameters.AddWithValue("$prefix", ToLikePrefix(normalized));
        await using var userReader = await userCommand.ExecuteReaderAsync();
        while (await userReader.ReadAsync())
        {
            userCandidates.Add(userReader.GetString(0));
        }

        candidates.AddRange(userCandidates.Except(candidates, StringComparer.Ordinal));

        var budget = EditDistanceBudget(query);
        var results = new List<DictionaryEntry>();
        foreach (var candidate in candidates
                     .Where(candidate => candidate.Length is >= 2 and <= 48)
                     .OrderBy(candidate => OsaDistance(query, candidate))
                     .ThenBy(candidate => candidate.Length))
        {
            if (OsaDistance(query, candidate) > budget)
            {
                break;
            }

            var matches = await QueryExactAsync(candidate, limit - results.Count);
            foreach (var match in matches)
            {
                if (results.All(existing => !SameEntry(existing, match)))
                {
                    results.Add(match);
                }
            }

            if (results.Count >= limit)
            {
                break;
            }
        }

        return results;
    }

    private static string ToLikePrefix(string query)
    {
        // 首字符原样，其余任意；对拉丁与 CJK 词均可行，且利用 normalized_word 索引前缀。
        var first = query.Length > 0 ? query[..1] : string.Empty;
        return string.Concat(first, "%");
    }

    /// <summary>允许的编辑距离随查询长度放宽：短词放太松会召回一堆无关词，长词放太紧则几乎召不回。</summary>
    private static int EditDistanceBudget(string query)
    {
        // 阈值依据：≤4 字符（如 "wrod"）只容忍 1 处笔误，否则 3 个字母的词会匹配到半张表；
        // ≤8 字符容忍 2 处；更长的词组笔误概率随之上升，给到 3 处，再往上就失去区分度。
        var chars = query.EnumerateRunes().Count();
        return chars switch
        {
            <= 4 => 1,
            <= 8 => 2,
            _ => 3
        };
    }

    // OSA（最优字符串对齐）距离：相邻换位记 1，覆盖 wrold/teh 类换位 typo。
    // 名字如实写成 OsaDistance：若只叫 Levenshtein，后来者会以为换位要记 2 而误判召回结果。
    private static int OsaDistance(string source, string target)
    {
        if (source.Length == 0)
        {
            return target.Length;
        }

        if (target.Length == 0)
        {
            return source.Length;
        }

        var previous = new int[target.Length + 1];
        var current = new int[target.Length + 1];
        var twoRowsBack = new int[target.Length + 1];
        for (var j = 0; j <= target.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= source.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= target.Length; j++)
            {
                var substitution = previous[j - 1] + (source[i - 1] == target[j - 1] ? 0 : 1);
                current[j] = Math.Min(substitution, Math.Min(previous[j] + 1, current[j - 1] + 1));
                if (i > 1 && j > 1
                    && source[i - 1] == target[j - 2]
                    && source[i - 2] == target[j - 1]
                    && source[i - 1] != source[i - 2])
                {
                    current[j] = Math.Min(current[j], twoRowsBack[j - 2] + 1);
                }
            }

            (twoRowsBack, previous) = (previous, current);
            (previous, current) = (current, previous);
        }

        return previous[target.Length];
    }

    private static string DetectLanguage(string text)
    {
        var cjkCount = text.Count(IsCjk);
        var latinCount = text.Count(char.IsAsciiLetter);
        return cjkCount > latinCount ? "zh" : "en";
    }

    private async Task ExecuteOnUserAsync(string sql)
    {
        await using var command = _userConnection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<DictionaryEntry>> QueryExactAsync(string query, int limit)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT word, lang, phonetic, definition, translation, example, source
            FROM entries
            WHERE normalized_word = $query OR word = $raw
            ORDER BY CASE WHEN normalized_word = $query THEN 0 ELSE 1 END, length(word)
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$query", NormalizeWord(query));
        command.Parameters.AddWithValue("$raw", query);
        command.Parameters.AddWithValue("$limit", limit);
        return await ReadEntriesAsync(command);
    }

    private async Task<List<DictionaryEntry>> QueryFtsAsync(string query, int limit)
    {
        if (limit <= 0)
        {
            return [];
        }

        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT e.word, e.lang, e.phonetic, e.definition, e.translation, e.example, e.source
            FROM entries_fts f
            JOIN entries e ON e.id = f.rowid
            WHERE entries_fts MATCH $query
            ORDER BY rank
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$query", ToFtsQuery(query));
        command.Parameters.AddWithValue("$limit", limit);
        return await ReadEntriesAsync(command);
    }

    private async Task<List<DictionaryEntry>> QueryCandidatesAsync(string query, int limit)
    {
        if (limit <= 0)
        {
            return [];
        }

        var results = new List<DictionaryEntry>();
        foreach (var candidate in ExtractCandidates(query))
        {
            var matches = await QueryExactAsync(candidate, limit - results.Count);
            foreach (var match in matches)
            {
                if (results.All(existing => !SameEntry(existing, match)))
                {
                    results.Add(match);
                }
            }

            if (results.Count >= limit)
            {
                break;
            }
        }

        return results;
    }

    private static async Task<List<DictionaryEntry>> ReadEntriesAsync(SqliteCommand command)
    {
        var entries = new List<DictionaryEntry>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entries.Add(new DictionaryEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6)));
        }

        return entries;
    }

    // ---- 首启种子数据：让空库也能演示查词 ----

    private async Task SeedIfEmptyAsync()
    {
        await using var countCommand = _connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM entries;";
        var count = (long)(await countCommand.ExecuteScalarAsync() ?? 0L);
        if (count > 0)
        {
            return;
        }

        var samples = new[]
        {
            new DictionaryEntry(
                "hello", "en", "heh-loh", "used as a greeting", "你好; 喂",
                "Hello, nice to meet you.", "sample"),
            new DictionaryEntry(
                "translate", "en", "trans-late", "to express words in another language", "翻译; 转换",
                "Translate the selected text.", "sample"),
            new DictionaryEntry(
                "dictionary", "en", "dik-shuh-ner-ee", "a reference work of words and meanings",
                "词典; 字典", "Look it up in the dictionary.", "sample"),
            new DictionaryEntry(
                "截图", "zh", null, "capture an image of part or all of the screen",
                "screenshot; screen capture", "截图翻译", "sample"),
            new DictionaryEntry(
                "选择", "zh", null, "to pick or select", "select; choose",
                "选择文本翻译", "sample")
        };

        using var transaction = _connection.BeginTransaction();
        foreach (var item in samples)
        {
            await using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO entries(word, normalized_word, lang, phonetic, definition, translation, example, source)
                VALUES ($word, $normalized, $lang, $phonetic, $definition, $translation, $example, $source);
                """;
            insert.Parameters.AddWithValue("$word", item.Word);
            insert.Parameters.AddWithValue("$normalized", NormalizeWord(item.Word));
            insert.Parameters.AddWithValue("$lang", item.Language);
            insert.Parameters.AddWithValue("$phonetic", (object?)item.Phonetic ?? DBNull.Value);
            insert.Parameters.AddWithValue("$definition", item.Definition);
            insert.Parameters.AddWithValue("$translation", item.Translation);
            insert.Parameters.AddWithValue("$example", (object?)item.Example ?? DBNull.Value);
            insert.Parameters.AddWithValue("$source", item.Source);
            await insert.ExecuteNonQueryAsync();
        }

        transaction.Commit();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    // ---- 文本规范化与 FTS 查询串构造 ----

    // 空白 + 中英文标点都当分隔符：OCR 与复制粘贴常把单词和标点粘连，先切干净再规范化。
    private static readonly char[] TokenSeparators =
        [' ', '\r', '\n', '\t', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\'];

    private static readonly StringSplitOptions TokenSplitOptions =
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries;

    /// <summary>把换行/制表符/连续空格压成单个空格并裁剪两端，用于输入框里的整段选中文本。</summary>
    private static string NormalizeInput(string input)
    {
        var compact = string.Join(' ', input.Split(['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
        return compact.Trim();
    }

    private static string NormalizeWord(string word) => word.Trim().ToLowerInvariant();

    private static string ToFtsQuery(string query)
    {
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Replace("\"", string.Empty))
            .Where(token => token.Length > 0)
            .Take(8)
            .Select(token => $"\"{token}\"*");

        var fts = string.Join(" OR ", tokens);
        return string.IsNullOrWhiteSpace(fts) ? "\"\"" : fts;
    }

    /// <summary>
    /// 把用户输入切成可能命中词典的候选词：先按空白与标点切分，再为中文补 n-gram 子串。
    /// </summary>
    /// <remarks>
    /// 中文没有空格，整句输入（如“截图翻译”）切完只有一个 token，精确查询必然落空；
    /// 因此额外产出 2–4 字子串（2 字覆盖常用词，4 字覆盖成语/短语），长度从长到短输出，
    /// 让较长的短语先被匹配，避免“翻译”抢在“截图翻译”前面。子串总量按 48 封顶，
    /// 否则长句会产出 O(n²) 个候选，每个候选都要走一次查询。
    /// </remarks>
    private static IEnumerable<string> ExtractCandidates(string query)
    {
        foreach (var token in query.Split(TokenSeparators, TokenSplitOptions))
        {
            var normalized = NormalizeWord(token);
            if (normalized.Length is >= 2 and <= 48)
            {
                yield return normalized;
            }
        }

        var cjkText = new string(query.Where(IsCjk).ToArray());
        if (cjkText.Length < 2)
        {
            yield break;
        }

        var emitted = new HashSet<string>();
        for (var length = Math.Min(4, cjkText.Length); length >= 2; length--)
        {
            for (var start = 0; start <= cjkText.Length - length && emitted.Count < 48; start++)
            {
                var candidate = cjkText.Substring(start, length);
                if (emitted.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    // CJK 统一表意文字基本区：㐀 起至 鿿 止（U+3400–U+9FFF），扩展区不含在内。
    private static bool IsCjk(char value) => value is >= '㐀' and <= '鿿';

    private static bool SameEntry(DictionaryEntry left, DictionaryEntry right) =>
        string.Equals(left.Word, right.Word, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Translation, right.Translation, StringComparison.OrdinalIgnoreCase);
}
