#!/usr/bin/env python3
"""Warn when the bundled IERS DUT1 snapshot is close to (or past) its end (#1232).

Reads the `# span=YYYY-MM-DD..YYYY-MM-DD (...)` header of
OpenAstroAra.Astrometry/External/iers-dut1.tsv and prints a GitHub Actions
`::warning::` when the end date is within --days of today, or already past.
Exit code is always 0: a stale table only extrapolates (within a few
hundredths of a second), so this is a release reminder, not a gate.

Usage: python3 scripts/check-dut1-freshness.py [--table PATH] [--days 90] [--today YYYY-MM-DD]
"""
from __future__ import annotations

import argparse
import datetime as dt
import re
import sys
from pathlib import Path

DEFAULT_TABLE = Path(__file__).resolve().parent.parent / "OpenAstroAra.Astrometry" / "External" / "iers-dut1.tsv"
SPAN_RE = re.compile(r"^#\s*span=(\d{4}-\d{2}-\d{2})\.\.(\d{4}-\d{2}-\d{2})")


def span_end(text: str) -> dt.date | None:
    for line in text.splitlines():
        m = SPAN_RE.match(line)
        if m:
            return dt.date.fromisoformat(m.group(2))
        if line and not line.startswith("#"):
            break
    return None


def verdict(end: dt.date | None, today: dt.date, days: int) -> tuple[str, str]:
    """Returns (level, message) with level in {"notice", "warning"}."""
    if end is None:
        return "warning", "iers-dut1.tsv has no '# span=' header; regenerate it with scripts/update-dut1-table.py"
    left = (end - today).days
    hint = " Regenerate with scripts/update-dut1-table.py before the next release."
    if left < 0:
        return "warning", f"bundled IERS DUT1 table ended {-left} day(s) ago ({end}); the daemon is extrapolating.{hint}"
    if left <= days:
        return "warning", f"bundled IERS DUT1 table ends in {left} day(s) ({end}).{hint}"
    return "notice", f"bundled IERS DUT1 table ends {end} ({left} days away)"


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--table", type=Path, default=DEFAULT_TABLE)
    ap.add_argument("--days", type=int, default=90)
    ap.add_argument("--today", type=dt.date.fromisoformat, default=dt.datetime.now(dt.timezone.utc).date())
    args = ap.parse_args(argv)
    try:
        text = args.table.read_text(encoding="utf-8")
    except OSError as ex:
        print(f"::warning::cannot read {args.table}: {ex}")
        return 0
    level, message = verdict(span_end(text), args.today, args.days)
    print(f"::{level}::{message}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
