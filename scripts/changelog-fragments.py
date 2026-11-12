#!/usr/bin/env python3
"""Validate changelog fragments and assemble them into CHANGELOG.md at release.

A PR adds one file, ``changelog.d/<branch-slug>.md``, instead of editing
CHANGELOG.md, so parallel PRs never conflict on it (#1812). Only the release
step writes CHANGELOG.md. The format is in ``changelog.d/README.md``.

Ported from AlpacaBridge's scripts/changelog_fragments.py (its PR #760),
without the version derivation (--bump), the VERSION file, betas and the
Debian changelog: an Ara release names its version by hand.

  --check                    validate every fragment, and that CHANGELOG.md
                             has no undated section (CI)
  --preview [--version V]    print the section --release would write (no date)
  --release V --date D       write the dated section, collapse the previous
                             release, and delete the fragments
"""

from __future__ import annotations

import argparse
import datetime
import re
import sys
from pathlib import Path

HEADING_RE = re.compile(r"^## \[([^\]]+)\](?: - (\d{4}-\d{2}-\d{2}))?\s*$")
SUMMARY_HEADING_RE = re.compile(r"^<summary><strong>\[([^\]]+)\](?: - (\d{4}-\d{2}-\d{2}))?\s*</strong></summary>\s*$")
NAME_RE = re.compile(r"^[a-z0-9][a-z0-9._-]*\.md$")
# The release tag convention of design/PORT_PLAYBOOK.md §34.5 (v0.0.1-ara.N), without the v.
VERSION_RE = re.compile(r"^\d+\.\d+\.\d+-ara\.[1-9]\d*$")
DATE_RE = re.compile(r"^\d{4}-\d{2}-\d{2}$")
CATEGORY_RE = re.compile(r"^### (.+?)\s*$")
BASE_CATEGORIES = [
    "Breaking changes",
    "Added",
    "Changed",
    "Deprecated",
    "Removed",
    "Fixed",
    "Security",
]
QUALIFIED_RE = re.compile(r"^(%s)(?: \(([^()]+)\))?$" % "|".join(BASE_CATEGORIES))
FENCE_RE = re.compile(r"^(`{3,}|~{3,})(.*)$")
BAD_HEADING_RE = re.compile(r"^#{1,2}\s")


def fence_match(line: str, content_col: int = 2) -> re.Match | None:
    """Match a CommonMark fence line, or None for inline code or an indented code block.

    A backtick fence's info string cannot hold a backtick (CommonMark 4.5), so a line that
    starts with inline code is no fence; a line indented 4+ columns past ``content_col`` (the
    content column of the nearest bullet, 2 for "- text") is an indented code block.
    """
    if len(line) - len(line.lstrip()) >= content_col + 4:
        return None
    m = FENCE_RE.match(line.strip())
    if m and m.group(1)[0] == "`" and "`" in m.group(2):
        return None
    return m


Entries = dict  # category -> list of bullets; a bullet is a list of lines


def category_key(category: str) -> tuple:
    """Sort key: base order, the unqualified form first, then qualifiers by name."""
    m = QUALIFIED_RE.match(category)
    if not m:
        return (len(BASE_CATEGORIES), 0, category)
    return (BASE_CATEGORIES.index(m.group(1)), 1 if m.group(2) else 0, m.group(2) or "")


def parse_body(lines: list[str]) -> tuple[Entries, list[str]]:
    """Split ``### Category`` subsections into bullets; return (entries, problems)."""
    entries: Entries = {}
    problems: list[str] = []
    category: str | None = None
    fence = ""  # the opening fence run (``` or ~~~, any length) while inside a fenced block
    in_fence = False
    pending: list[str] = []  # blank lines seen since the last kept line
    content_col = 2  # content column of the latest bullet line, "- text" at indent n gives n + 2
    for raw in lines:
        line = raw.rstrip()
        if not in_fence:
            m = CATEGORY_RE.match(line)
            if m:
                category = m.group(1)
                entries.setdefault(category, [])
                pending = []
                continue
        if not line.strip():
            pending.append("")
            continue
        if category is None:
            problems.append("text before the first '### <Category>' subsection: %r" % line[:60])
            pending = []
            continue
        if not in_fence and line.startswith("- "):
            entries[category].append([line])
        elif entries[category]:
            # a bullet's own text stays on one line; a nested bullet, a fence or a paragraph after a blank line may follow
            if not in_fence and not pending and not line.lstrip().startswith("- ") and not fence_match(line, content_col):
                problems.append("a bullet is wrapped onto a second line, put it on one line: %r" % line[:60])
            # a blank line before a continuation stays, so a paragraph does not join the bullet's text
            if pending:
                entries[category][-1].extend(pending)
            entries[category][-1].append(line)  # continuation or nested bullet
        else:
            problems.append("'### %s' has text before its first '- ' bullet: %r" % (category, line[:60]))
        pending = []
        if not in_fence and line.lstrip().startswith("- "):
            content_col = len(line) - len(line.lstrip()) + 2
        m = fence_match(line, content_col)
        if m and not in_fence:
            fence, in_fence = m.group(1), True
        elif m and in_fence and m.group(1)[0] == fence[0] and len(m.group(1)) >= len(fence) and not m.group(2):
            fence, in_fence = "", False
    if in_fence:
        problems.append("unclosed code fence %r: close it with a matching fence line" % fence)
    return entries, problems


def validate(name: str, text: str) -> list[str]:
    """Return the rule violations of one fragment (file name and body)."""
    problems: list[str] = []
    if not NAME_RE.match(name):
        hint = ""
        if name != name.lower() and NAME_RE.match(name.lower()):
            hint = ": lowercase the branch slug, expected '%s'" % name.lower()
        problems.append(
            "file name must match [a-z0-9][a-z0-9._-]*.md (the branch name after its last '/', lowercased)" + hint
        )
    lines = text.splitlines()
    for i, line in enumerate(lines, 1):
        if BAD_HEADING_RE.match(line):
            problems.append("line %d: a fragment has no '#' or '##' heading (no version, no date)" % i)
    entries, body_problems = parse_body(lines)
    problems.extend(body_problems)
    if not entries:
        problems.append("no '### <Category>' subsection with a '- ' bullet")
    for category, bullets in entries.items():
        if not QUALIFIED_RE.match(category):
            problems.append(
                "unknown category '### %s' (allowed: %s, optionally followed by '(qualifier)')"
                % (category, ", ".join(BASE_CATEGORIES))
            )
        if not bullets:
            problems.append("'### %s' has no '- ' bullet" % category)
    return problems


def fragment_files(directory: Path) -> list[Path]:
    if not directory.is_dir():
        return []
    return sorted(p for p in directory.iterdir() if p.is_file() and p.name != "README.md" and not p.name.startswith("."))


def problems_of(directory: Path, changelog: Path) -> list[str]:
    """Every fragment problem, plus an undated section a PR wrote into the changelog."""
    out = ["%s: %s" % (p, problem) for p in fragment_files(directory) for problem in validate(p.name, p.read_text(encoding="utf-8"))]
    if changelog.is_file():
        for i, line in enumerate(changelog.read_text(encoding="utf-8").split("\n"), 1):
            m = HEADING_RE.match(line)
            if m and not m.group(2):
                out.append(
                    "%s:%d: an undated '## [%s]' section; put the entries in changelog.d/<branch-slug>.md instead"
                    % (changelog, i, m.group(1))
                )
    return out


def check(directory: Path, changelog: Path) -> int:
    problems = problems_of(directory, changelog)
    if problems:
        print("\n".join(problems), file=sys.stderr)
        print("%d changelog fragment problem(s); see changelog.d/README.md" % len(problems), file=sys.stderr)
        return 1
    print("changelog fragments OK (%d file(s))." % len(fragment_files(directory)))
    return 0


# ---------------------------------------------------------------- CHANGELOG.md


def block_starts(lines: list[str]) -> list[int]:
    """Indexes where a dated release section starts (an expanded heading or its <details>)."""
    starts = []
    for i, line in enumerate(lines):
        m = HEADING_RE.match(line)
        if m and m.group(2):
            starts.append(i)
        elif line.strip().startswith("<details"):
            j = i + 1
            while j < len(lines) and not lines[j].strip():
                j += 1
            if j < len(lines) and SUMMARY_HEADING_RE.match(lines[j]):
                starts.append(i)
    return starts


def collect(directory: Path) -> Entries:
    """The entries of every fragment, merged by category in file-name order."""
    merged: Entries = {}
    for p in fragment_files(directory):
        for category, bullets in parse_body(p.read_text(encoding="utf-8").splitlines())[0].items():
            merged.setdefault(category, []).extend(bullets)
    return merged


def render_section(heading: str, entries: Entries) -> list[str]:
    out = [heading, ""]
    for category in sorted(entries, key=category_key):
        if not entries[category]:
            continue
        out.append("### %s" % category)
        for bullet in entries[category]:
            out.extend(bullet)
        out.append("")
    return out


def collapse(block: list[str]) -> list[str]:
    """Turn an expanded ``## [X] - date`` section into the <details> form."""
    m = HEADING_RE.match(block[0])
    assert m, block[0]  # the caller passes a block that starts at an expanded heading
    body = block[1:]
    while body and not body[0].strip():
        body.pop(0)
    while body and not body[-1].strip():
        body.pop()
    label = "[%s] - %s" % (m.group(1), m.group(2))
    return ["<details>", "<summary><strong>%s</strong></summary>" % label, ""] + body + ["", "</details>", ""]


def assemble(text: str, directory: Path, version: str, date: str) -> str:
    """Return the new CHANGELOG.md text (pure; the caller deletes the fragments)."""
    if not VERSION_RE.match(version):
        raise SystemExit("ERROR: %r is not an Ara release version X.Y.Z-ara.N" % version)
    if not DATE_RE.match(date):
        raise SystemExit("ERROR: %r is not a YYYY-MM-DD date" % date)
    try:
        datetime.date.fromisoformat(date)
    except ValueError as e:
        raise SystemExit("ERROR: %r is not a real date (%s)" % (date, e))
    lines = text.split("\n")
    for line in lines:
        m = HEADING_RE.match(line) or SUMMARY_HEADING_RE.match(line)
        if m and m.group(1) == version:
            raise SystemExit("ERROR: %s is already released in the changelog" % version)
    entries = collect(directory)
    if not any(entries.values()):
        raise SystemExit("ERROR: nothing to release: no fragments")
    starts = block_starts(lines)
    if starts:
        first = starts[0]
        if HEADING_RE.match(lines[first]):
            # The section ends at the next release or at any other '## ' section (the pre-release history).
            end = next((i for i in range(first + 1, len(lines)) if i in starts or lines[i].startswith("## ")), len(lines))
            lines[first:end] = collapse(lines[first:end])
    else:
        # The first release goes above the first '## ' heading (the pre-release history).
        first = next((i for i, line in enumerate(lines) if line.startswith("## ")), len(lines))
    lines[first:first] = render_section("## [%s] - %s" % (version, date), entries)
    return "\n".join(lines)


def main() -> int:
    ap = argparse.ArgumentParser(description="Validate changelog fragments and assemble them into CHANGELOG.md at release.", epilog="Fragment format: changelog.d/README.md.")
    mode = ap.add_mutually_exclusive_group(required=True)
    mode.add_argument("--check", action="store_true", help="validate every fragment (CI)")
    mode.add_argument("--preview", action="store_true", help="print the section --release would write, without a date")
    mode.add_argument(
        "--release", metavar="X.Y.Z-ara.N", help="write the dated section to the changelog and delete the fragments (needs --date)"
    )
    ap.add_argument("--changelog", default="CHANGELOG.md", type=Path, metavar="PATH", help="changelog file (default: %(default)s)")
    ap.add_argument("--fragments", default="changelog.d", type=Path, metavar="DIR", help="fragment directory (default: %(default)s)")
    ap.add_argument("--version", metavar="X.Y.Z-ara.N", help="with --preview: version for the heading (default: Unreleased)")
    ap.add_argument("--date", metavar="YYYY-MM-DD", help="with --release: release date (required)")
    args = ap.parse_args()

    if args.check:
        return check(args.fragments, args.changelog)

    problems = problems_of(args.fragments, args.changelog)
    if problems:
        print("\n".join(problems), file=sys.stderr)
        print("ERROR: fix the fragments first (--check)", file=sys.stderr)
        return 1
    if args.preview:
        entries = collect(args.fragments)
        if not any(entries.values()):
            print("ERROR: nothing to release: no fragments", file=sys.stderr)
            return 1
        print("\n".join(render_section("## [%s]" % (args.version or "Unreleased"), entries)).rstrip())
        return 0

    if not args.date:
        ap.error("--release requires --date YYYY-MM-DD")
    new = assemble(args.changelog.read_text(encoding="utf-8"), args.fragments, args.release, args.date)
    args.changelog.write_text(new, encoding="utf-8")
    for p in fragment_files(args.fragments):
        p.unlink()
    print("Wrote [%s] - %s to %s" % (args.release, args.date, args.changelog))
    return 0


if __name__ == "__main__":
    sys.exit(main())
