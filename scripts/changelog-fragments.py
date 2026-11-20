#!/usr/bin/env python3
"""Validate changelog fragments and assemble them into CHANGELOG.md at release.

A PR adds one file, ``changelog.d/<branch-slug>.md``, instead of editing
CHANGELOG.md, so parallel PRs never conflict on it (#1812). Only the release
step writes CHANGELOG.md. The format is in ``changelog.d/README.md``.

Ported from AlpacaBridge's scripts/changelog_fragments.py (its PR #760),
without its legacy UNRELEASED section and its Debian changelog. The VERSION
spellings and the release process are in docs/RELEASES.md; the patterns live in
scripts/version.py.

  --check                    validate every fragment, and that CHANGELOG.md
                             has no undated section (CI)
  --preview [--version V]    print the section --release would write (no date;
                             the heading defaults to the --bump result)
  --bump [--version V]       print the proposed next version: major for
                             Breaking changes, minor for an unqualified Added,
                             else patch, from the newest dated release (none
                             yet: from a stable VERSION, else 0.0.0); the
                             current VERSION (or V) is a floor, a beta counts
                             as its base version
  --is-beta V                exit 0 if V is X.Y.Z~betaN, 1 if X.Y.Z, 2 otherwise
  --release X.Y.Z --date D   write the dated section, collapse the previous
                             release, and delete the fragments (a bare X.Y.Z
                             only: a beta never consumes the fragments)
  --self-test                run the built-in cases
"""

from __future__ import annotations

import argparse
import contextlib
import datetime
import io
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from version import (  # noqa: E402  (scripts/version.py, the one spelling of a VERSION)
    BARE_PATTERN,
    BARE_RE,
    BETA_RE,
    BETA_SUFFIX_PATTERN,
    VERSION_PATTERN,
    VERSION_RE,
    base_version,
    is_beta,
    version_tuple,
)

HEADING_RE = re.compile(r"^## \[([^\]]+)\](?: - (\d{4}-\d{2}-\d{2}))?\s*$")
SUMMARY_HEADING_RE = re.compile(r"^<summary><strong>\[([^\]]+)\](?: - (\d{4}-\d{2}-\d{2}))?\s*</strong></summary>\s*$")
NAME_RE = re.compile(r"^[a-z0-9][a-z0-9._-]*\.md$")
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


def section_headers(lines: list[str]) -> list[tuple[str, str | None]]:
    out = []
    for line in lines:
        m = HEADING_RE.match(line) or SUMMARY_HEADING_RE.match(line)
        if m:
            out.append((m.group(1), m.group(2)))
    return out


def latest_released(lines: list[str]) -> str | None:
    """The highest dated bare X.Y.Z release in the changelog, or None before the first release."""
    dated = [v for v, d in section_headers(lines) if d and BARE_RE.match(v)]
    return max(dated, key=version_tuple) if dated else None


def bump_from(base: str, entries: Entries) -> tuple:
    """AlpacaBridge's rule, the same before 1.0: Breaking -> major, unqualified Added -> minor, else patch."""
    major, minor, patch = version_tuple(base)
    if any(c == "Breaking changes" or c.startswith("Breaking changes (") for c in entries):
        return (major + 1, 0, 0)
    if "Added" in entries:
        return (major, minor + 1, 0)
    return (major, minor, patch + 1)


def propose_bump(lines: list[str], directory: Path, current: str | None = None) -> str:
    """The next version the fragments call for, never below `current` (a beta counts as its base).

    The start is the newest dated release. Before the first one it is a stable
    `current` (master's VERSION names the last stable version), else 0.0.0.
    """
    base = latest_released(lines)
    if base is None:
        base = current if current and BARE_RE.match(current) else "0.0.0"
    proposed = bump_from(base, collect(directory))
    if current:
        proposed = max(proposed, version_tuple(current))
    return ".".join(str(x) for x in proposed)


def beta_bump_warning(current: str | None, branch: str | None) -> str | None:
    """Why a --bump with a beta VERSION on a branch other than stable/X.Y is suspect.

    Only stable/X.Y (and the release/ PR branch cut from it) carries a beta
    VERSION: a merge down keeps the receiving branch's VERSION and badge
    (docs/RELEASES.md), so a beta VERSION anywhere else means one was not kept.
    None when the VERSION is not a beta, the branch is a stable branch, or it is unknown.
    """
    if (not current or not is_beta(current) or branch in (None, "HEAD")
            or branch.startswith(("stable/", "release/"))):
        return None  # "HEAD": a detached checkout (CI); release/: the PR branch /bump-release cuts
    return ("WARNING: VERSION %s is a beta but this is branch %r, not stable/X.Y: the proposed version belongs "
            "to the stable branch, run /bump-release there (docs/RELEASES.md)" % (current, branch))


def current_branch() -> str | None:
    try:
        r = subprocess.run(["git", "rev-parse", "--abbrev-ref", "HEAD"], capture_output=True, text=True, check=False)
    except OSError:
        return None
    return (r.stdout.strip() or None) if r.returncode == 0 else None


def default_version_floor(changelog: Path) -> str | None:
    """The VERSION file beside the changelog, the floor --bump and --preview use without --version."""
    version_file = changelog.resolve().parent / "VERSION"
    if not version_file.is_file():
        return None
    text = version_file.read_text(encoding="utf-8").strip()
    if not VERSION_RE.match(text):
        # Say so: a silently dropped floor proposes a lower version than the one VERSION names.
        print("WARNING: %s holds %r, which is not X.Y.Z or X.Y.Z~betaN; it is not used as the version floor"
              % (version_file, text), file=sys.stderr)
        return None
    return text


def assemble(text: str, directory: Path, version: str, date: str) -> str:
    """Return the new CHANGELOG.md text (pure; the caller deletes the fragments)."""
    # A beta never consumes the fragments (docs/RELEASES.md): /bump-release beta
    # mode skips this step, and stable mode writes VERSION X.Y.Z first.
    if not BARE_RE.match(version):
        raise SystemExit(
            "ERROR: %r is not a bare X.Y.Z version (a beta is not released this way, and the X.Y.Z-ara.N form is retired)"
            % version
        )
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
    latest = latest_released(lines)
    if latest and version_tuple(version) <= version_tuple(latest):
        raise SystemExit("ERROR: %s is not greater than the latest dated release %s" % (version, latest))
    entries = collect(directory)
    if not any(entries.values()):
        raise SystemExit("ERROR: nothing to release: no fragments")
    proposed = propose_bump(lines, directory, None)
    if version_tuple(version) < version_tuple(proposed):
        raise SystemExit("ERROR: %s is below the proposed bump %s for these fragments" % (version, proposed))
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


# ------------------------------------------------------------------- self-test

FIXTURE = """# Changelog

Intro paragraph.

---

## [1.2.0] - 2026-01-02

### Added
- **Older** (#0)

<details>
<summary><strong>[1.1.0] - 2026-01-01</strong></summary>

### Added
- **Oldest** (#0)

</details>

## Pre-release history (backfilled 2026-05-26)

### [phase-1-complete] - 2026-05-01
"""


def _write(d: Path, name: str, body: str) -> None:
    d.mkdir(exist_ok=True)
    (d / name).write_text(body, encoding="utf-8")


def self_test() -> int:
    failures: list[str] = []

    def expect(cond: bool, msg: str) -> None:
        if not cond:
            failures.append(msg)

    here = Path(__file__).resolve()
    good = "### Fixed\n- **x** (#1)\n\n### Added (tests)\n- **y**\n  - more\n"
    expect(validate("a-b.c-d.md", good) == [], "valid fragment rejected")
    expect(any("unknown category" in p for p in validate("a.md", "### Stuff\n- x\n")), "unknown category accepted")
    expect(any("wrapped" in p for p in validate("a.md", "### Fixed\n- **x**: one\n  two\n")), "wrapped bullet accepted")
    expect(any("heading" in p for p in validate("a.md", "## [1.0.0] - x\n### Fixed\n- x\n")), "## heading accepted")

    expect(VERSION_RE.match("1.0.0~beta2") and not VERSION_RE.match("1.0.0~rc1")
           and not VERSION_RE.match("0.0.1-ara.1") and not VERSION_RE.match("1.0.0~beta0"),
           "VERSION_RE does not accept exactly X.Y.Z[~betaN]")
    expect(re.compile(VERSION_PATTERN).groups == 0 and re.compile(BETA_SUFFIX_PATTERN).groups == 0
           and re.compile(BARE_PATTERN).groups == 0 and BETA_RE.groups == 0, "a VERSION pattern gained a group")
    expect(base_version("1.0.0~beta2") == "1.0.0" and is_beta("1.0.0~beta2") and not is_beta("1.0.0"), "beta helpers wrong")

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        d = root / "changelog.d"
        d.mkdir()
        (d / "README.md").write_text("# readme\n", encoding="utf-8")
        lines = FIXTURE.split("\n")
        expect(latest_released(lines) == "1.2.0", "latest dated release not 1.2.0")
        expect(latest_released(["# Changelog"]) is None, "a changelog with no release named one")
        # bump rules
        for body, want, why in (
            ("### Fixed\n- x\n", "1.2.1", "Fixed must count as patch"),
            ("### Added (tests)\n- x\n", "1.2.1", "Added (tests) must count as patch"),
            ("### Added\n- x\n", "1.3.0", "Added must count as minor"),
            ("### Breaking changes\n- x\n", "2.0.0", "Breaking changes must count as major"),
            ("### Breaking changes (api)\n- x\n", "2.0.0", "qualified Breaking changes must count as major"),
        ):
            _write(d, "t.md", body)
            expect(propose_bump(lines, d) == want, why)
        _write(d, "t.md", "### Fixed\n- x\n")
        expect(propose_bump(lines, d, "1.5.0~beta2") == "1.5.0", "a beta VERSION floor is not its base")
        expect(propose_bump(lines, d, "1.0.0~beta1") == "1.2.1", "a lower beta base lowered the bump")
        expect(propose_bump(lines, d, "1.4.0") == "1.4.0", "a stable VERSION is not a floor")
        # before the first release: a stable VERSION is the last stable; pre-1.0 uses the same rules
        none = ["# Changelog", "", "## Pre-release history"]
        expect(propose_bump(none, d, "0.0.1") == "0.0.2", "Fixed from VERSION 0.0.1 with no release is not 0.0.2")
        _write(d, "t.md", "### Added\n- x\n")
        expect(propose_bump(none, d, "0.0.1") == "0.1.0", "Added before 1.0 is not a minor")
        _write(d, "t.md", "### Breaking changes\n- x\n")
        expect(propose_bump(none, d, "0.0.1") == "1.0.0", "Breaking before 1.0 is not a major")
        expect(propose_bump(none, d, "1.0.0~beta1") == "1.0.0", "a first beta does not floor at its base")
        expect(propose_bump(none, d, None) == "1.0.0", "Breaking from nothing is not 1.0.0")
        _write(d, "t.md", "### Fixed\n- x\n")
        expect(propose_bump(none, d, None) == "0.0.1", "Fixed from nothing is not 0.0.1")
        (d / "t.md").unlink()
        expect(propose_bump(lines, d) == "1.2.1", "no fragments is not a patch")

        expect(beta_bump_warning("1.0.0~beta2", "master") is not None, "a beta VERSION on master did not warn")
        expect(beta_bump_warning("1.0.0~beta2", "stable/1.0") is None and beta_bump_warning("1.0.0", "master") is None
               and beta_bump_warning("1.0.0~beta2", "HEAD") is None
               and beta_bump_warning("1.0.0~beta2", "release/1.0.0-beta.2") is None,
               "beta_bump_warning warned where it should not")

        # release
        _write(d, "b-second.md", "### Fixed\n- **Second fix** (#3)\n\n### Security\n- **Sec** (#3)\n")
        _write(d, "a-first.md", "### Added\n- **First add** (#2)\n\n### Fixed\n- **First fix** (#2)\n")
        new = assemble(FIXTURE, d, "1.3.0", "2026-02-03")
        out = new.split("\n")
        expect("## [1.3.0] - 2026-02-03" in out, "new dated heading missing")
        expect("<summary><strong>[1.2.0] - 2026-01-02</strong></summary>" in out and "## [1.2.0] - 2026-01-02" not in out,
               "previous release not collapsed")
        expect(new.index("## [1.3.0]") < new.index("[1.2.0]") < new.index("[1.1.0]") < new.index("Pre-release history"),
               "sections out of order")
        sect = new[new.index("## [1.3.0]"):new.index("<details>")]
        expect(sect.index("First fix") < sect.index("Second fix"), "entries not in file-name order")
        for ver, date, why in (
            ("1.2.0", "2026-02-03", "an already released version"),
            ("1.1.5", "2026-02-03", "a version below the latest release"),
            ("1.2.5", "2026-02-03", "a version below the proposed bump"),
            ("1.3", "2026-02-03", "a malformed version"),
            ("1.3.0~beta1", "2026-02-03", "a beta"),
            ("0.0.1-ara.1", "2026-02-03", "the retired -ara.N form"),
            ("1.3.0", "2026-2-3", "a malformed date"),
            ("1.3.0", "2026-02-30", "an impossible date"),
        ):
            try:
                assemble(FIXTURE, d, ver, date)
                failures.append("release accepted " + why)
            except SystemExit:
                pass
        expect("## [1.4.0]" in assemble(FIXTURE, d, "1.4.0", "2026-02-03"), "a version above the proposal was refused")

        # end to end through the CLI, then changelog-section.py reads the result
        cl = root / "CHANGELOG.md"
        cl.write_text(FIXTURE, encoding="utf-8")

        def cli(*args: str) -> subprocess.CompletedProcess:
            return subprocess.run([sys.executable, str(here), "--changelog", str(cl), "--fragments", str(d), *args],
                                  capture_output=True, text=True)

        for v, rc in (("1.0.0~beta2", 0), ("1.0.0", 1), ("1.0.0~beta0", 2), ("0.0.1-ara.1", 2)):
            got = cli("--is-beta", v).returncode
            expect(got == rc, "--is-beta %s exited %d, not %d" % (v, got, rc))
        for mode, bad in (("--bump", "1.0.0~rc1"), ("--preview", "1.0~beta1")):
            r = cli(mode, "--version", bad)
            expect(r.returncode != 0 and "is not X.Y.Z or X.Y.Z~betaN" in r.stderr, "%s --version %s accepted" % (mode, bad))
        r = cli("--bump", "--version", "1.5.0~beta2")
        expect(r.returncode == 0 and r.stdout.strip() == "1.5.0", "--bump --version 1.5.0~beta2: " + r.stderr.strip())
        (root / "VERSION").write_text("1.5.0~beta2\n", encoding="utf-8")
        for mode, want in (("--bump", "1.5.0"), ("--preview", "## [1.5.0]")):
            r = cli(mode)
            expect(r.returncode == 0 and r.stdout.splitlines()[0].strip() == want,
                   "%s ignored the VERSION file beside the changelog: %r" % (mode, r.stdout[:40]))
        (root / "VERSION").write_text("1.5.0~rc1\n", encoding="utf-8")
        warned = io.StringIO()
        with contextlib.redirect_stderr(warned):
            expect(default_version_floor(cl) is None and "not used as the version floor" in warned.getvalue(),
                   "an unparseable VERSION was dropped without a warning")
        (root / "VERSION").unlink()
        r = cli("--release", "1.3.0", "--date", "2026-02-03")
        expect(r.returncode == 0, "--release failed: " + r.stderr.strip())
        expect([p.name for p in d.iterdir()] == ["README.md"], "fragments not deleted (or README.md was)")
        s = subprocess.run([sys.executable, str(here.parent / "changelog-section.py"), "1.3.0", "--changelog", str(cl)],
                           capture_output=True, text=True)
        expect(s.returncode == 0 and "First add" in s.stdout, "changelog-section.py did not read the new section: " + s.stderr)
        expect(cli("--release", "1.4.0", "--date", "2026-02-04").returncode != 0, "a release with nothing to release succeeded")

    for f in failures:
        print("SELF-TEST FAIL: %s" % f, file=sys.stderr)
    print("changelog-fragments self-test %s." % ("FAILED" if failures else "OK"))
    return 1 if failures else 0


def main() -> int:
    ap = argparse.ArgumentParser(
        description="Validate changelog fragments and assemble them into CHANGELOG.md at release.",
        epilog="Fragment format: changelog.d/README.md. Release process: docs/RELEASES.md.",
    )
    mode = ap.add_mutually_exclusive_group(required=True)
    mode.add_argument("--check", action="store_true", help="validate every fragment (CI)")
    mode.add_argument("--preview", action="store_true", help="print the section --release would write, without a date")
    mode.add_argument(
        "--bump", action="store_true",
        help="print the proposed next version: major for Breaking changes, minor for an unqualified Added, else patch",
    )
    mode.add_argument(
        "--release", metavar="X.Y.Z", help="write the dated X.Y.Z section to the changelog and delete the fragments (needs --date)"
    )
    mode.add_argument(
        "--is-beta", metavar="VERSION",
        help="exit 0 if VERSION is X.Y.Z~betaN, 1 if it is a bare X.Y.Z, 2 otherwise (for shell scripts)",
    )
    mode.add_argument("--self-test", action="store_true", help="run the script's built-in tests")
    ap.add_argument("--changelog", default="CHANGELOG.md", type=Path, metavar="PATH", help="changelog file (default: %(default)s)")
    ap.add_argument("--fragments", default="changelog.d", type=Path, metavar="DIR", help="fragment directory (default: %(default)s)")
    ap.add_argument(
        "--version", metavar="X.Y.Z[~betaN]",
        help="with --preview: version for the heading (default: the --bump result); with --bump: the current VERSION, "
        "a floor where a beta counts as its base (default: the VERSION file beside the changelog)",
    )
    ap.add_argument("--date", metavar="YYYY-MM-DD", help="with --release: release date (required)")
    args = ap.parse_args()

    if args.self_test:
        return self_test()
    if args.is_beta is not None:
        if is_beta(args.is_beta):
            return 0
        if BARE_RE.match(args.is_beta):
            return 1
        print("ERROR: %r is not X.Y.Z or X.Y.Z~betaN" % args.is_beta, file=sys.stderr)
        return 2
    if args.check:
        return check(args.fragments, args.changelog)
    if args.version and not VERSION_RE.match(args.version):
        ap.error("--version %r is not X.Y.Z or X.Y.Z~betaN" % args.version)

    problems = problems_of(args.fragments, args.changelog)
    if problems:
        print("\n".join(problems), file=sys.stderr)
        print("ERROR: fix the fragments first (--check)", file=sys.stderr)
        return 1
    text = args.changelog.read_text(encoding="utf-8")
    lines = text.split("\n")
    if args.bump or args.preview:
        entries = collect(args.fragments)
        if not any(entries.values()):
            print("ERROR: nothing to release: no fragments", file=sys.stderr)
            return 1
        if args.bump:
            current = args.version or default_version_floor(args.changelog)
            warning = beta_bump_warning(current, current_branch())
            if warning:
                print(warning, file=sys.stderr)
            print(propose_bump(lines, args.fragments, current))
            return 0
        # Same floor as --bump, so the preview shows the section the release writes.
        version = args.version or propose_bump(lines, args.fragments, default_version_floor(args.changelog))
        print("\n".join(render_section("## [%s]" % version, entries)).rstrip())
        return 0

    if not args.date:
        ap.error("--release requires --date YYYY-MM-DD")
    new = assemble(text, args.fragments, args.release, args.date)
    args.changelog.write_text(new, encoding="utf-8")
    for p in fragment_files(args.fragments):
        p.unlink()
    print("Wrote [%s] - %s to %s" % (args.release, args.date, args.changelog))
    return 0


if __name__ == "__main__":
    sys.exit(main())
