#!/usr/bin/env python3
"""Build the TranslateLite SQLite dictionary from ECDICT and CC-CEDICT files."""

from __future__ import annotations

import argparse
import csv
import gzip
import re
import sqlite3
from pathlib import Path


SCHEMA = """
PRAGMA journal_mode=OFF;
PRAGMA synchronous=OFF;
PRAGMA temp_store=MEMORY;

DROP TABLE IF EXISTS entries_fts;
DROP TABLE IF EXISTS entries;

CREATE TABLE entries (
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

CREATE INDEX idx_entries_normalized_word ON entries(normalized_word);
CREATE INDEX idx_entries_lang ON entries(lang);

CREATE VIRTUAL TABLE entries_fts USING fts5(
    word,
    translation,
    definition,
    content='entries',
    content_rowid='id'
);
"""

INSERT_SQL = """
INSERT INTO entries(word, normalized_word, lang, phonetic, definition, translation, example, source)
VALUES (?, ?, ?, ?, ?, ?, ?, ?);
"""

CC_CEDICT_LINE = re.compile(r"^([^\s]+)\s+([^\s]+)\s+\[([^\]]*)\]\s+/(.*)/$")


def normalize_word(value: str) -> str:
    return value.strip().lower()


def clean(value: object | None) -> str:
    if value is None:
        return ""
    return str(value).replace("\r\n", "\n").replace("\r", "\n").strip()


def open_text(path: Path):
    if path.suffix.lower() == ".gz":
        return gzip.open(path, "rt", encoding="utf-8", errors="replace", newline="")
    return path.open("r", encoding="utf-8", errors="replace", newline="")


def import_ecdict(connection: sqlite3.Connection, path: Path, batch_size: int) -> int:
    if not path.exists():
        return 0

    count = 0
    batch: list[tuple[str, str, str, str | None, str, str, str | None, str]] = []

    with open_text(path) as handle:
        reader = csv.DictReader(handle)
        for row in reader:
            word = clean(row.get("word"))
            if not word:
                continue

            phonetic = clean(row.get("phonetic")) or None
            definition = clean(row.get("definition"))
            translation = clean(row.get("translation"))

            if not definition and not translation:
                continue

            batch.append((
                word,
                normalize_word(word),
                "en",
                phonetic,
                definition,
                translation,
                # ECDICT 没有例句字段：exchange 是词形变化代码（s:a-bombs），collins/bnc 是词频排名（0、1、2），
                # 都不是句子。写进 example 只会在界面上显示成“0”，宁可为空。
                None,
                "ecdict",
            ))

            if len(batch) >= batch_size:
                connection.executemany(INSERT_SQL, batch)
                count += len(batch)
                batch.clear()

    if batch:
        connection.executemany(INSERT_SQL, batch)
        count += len(batch)

    return count


def import_cc_cedict(connection: sqlite3.Connection, path: Path, batch_size: int) -> int:
    if not path.exists():
        return 0

    count = 0
    seen: set[tuple[str, str]] = set()
    batch: list[tuple[str, str, str, str | None, str, str, str | None, str]] = []

    with open_text(path) as handle:
        for line in handle:
            line = line.strip()
            if not line or line.startswith("#"):
                continue

            match = CC_CEDICT_LINE.match(line)
            if not match:
                continue

            traditional, simplified, pinyin, definitions = match.groups()
            translation = "; ".join(part.strip() for part in definitions.split("/") if part.strip())
            if not translation:
                continue

            for word in dict.fromkeys([simplified, traditional]):
                key = (word, translation)
                if not word or key in seen:
                    continue
                seen.add(key)
                batch.append((
                    word,
                    normalize_word(word),
                    "zh",
                    clean(pinyin) or None,
                    "",
                    translation,
                    None,
                    "cc-cedict",
                ))

                if len(batch) >= batch_size:
                    connection.executemany(INSERT_SQL, batch)
                    count += len(batch)
                    batch.clear()

    if batch:
        connection.executemany(INSERT_SQL, batch)
        count += len(batch)

    return count


def rebuild_fts(connection: sqlite3.Connection) -> None:
    connection.execute("INSERT INTO entries_fts(entries_fts) VALUES('rebuild');")
    connection.execute("PRAGMA optimize;")


def main() -> int:
    parser = argparse.ArgumentParser(description="Import ECDICT and CC-CEDICT into TranslateLite SQLite format.")
    parser.add_argument("--ecdict", type=Path, default=Path("data/raw/ecdict.csv"))
    parser.add_argument("--cedict", type=Path, default=Path("data/raw/cedict_ts.u8"))
    parser.add_argument("--output", type=Path, default=Path("data/dictionary.db"))
    parser.add_argument("--batch-size", type=int, default=5000)
    args = parser.parse_args()

    args.output.parent.mkdir(parents=True, exist_ok=True)
    if args.output.exists():
        args.output.unlink()

    with sqlite3.connect(args.output) as connection:
        connection.executescript(SCHEMA)
        with connection:
            ecdict_count = import_ecdict(connection, args.ecdict, args.batch_size)
            cedict_count = import_cc_cedict(connection, args.cedict, args.batch_size)
        rebuild_fts(connection)
        total = connection.execute("SELECT COUNT(*) FROM entries;").fetchone()[0]

    print(f"Imported ECDICT entries: {ecdict_count}")
    print(f"Imported CC-CEDICT entries: {cedict_count}")
    print(f"Total entries: {total}")
    print(f"Output: {args.output.resolve()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
