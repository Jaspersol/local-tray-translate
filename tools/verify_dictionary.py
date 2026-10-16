#!/usr/bin/env python3
"""Quick sanity checks for a TranslateLite dictionary database."""

from __future__ import annotations

import argparse
import sqlite3
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser(description="Verify basic dictionary queries.")
    parser.add_argument("database", type=Path, nargs="?", default=Path("data/dictionary.db"))
    args = parser.parse_args()

    with sqlite3.connect(args.database) as connection:
        total = connection.execute("SELECT COUNT(*) FROM entries;").fetchone()[0]
        print(f"count: {total}")

        for query in ["hello", "translate", "dictionary"]:
            rows = connection.execute(
                """
                SELECT word, lang, phonetic, substr(translation, 1, 80)
                FROM entries
                WHERE normalized_word = ?
                LIMIT 2;
                """,
                (query,),
            ).fetchall()
            print(f"exact {query}: {rows}")

        for query in ["\u5b66\u4e60", "\u6c49\u8bed", "\u673a\u5668\u7ffb\u8bd1"]:
            rows = connection.execute(
                """
                SELECT word, lang, phonetic, substr(translation, 1, 80)
                FROM entries
                WHERE word = ?
                LIMIT 3;
                """,
                (query,),
            ).fetchall()
            print(f"exact {query}: {rows}")

        for query in ["computer", "translation"]:
            rows = connection.execute(
                """
                SELECT e.word, e.lang, substr(e.translation, 1, 80)
                FROM entries_fts f
                JOIN entries e ON e.id = f.rowid
                WHERE entries_fts MATCH ?
                LIMIT 5;
                """,
                (f'"{query}"*',),
            ).fetchall()
            print(f"fts {query}: {rows}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
