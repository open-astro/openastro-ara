#!/usr/bin/env python3
"""Print the CHANGELOG.md body for one version, for use as GitHub Release notes.

Ported from AlpacaBridge's scripts/changelog_section.py. .github/workflows/release.yml
links a stable Release to this section; the plain-language notes themselves come
from docs/releases/<version>.md (docs/RELEASES.md).

Handles both heading forms scripts/changelog-fragments.py writes: the expanded
top section (``## [X.Y.Z] - YYYY-MM-DD``) and collapsed released sections
(``<summary><strong>[X.Y.Z] - YYYY-MM-DD</strong></summary>`` inside
``<details>``). A section also ends at any other ``## `` heading, because Ara's
pre-release history follows the releases. Fails if the section is missing or still UNRELEASED, so a tag
can never publish notes for an uncut release.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

HEADING_RE = re.compile(r"^## \[([^\]]+)\](?: - (\d{4}-\d{2}-\d{2}|UNRELEASED))?\s*$")
SUMMARY_HEADING_RE = re.compile(
    r"^<summary><strong>\[([^\]]+)\](?: - (\d{4}-\d{2}-\d{2}|UNRELEASED))?\s*</strong></summary>\s*$"
)


def extract(text: str, version: str) -> tuple[str | None, list[str]]:
    """Return (date, body_lines) for ``version``; date is None if not found."""
    date: str | None = None
    body: list[str] = []
    capturing = False
    collapsed = False
    for line in text.splitlines():
        m = HEADING_RE.match(line) or SUMMARY_HEADING_RE.match(line)
        if m:
            if capturing:
                break
            if m.group(1) == version:
                capturing = True
                collapsed = line.startswith("<summary>")
                date = m.group(2) or ""
            continue
        if not capturing:
            continue
        stripped = line.strip()
        # Ara's CHANGELOG.md goes on with "## Pre-release history" after the
        # oldest release: any other '## ' heading ends a section, and so does
        # the </details> of a collapsed one.
        if line.startswith("## ") or (collapsed and stripped.startswith("</details")):
            break
        if stripped.startswith("<details") or stripped.startswith("</details"):
            continue
        body.append(line.rstrip())
    while body and not body[0].strip():
        body.pop(0)
    while body and not body[-1].strip():
        body.pop()
    return date, body


def heading_anchor(version: str, date: str) -> str:
    """GitHub's auto-anchor for ``## [version] - date``.

    GitHub lowercases the heading, drops everything but letters, digits,
    spaces and hyphens, then turns spaces into hyphens: ``## [4.0.0] - 2026-09-17``
    becomes ``400---2026-09-17``. Only a Markdown heading gets an anchor, so the
    link resolves while the section is the expanded top one; once a release is
    collapsed into ``<details>`` it degrades to the top of CHANGELOG.md.
    """
    heading = f"[{version}] - {date}".lower()
    kept = re.sub(r"[^a-z0-9 -]", "", heading)
    return kept.replace(" ", "-")


def self_test() -> int:
    """Pin the slug rule and the extractor; a wrong slug only misplaces a link."""
    cases = {
        ("4.0.0", "2026-09-17"): "400---2026-09-17",
        ("10.2.30", "2027-01-05"): "10230---2027-01-05",
        ("3.5.1", "2026-08-16"): "351---2026-08-16",
    }
    failures = [
        f"heading_anchor{args!r} -> {heading_anchor(*args)!r}, expected {want!r}"
        for args, want in cases.items()
        if heading_anchor(*args) != want
    ]
    sample = (
        "## [2.0.0] - UNRELEASED\n- new\n\n<details>\n"
        "<summary><strong>[1.0.0] - 2026-01-01</strong></summary>\n\n- old\n</details>\n"
    )
    if extract(sample, "2.0.0") != ("UNRELEASED", ["- new"]):
        failures.append("extract() did not read the expanded UNRELEASED section")
    if extract(sample, "1.0.0") != ("2026-01-01", ["- old"]):
        failures.append("extract() did not read the collapsed <details> section")
    history = "## [1.1.0] - 2026-02-01\n- mid\n\n## Pre-release history\n- phase\n"
    if extract(history, "1.1.0") != ("2026-02-01", ["- mid"]):
        failures.append("extract() ran past the section into the pre-release history")
    collapsed_last = "<details>\n<summary><strong>[1.0.0] - 2026-01-01</strong></summary>\n\n- old\n</details>\n\n## Pre-release history\n"
    if extract(collapsed_last, "1.0.0") != ("2026-01-01", ["- old"]):
        failures.append("extract() ran past a collapsed section's </details>")
    if extract(sample, "9.9.9") != (None, []):
        failures.append("extract() reported a section that does not exist")
    for f in failures:
        print(f"SELF-TEST FAIL: {f}", file=sys.stderr)
    print("changelog-section self-test %s." % ("FAILED" if failures else "OK"))
    return 1 if failures else 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("version", nargs="?", help="bare version, e.g. 1.0.0 (no v prefix)")
    parser.add_argument("--changelog", default="CHANGELOG.md", type=Path)
    parser.add_argument(
        "--anchor",
        action="store_true",
        help="print the GitHub heading anchor for the section instead of its body",
    )
    parser.add_argument(
        "--self-test",
        action="store_true",
        help="check heading_anchor() and extract() against known inputs and exit",
    )
    args = parser.parse_args()
    if args.self_test:
        return self_test()
    if args.version is None:
        parser.error("the following arguments are required: version")

    date, body = extract(args.changelog.read_text(encoding="utf-8"), args.version)
    if date is None:
        print(f"ERROR: no CHANGELOG section for [{args.version}]", file=sys.stderr)
        return 1
    if date in ("", "UNRELEASED"):
        print(f"ERROR: CHANGELOG section [{args.version}] is not dated (still UNRELEASED)", file=sys.stderr)
        return 1
    if not body:
        print(f"ERROR: CHANGELOG section [{args.version}] is empty", file=sys.stderr)
        return 1
    if args.anchor:
        print(heading_anchor(args.version, date))
        return 0
    print("\n".join(body))
    return 0


if __name__ == "__main__":
    sys.exit(main())
