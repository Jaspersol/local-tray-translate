using System.IO;
using System.Text;
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

        await MigrateLegacyKeysAsync();
    }

    // ---- 一次性迁移：历史数据把未转小写的词形写进了 normalized_word，而查询一律用小写比较 ----

    // 旧版导入脚本的 CC-CEDICT 分支直接写 word 列的原文（如 "A片"），旧版用户词条也只在 AddUserEntryAsync
    // 里做了 NormalizeInput。这些行按小写查询永远命中不了，启动时统一压成 lower(trim()) 一次。
    // 版本号存在库文件自己的 user_version 里：词典被新版覆盖后（user_version 回到 0）迁移会再跑一次。
    private const int KeyNormalizationVersion = 1;

    private async Task MigrateLegacyKeysAsync()
    {
        // 内置词典由 Python 导入脚本生成，全是大写只可能来自旧版 CC-CEDICT 分支；
        // SQLite 的 lower() 只覆盖 ASCII，对生成数据足够，且比把 90 万行读进内存再比较便宜。
        if (await GetUserVersionAsync(_connection) < KeyNormalizationVersion)
        {
            await ExecuteAsync(
                "UPDATE entries SET normalized_word = lower(trim(normalized_word)) " +
                "WHERE normalized_word != lower(trim(normalized_word));");
            await ExecuteAsync($"PRAGMA user_version = {KeyNormalizationVersion};");
        }

        // 用户词条是自己敲的，可能含非 ASCII 大写（西里尔、希腊字母等），必须用 ToLowerInvariant
        // 而不是 SQL 的 lower()（只转 ASCII），否则迁移后仍然查不到。
        if (await GetUserVersionAsync(_userConnection) < KeyNormalizationVersion)
        {
            var pending = new List<(long Id, string Normalized)>();
            await using (var select = _userConnection.CreateCommand())
            {
                select.CommandText = "SELECT id, normalized_word FROM user_entries;";
                await using var reader = await select.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var id = reader.GetInt64(0);
                    var stored = reader.GetString(1);
                    var expected = NormalizeWord(stored);
                    if (!string.Equals(stored, expected, StringComparison.Ordinal))
                    {
                        pending.Add((id, expected));
                    }
                }
            }

            if (pending.Count > 0)
            {
                using var transaction = _userConnection.BeginTransaction();
                foreach (var (id, normalized) in pending)
                {
                    await using var update = _userConnection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE user_entries SET normalized_word = $normalized WHERE id = $id;";
                    update.Parameters.AddWithValue("$normalized", normalized);
                    update.Parameters.AddWithValue("$id", id);
                    await update.ExecuteNonQueryAsync();
                }

                transaction.Commit();
            }

            await ExecuteOnUserAsync($"PRAGMA user_version = {KeyNormalizationVersion};");
        }
    }

    private static async Task<long> GetUserVersionAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(await command.ExecuteScalarAsync() ?? 0L);
    }

    // ---- 用户词条 CRUD（独立库，加载时优先于内置词典命中） ----

    /// <summary>新增一条用户词条，返回新记录的 id。语言留空时按内容自动判定。</summary>
    /// <exception cref="ArgumentException"><paramref name="word"/> 规范化后为空。</exception>
    public async Task<int> AddUserEntryAsync(string word, string language, string? phonetic,
        string definition, string translation, string? example, string? note, IReadOnlyCollection<string>? tags)
    {
        var normalized = NormalizeWord(word);
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
        var normalized = NormalizeWord(word);
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
    /// 从文本文件导入用户词条（word / translation / 可选 definition / 可选 example，'#' 开头为注释）。
    /// 制表符与逗号两种分隔符都接受：按首个有效行检测，逗号文件按带引号的 CSV 规则切分。
    /// </summary>
    /// <exception cref="InvalidDataException">文件里没有一行符合格式。</exception>
    public async Task ImportUserEntriesAsync(string filePath)
    {
        var lines = await File.ReadAllLinesAsync(filePath);
        var delimiter = DetectDelimiter(lines);
        var imported = 0;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = SplitDelimited(line, delimiter);
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
            throw new InvalidDataException(
                "文件中未找到有效的词条行（格式：word<TAB>translation[<TAB>definition[<TAB>example]]，或同结构的逗号分隔 CSV）。");
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
        // 一次性统一到小写：后面每一层（用户词典、精确、FTS、编辑距离）都用这个值比较，
        // 混用原文与实际只转小写的词形，会让含大写字母的词条在某些层命中、在另一些层落空。
        var query = NormalizeWord(input);
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

        var budget = EditDistanceBudget(normalized);
        var results = new List<DictionaryEntry>();
        // 距离只算一次：排序键和阈值判断用同一个值，避免同一候选被算两遍 O(n*m)。
        foreach (var (candidate, _) in candidates
                     .Where(candidate => candidate.Length is >= 2 and <= 48)
                     .Select(candidate => (candidate, distance: OsaDistance(normalized, candidate)))
                     .Where(item => item.distance <= budget)
                     .OrderBy(item => item.distance)
                     .ThenBy(item => item.candidate.Length))
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

        // 三行滚动：previous = i-1 行，current = i 行，twoRowsBack = i-2 行（换位要用到 i-2 行）。
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

            // 轮换三行：i-1 行退成 i-2 行，i 行成为下一轮的 i-1 行，旧 i-2 行被回收作暂存。
            // 三个变量必须指向三块不同的数组，任何一次交换让 previous 与 current 别名相同，
            // 下一行就会在刚写好的行上原地覆盖，距离结果会系统性偏大。
            (twoRowsBack, previous, current) = (previous, current, twoRowsBack);
        }

        // 循环结束时 previous 是最后一行（source.Length 行），答案在它的最后一列。
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
        // 唯一比较键是 normalized_word：写入与查询都经过 NormalizeWord，键上还有索引；
        // 再加 `OR word = ...` 会让 SQLite 放弃索引去全表扫描（entries 约 90 万行），因此不再用 word 列兜底。
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT word, lang, phonetic, definition, translation, example, source
            FROM entries
            WHERE normalized_word = $query
            ORDER BY length(word)
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$query", NormalizeWord(query));
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

    /// <summary>
    /// 词典键的唯一规范化：折叠空白并转小写。写库（normalized_word）与查询都必须走这里，
    /// 否则含大写、换行或连续空格的词条会写入一套键、用另一套键去查。
    /// </summary>
    private static string NormalizeWord(string word) => NormalizeInput(word).ToLowerInvariant();

    /// <summary>
    /// 判断导入文件的字段分隔符：首个有效行含制表符就是 TSV，否则按 CSV 处理。
    /// 导入对话框同时列出 .tsv/.txt/.csv，两种格式都要能真正读进来。
    /// </summary>
    private static char DetectDelimiter(IEnumerable<string> lines)
    {
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            return line.Contains('\t') ? '\t' : ',';
        }

        return '\t';
    }

    /// <summary>
    /// 按分隔符切分一行：制表符直接切；逗号按 RFC 4180 处理双引号包裹的字段（"a,b" 视为一个字段，"" 表示一个引号）。
    /// </summary>
    private static string[] SplitDelimited(string line, char delimiter)
    {
        if (delimiter == '\t')
        {
            return line.Split('\t');
        }

        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }
            }
            else if (ch == '"' && field.Length == 0)
            {
                // 只有字段开头的引号才开启引用区；词里出现的 5" 之类的引号按普通字符处理。
                inQuotes = true;
            }
            else if (ch == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(ch);
            }
        }

        fields.Add(field.ToString());
        return [.. fields];
    }

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
