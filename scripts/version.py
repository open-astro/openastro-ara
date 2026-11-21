#!/usr/bin/env python3
"""Read the root VERSION file and print it in each spelling Ara uses.

VERSION holds one line: X.Y.Z, or X.Y.Z~betaN (N >= 1) for a beta. master holds
the last stable version; a beta VERSION only ever lives on stable/X.Y
(docs/RELEASES.md). One version has four spellings:

  VERSION file   1.0.0~beta1   1.0.0     (Debian pre-release spelling: ~ sorts first)
  git tag        v1.0.0-beta.1 v1.0.0    (scripts/release-tag.py maps it back)
  display        1.0.0-beta.1  1.0.0     (SemVer; UI, /server/info, mDNS, README badge)
  pubspec        1.0.0-beta.1+<build>    (client/openastroara_client/pubspec.yaml)
  .deb           1.0.0~beta1   1.0.0

  --semver         print the display (SemVer) spelling
  --pubspec-base   print the pubspec version before '+' (the display spelling)
  --deb            print the .deb spelling
  --is-beta        exit 0 for a beta VERSION, 1 for a stable one (2 if invalid)
  --check          fail unless VERSION is valid, the client pubspec version
                   (before '+') is its display spelling, the client's
                   kAraAppVersion constant (lib/version/ara_version.dart) is
                   too, and the README badge line
                   '#### [<display>] - YYYY-MM-DD · [Changelog](CHANGELOG.md)'
                   carries the same version
  --self-test      run the built-in cases

--file PATH reads another VERSION file ('-' reads stdin), --root DIR checks
another checkout. Other scripts import this module for the patterns.
"""

from __future__ import annotations

import argparse
import re
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# Plain strings with no capturing group, so other scripts can embed them.
# No leading zeros (1.02.0), as Directory.Build.props, AraVersion.cs and
# ara_version.dart refuse them; a number is 0 or starts with 1-9.
NUMBER_PATTERN = r"(?:0|[1-9]\d*)"
BARE_PATTERN = NUMBER_PATTERN + r"\." + NUMBER_PATTERN + r"\." + NUMBER_PATTERN
BETA_SUFFIX_PATTERN = r"~beta[1-9]\d*"
VERSION_PATTERN = BARE_PATTERN + "(?:" + BETA_SUFFIX_PATTERN + ")?"
SEMVER_PATTERN = BARE_PATTERN + r"(?:-beta\.[1-9]\d*)?"
BARE_RE = re.compile("^" + BARE_PATTERN + "$")
BETA_RE = re.compile("^" + BARE_PATTERN + BETA_SUFFIX_PATTERN + "$")
VERSION_RE = re.compile("^" + VERSION_PATTERN + "$")
SEMVER_RE = re.compile("^" + SEMVER_PATTERN + "$")

PUBSPEC = Path("client/openastroara_client/pubspec.yaml")
# The app's own version: Apple platforms can drop -beta.N from the platform
# version, so the client reads this constant instead (#2093).
APP_VERSION_DART = Path("client/openastroara_client/lib/version/ara_version.dart")
APP_VERSION_RE = re.compile(r"^const String kAraAppVersion = '(?P<v>[^']*)';\s*$")
README = Path("README.md")
PUBSPEC_LINE_RE = re.compile(r"^version:\s*(?P<v>\S+)\s*(?:#.*)?$")
# AlpacaBridge writes the middle dot as an entity; either form reads the same on GitHub.
BADGE_RE = re.compile(
    r"^#### \[(?P<v>[^\]]+)\] - (?P<date>\d{4}-\d{2}-\d{2}) (?:·|&middot;) \[Changelog\]\(CHANGELOG\.md\)\s*$"
)


def is_beta(v: str) -> bool:
    """True only for X.Y.Z~betaN with N >= 1 (not ~rc1, ~beta0, ~beta)."""
    return BETA_RE.match(v) is not None


def base_version(v: str) -> str:
    """1.0.0~beta2 -> 1.0.0; a bare version is returned unchanged."""
    return v.split("~", 1)[0]


def version_tuple(v: str) -> tuple:
    return tuple(int(x) for x in base_version(v).split("."))


def validate(v: str) -> str:
    if not VERSION_RE.match(v):
        raise ValueError("%r is not X.Y.Z or X.Y.Z~betaN (N >= 1)" % v)
    return v


def to_semver(v: str) -> str:
    """1.0.0~beta1 -> 1.0.0-beta.1; 1.0.0 -> 1.0.0."""
    validate(v)
    if is_beta(v):
        base, n = v.split("~beta", 1)
        return "%s-beta.%s" % (base, n)
    return v


def from_semver(s: str) -> str:
    """1.0.0-beta.1 -> 1.0.0~beta1 (the inverse of to_semver)."""
    if not SEMVER_RE.match(s):
        raise ValueError("%r is not X.Y.Z or X.Y.Z-beta.N (N >= 1)" % s)
    if "-beta." in s:
        base, n = s.split("-beta.", 1)
        return "%s~beta%s" % (base, n)
    return s


def to_deb(v: str) -> str:
    """The .deb spelling is the VERSION spelling."""
    return validate(v)


def read_version(path: Path | str) -> str:
    """The VERSION text, stripped and validated (ValueError when it is not one line X.Y.Z[~betaN])."""
    text = sys.stdin.read() if str(path) == "-" else Path(path).read_text(encoding="utf-8")
    lines = [ln for ln in text.splitlines() if ln.strip()]
    if len(lines) != 1:
        raise ValueError("%s must hold exactly one line, found %d" % (path, len(lines)))
    return validate(lines[0].strip())


def pubspec_version(text: str) -> str | None:
    for line in text.splitlines():
        m = PUBSPEC_LINE_RE.match(line)
        if m:
            return m.group("v")
    return None


def app_version_constant(text: str) -> str | None:
    for line in text.splitlines():
        m = APP_VERSION_RE.match(line)
        if m:
            return m.group("v")
    return None


def badge_version(text: str) -> str | None:
    for line in text.splitlines():
        m = BADGE_RE.match(line)
        if m:
            return m.group("v")
    return None


def check(root: Path, version_file: Path | None = None) -> list[str]:
    """Every disagreement between VERSION, the client pubspec, its kAraAppVersion and the README badge."""
    problems: list[str] = []
    vf = version_file or root / "VERSION"
    try:
        v = read_version(vf)
    except (OSError, ValueError) as e:
        return ["VERSION: %s" % e]
    semver = to_semver(v)
    pubspec = root / PUBSPEC
    if not pubspec.is_file():
        problems.append("%s: missing" % PUBSPEC)
    else:
        pv = pubspec_version(pubspec.read_text(encoding="utf-8"))
        if pv is None:
            problems.append("%s: no 'version:' line" % PUBSPEC)
        else:
            base, plus, build = pv.partition("+")
            if base != semver:
                problems.append("%s: version %s, but VERSION %s is %s (want %s+<build>)" % (PUBSPEC, pv, v, semver, semver))
            if plus and not build.isdigit():
                problems.append("%s: build number %r after '+' is not digits" % (PUBSPEC, build))
    dart = root / APP_VERSION_DART
    if not dart.is_file():
        problems.append("%s: missing" % APP_VERSION_DART)
    else:
        av = app_version_constant(dart.read_text(encoding="utf-8"))
        if av is None:
            problems.append("%s: no line \"const String kAraAppVersion = '%s';\"" % (APP_VERSION_DART, semver))
        elif av != semver:
            problems.append("%s: kAraAppVersion is %s, but VERSION %s is %s" % (APP_VERSION_DART, av, v, semver))
    readme = root / README
    if not readme.is_file():
        problems.append("%s: missing" % README)
    else:
        bv = badge_version(readme.read_text(encoding="utf-8"))
        if bv is None:
            problems.append("%s: no badge line '#### [%s] - YYYY-MM-DD · [Changelog](CHANGELOG.md)'" % (README, semver))
        elif bv != semver:
            problems.append("%s: badge says %s, but VERSION %s is %s" % (README, bv, v, semver))
    return problems


def self_test() -> int:
    failures: list[str] = []

    def expect(ok: bool, msg: str) -> None:
        if not ok:
            failures.append(msg)

    for v, s in (("0.0.1", "0.0.1"), ("1.0.0~beta1", "1.0.0-beta.1"), ("1.2.3~beta10", "1.2.3-beta.10")):
        expect(to_semver(v) == s, "to_semver(%s) != %s" % (v, s))
        expect(from_semver(s) == v, "from_semver(%s) != %s" % (s, v))
        expect(to_deb(v) == v, "to_deb(%s) changed it" % v)
    for bad in ("1.0", "1.0.0~beta0", "1.0.0~beta01", "1.0.0~rc1", "1.0.0-beta.1", "0.0.1-ara.1", "v1.0.0", "",
                "1.02.0", "01.0.0", "1.0.00"):
        try:
            to_semver(bad)
            failures.append("accepted VERSION %r" % bad)
        except ValueError:
            pass
    for bad in ("1.0.0~beta1", "1.0.0-beta.0", "1.0.0-rc.1", "1.0.0-beta", "1.02.0-beta.1"):
        try:
            from_semver(bad)
            failures.append("accepted display version %r" % bad)
        except ValueError:
            pass
    expect(is_beta("1.0.0~beta1") and not is_beta("1.0.0") and not is_beta("1.0.0~beta0"), "is_beta wrong")
    expect(version_tuple("1.10.0~beta2") == (1, 10, 0), "version_tuple wrong")
    expect(re.compile(VERSION_PATTERN).groups == 0 and re.compile(SEMVER_PATTERN).groups == 0,
           "a pattern gained a capturing group")
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        (root / PUBSPEC).parent.mkdir(parents=True)
        (root / APP_VERSION_DART).parent.mkdir(parents=True)
        (root / APP_VERSION_DART).write_text("const String kAraAppVersion = '1.0.0-beta.2';\n", encoding="utf-8")
        (root / "VERSION").write_text("1.0.0~beta2\n", encoding="utf-8")
        (root / PUBSPEC).write_text("name: x\nversion: 1.0.0-beta.2+40\n", encoding="utf-8")
        (root / README).write_text("# Ara\n\n#### [1.0.0-beta.2] - 2026-12-01 · [Changelog](CHANGELOG.md)\n", encoding="utf-8")
        expect(check(root) == [], "a consistent tree failed: %s" % check(root))
        (root / README).write_text("#### [1.0.0-beta.2] - 2026-12-01 &middot; [Changelog](CHANGELOG.md)\n", encoding="utf-8")
        expect(check(root) == [], "the &middot; badge form failed")
        (root / PUBSPEC).write_text("version: 1.0.0+40\n", encoding="utf-8")
        expect(any("pubspec" in p for p in check(root)), "a pubspec mismatch passed")
        (root / PUBSPEC).write_text("version: 1.0.0-beta.2+4x\n", encoding="utf-8")
        expect(any("not digits" in p for p in check(root)), "a bad build number passed")
        (root / PUBSPEC).write_text("version: 1.0.0-beta.2+40\n", encoding="utf-8")
        (root / APP_VERSION_DART).write_text("const String kAraAppVersion = '1.0.0';\n", encoding="utf-8")
        expect(any("kAraAppVersion is 1.0.0" in p for p in check(root)), "an app constant mismatch passed")
        (root / APP_VERSION_DART).write_text("const String kAraAppVersion = '1.0.0-beta.2';\n", encoding="utf-8")
        (root / README).write_text("#### [1.0.0-beta.1] - 2026-12-01 · [Changelog](CHANGELOG.md)\n", encoding="utf-8")
        expect(any("badge says" in p for p in check(root)), "a badge mismatch passed")
        (root / README).write_text("# no badge\n", encoding="utf-8")
        expect(any("no badge line" in p for p in check(root)), "a missing badge passed")
        (root / "VERSION").write_text("1.0.0\n2.0.0\n", encoding="utf-8")
        expect(any("exactly one line" in p for p in check(root)), "a two-line VERSION passed")
        (root / "VERSION").write_text("0.0.1-ara.1\n", encoding="utf-8")
        expect(any("not X.Y.Z" in p for p in check(root)), "the retired -ara.N form passed")
    for f in failures:
        print("SELF-TEST FAIL: %s" % f, file=sys.stderr)
    print("version self-test %s." % ("FAILED" if failures else "OK"))
    return 1 if failures else 0


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    mode = ap.add_mutually_exclusive_group(required=True)
    mode.add_argument("--semver", action="store_true", help="print the display (SemVer) spelling")
    mode.add_argument("--pubspec-base", action="store_true", help="print the pubspec version before '+'")
    mode.add_argument("--deb", action="store_true", help="print the .deb spelling")
    mode.add_argument("--is-beta", action="store_true", help="exit 0 for a beta, 1 for a stable version, 2 if invalid")
    mode.add_argument("--check", action="store_true", help="VERSION, pubspec, kAraAppVersion and README badge agree")
    mode.add_argument("--self-test", action="store_true", help="run the built-in cases")
    ap.add_argument("--root", type=Path, default=ROOT, help="checkout to read (default: this script's repository)")
    ap.add_argument("--file", help="VERSION file to read instead of <root>/VERSION ('-' reads stdin)")
    args = ap.parse_args(argv)
    if args.self_test:
        return self_test()
    if args.check:
        problems = check(args.root, Path(args.file) if args.file else None)
        for p in problems:
            print("ERROR: %s" % p, file=sys.stderr)
        if problems:
            print("version check FAILED (%d problem(s)); see docs/RELEASES.md" % len(problems), file=sys.stderr)
            return 1
        print("version check OK.")
        return 0
    try:
        v = read_version(args.file or args.root / "VERSION")
    except (OSError, ValueError) as e:
        print("ERROR: %s" % e, file=sys.stderr)
        return 2
    if args.is_beta:
        return 0 if is_beta(v) else 1
    print(to_deb(v) if args.deb else to_semver(v))
    return 0


if __name__ == "__main__":
    sys.exit(main())
