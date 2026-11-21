#!/usr/bin/env python3
"""Map a release tag to its VERSION file spelling and release kind.

A stable tag vX.Y.Z is VERSION X.Y.Z. A beta tag vX.Y.Z-beta.N is VERSION
X.Y.Z~betaN (the Debian pre-release spelling, so 1.0.0~beta1 < 1.0.0~beta2 <
1.0.0). Prints key=value lines for .github/workflows/release.yml:

  version=<VERSION file text>
  semver=<display spelling: the tag without its leading v>
  prerelease=true|false
  notes_name=<docs/releases/ file name, <semver>.md>
  stable_branch=stable/X.Y

Exit 2 on a malformed tag. The pairs pinned in scripts/release-tag-cases.txt
are checked by --self-test.

Usage: python3 scripts/release-tag.py <tag>
Self-test: python3 scripts/release-tag.py --self-test
"""

from __future__ import annotations

import os
import re
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import version as versions  # noqa: E402  (scripts/version.py)

TAG_RE = re.compile(r"^v(" + versions.BARE_PATTERN + r")(?:-beta\.([1-9]\d*))?$")
CASES_FILE = os.path.join(HERE, "release-tag-cases.txt")


def map_tag(tag: str) -> dict:
    """The release facts of `tag`, or ValueError when it is not vX.Y.Z or vX.Y.Z-beta.N."""
    m = TAG_RE.fullmatch(tag)
    if not m:
        raise ValueError("tag %r is not vX.Y.Z or vX.Y.Z-beta.N (N >= 1)" % tag)
    base, beta = m.groups()
    major, minor, _ = base.split(".")
    semver = tag[1:]
    return {
        "version": "%s~beta%s" % (base, beta) if beta else base,
        "semver": semver,
        "prerelease": bool(beta),
        "notes_name": semver + ".md",
        "stable_branch": "stable/%s.%s" % (major, minor),
    }


def read_cases(path: str = CASES_FILE) -> list[tuple[str, str]]:
    """(tag, VERSION) pairs; a '#' starts a comment, every other line holds two fields."""
    pairs = []
    with open(path, encoding="utf-8") as f:
        for n, line in enumerate(f, 1):
            fields = line.split("#", 1)[0].split()
            if not fields:
                continue
            if len(fields) != 2:
                raise ValueError("%s:%d: expected '<tag> <VERSION>', got %r" % (path, n, line.rstrip("\n")))
            pairs.append((fields[0], fields[1]))
    return pairs


def self_test() -> int:
    failures: list[str] = []

    def check(name: str, ok: bool) -> None:
        if not ok:
            failures.append(name)

    check("stable", map_tag("v1.0.0") == {"version": "1.0.0", "semver": "1.0.0", "prerelease": False,
                                          "notes_name": "1.0.0.md", "stable_branch": "stable/1.0"})
    check("beta", map_tag("v1.0.0-beta.2") == {"version": "1.0.0~beta2", "semver": "1.0.0-beta.2", "prerelease": True,
                                               "notes_name": "1.0.0-beta.2.md", "stable_branch": "stable/1.0"})
    check("pre-1.0", map_tag("v0.0.1")["stable_branch"] == "stable/0.0")
    try:
        pairs = read_cases()
    except (OSError, ValueError) as e:
        failures.append(str(e))
        pairs = []
    with tempfile.TemporaryDirectory() as tmp:
        sample = os.path.join(tmp, "cases.txt")
        with open(sample, "w", encoding="utf-8") as f:
            f.write("# c\n\nv5.0.0 5.0.0  # trailing comment\n  v5.0.0-beta.1 5.0.0~beta1\r\n")
        check("trailing comments and CRLF are read",
              read_cases(sample) == [("v5.0.0", "5.0.0"), ("v5.0.0-beta.1", "5.0.0~beta1")])
        with open(sample, "w", encoding="utf-8") as f:
            f.write("v5.0.0 5.0.0 extra\n")
        try:
            read_cases(sample)
            failures.append("a three-field line was accepted")
        except ValueError as e:
            check("a three-field line names its line", ":1:" in str(e))
    check("release-tag-cases.txt has a beta and a stable pair",
          any("~" in v for _, v in pairs) and any("~" not in v for _, v in pairs))
    for tag, version in pairs:
        facts = map_tag(tag)
        check("case %s -> %s" % (tag, version), facts["version"] == version)
        check("case %s is a VERSION to version.py" % tag, versions.VERSION_RE.match(version) is not None)
        check("case %s beta agrees with version.is_beta" % tag, versions.is_beta(version) == facts["prerelease"])
        check("case %s display spelling agrees with version.to_semver" % tag, versions.to_semver(version) == tag[1:])
        check("case %s display spelling maps back" % tag, versions.from_semver(tag[1:]) == version)
        check("case %s notes name" % tag, facts["notes_name"] == tag[1:] + ".md")
        check("case %s stable branch" % tag,
              facts["stable_branch"] == "stable/" + ".".join(versions.base_version(version).split(".")[:2]))
    for bad in ("1.0.0", "v1.0", "v1.0.0-beta", "v1.0.0-beta.0", "v1.0.0-beta.01", "v1.0.0-rc.1", "v0.0.1-ara.1",
                "v1.0.0-beta.1-x", "v1.0.0~beta1", "v1.0.0-beta.1\n", "V1.0.0", "",
                "v1.02.0", "v01.0.0", "v1.0.00-beta.1"):
        try:
            map_tag(bad)
            failures.append("accepted malformed tag %r" % bad)
        except ValueError:
            pass
    order = ["0.0.1", "1.0.0~beta1", "1.0.0~beta2", "1.0.0~beta10", "1.0.0", "1.0.1", "1.1.0~beta1"]
    try:
        for lo, hi in zip(order, order[1:]):
            if subprocess.run(["dpkg", "--compare-versions", lo, "lt", hi]).returncode != 0:
                failures.append("dpkg order: %s is not below %s" % (lo, hi))
    except FileNotFoundError:
        print("dpkg not installed: version order not checked", file=sys.stderr)
    for f in failures:
        print("SELF-TEST FAIL: %s" % f, file=sys.stderr)
    print("release-tag self-test %s (%d pinned case(s))." % ("FAILED" if failures else "OK", len(pairs)))
    return 1 if failures else 0


def main(argv: list[str]) -> int:
    if argv == ["--self-test"]:
        return self_test()
    if len(argv) != 1:
        print(__doc__, file=sys.stderr)
        return 2
    try:
        facts = map_tag(argv[0])
    except ValueError as e:
        print("ERROR: %s" % e, file=sys.stderr)
        return 2
    for key in ("version", "semver", "prerelease", "notes_name", "stable_branch"):
        value = facts[key]
        print("%s=%s" % (key, str(value).lower() if isinstance(value, bool) else value))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
