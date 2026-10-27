#!/usr/bin/env python3
"""Generate shared key/value histories using SQLite, without importing Storage code."""
# Copyright (c) 2026 Pierre G. Boutquin. Licensed under Apache-2.0.
import argparse
import json
from pathlib import Path
import random
import sqlite3


def generate():
    histories = []
    for seed in (17, 73, 2026):
        rng = random.Random(seed)
        with sqlite3.connect(":memory:") as db:
            db.execute("CREATE TABLE kv (key INTEGER PRIMARY KEY, value TEXT NOT NULL)")
            steps = []
            for index in range(96):
                key = rng.randrange(1, 25)
                operation = "delete" if rng.randrange(4) == 0 else "put"
                value = f"s{seed}-i{index}-" + (chr(97 + key % 26) * rng.choice((1, 17, 140)))
                if operation == "delete":
                    db.execute("DELETE FROM kv WHERE key = ?", (key,))
                else:
                    db.execute("INSERT INTO kv VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", (key, value))
                db.commit()
                # Ordered state is computed by SQLite after each committed mutation.
                state = [{"key": k, "value": v} for k, v in db.execute("SELECT key, value FROM kv ORDER BY key")]
                steps.append({"operation": operation, "key": key, "value": value if operation == "put" else None, "expected": state})
            histories.append({"seed": seed, "steps": steps})
    return {"schema": 1, "oracle": "SQLite", "histories": histories}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Compare against the committed fixture without writing")
    args = parser.parse_args()
    destination = Path(__file__).resolve().parents[1] / "tests/Infrastructure/OssComparison/sqlite-kv-histories.json"
    payload = json.dumps(generate(), indent=2, ensure_ascii=True) + "\n"
    if args.check:
        if destination.read_text() != payload:
            raise SystemExit("SQLite fixture differs; regenerate and review it")
    else:
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(payload)
    print(f"SQLite {sqlite3.sqlite_version}: 3 seeds, 288 committed states; {'checked' if args.check else 'generated'} {destination}")


if __name__ == "__main__":
    main()
