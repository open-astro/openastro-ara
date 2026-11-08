#!/usr/bin/env python3
"""Hold every decision record in docs/adr/ to the shape docs/adr/README.md describes.

Status line, sections in order, numbered rule lines with `Scope:` globs that
match a tracked file and a `Checked by:`, no carve-out words, How to reverse
in every record, and an index that lists every record once. The grammar lives
in adr_grammar.py. Runs in the CI Sanity job.

Usage:
    check-adr-shape.py [--root DIR]
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import adr_grammar as g  # noqa: E402


def main(argv):
    root = g.ROOT
    if argv[:1] == ["--root"] and len(argv) == 2:
        root = Path(argv[1])
    elif argv:
        print("usage: check-adr-shape.py [--root DIR]", file=sys.stderr)
        return 2
    records = g.read_records(root)
    if not records:
        print("adr-shape: %s holds no decision record" % g.DECISION_DIR)
        return 1
    readme_path = Path(root) / g.DECISION_DIR / "README.md"
    readme = readme_path.read_text(encoding="utf-8", errors="replace") if readme_path.exists() else ""
    findings = g.record_findings(records, readme, g.tracked_paths(root))
    for f in findings:
        print("adr-shape: " + f)
    if findings:
        print("adr-shape: FAIL (%d problem(s)); see %s/README.md" % (len(findings), g.DECISION_DIR))
        return 1
    print("adr-shape: OK (%d record(s))" % len(records))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
