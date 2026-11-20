#!/usr/bin/env python3
"""Export legacy or monthly wheel JSON to CSV for a given calendar month.
Usage: python export-wheel.py wheel-entries.json 2026-11
"""
import csv
import json
import re
import sys
from pathlib import Path

if len(sys.argv) != 3 or not re.fullmatch(r"\d{4}-(0[1-9]|1[0-2])", sys.argv[2]):
    sys.exit("Usage: python export-wheel.py wheel-entries.json YYYY-MM")
source = Path(sys.argv[1])
month = sys.argv[2]
entries = json.loads(source.read_text(encoding="utf-8-sig"))
if not isinstance(entries, list):
    sys.exit("Expected a JSON array of wheel entries")
output = Path(f"wheel-{month}.csv")
with output.open("w", encoding="utf-8-sig", newline="") as f:
    writer = csv.DictWriter(f, fieldnames=["game", "user", "date"])
    writer.writeheader()
    for entry in entries:
        if entry.get("month") == month and not entry.get("removed", False):
            writer.writerow({k: entry.get(k, "") for k in ("game", "user", "date")})
print(f"Exported {output}")
