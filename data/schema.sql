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

CREATE VIRTUAL TABLE IF NOT EXISTS entries_fts USING fts5(
    word,
    translation,
    definition,
    content='entries',
    content_rowid='id'
);

CREATE INDEX IF NOT EXISTS idx_entries_normalized_word ON entries(normalized_word);
CREATE INDEX IF NOT EXISTS idx_entries_lang ON entries(lang);
