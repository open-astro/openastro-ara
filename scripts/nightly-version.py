#!/usr/bin/env python3
"""The .deb version of an untagged CI build: <next>~~nightly<YYYYMMDDHHMM>.g<sha7>.

Ara publishes no binaries (docs/RELEASES.md), but CI still builds a .deb on
every push, and a tester may install one. An untagged build is named after the
release it leads to, with a ~~nightly suffix so dpkg sorts it below that
release and below its first beta (~~ sorts before ~beta1), and above everything
already released. A single ~ would not do: ~nightly sorts above ~beta3
(b before n), so installing the real beta would count as a downgrade.

While VERSION is a beta X.Y.Z~betaN and <next> is its X.Y.Z (stable/X.Y
between betas), the build is X.Y.Z~betaN+nightly<YYYYMMDDHHMM>.g<sha7>, which
sorts between beta N and beta N+1.

changelog-fragments.py --bump alone is not enough (AlpacaBridge's
scripts/nightly_version.py explains why). Once stable/X.Y is cut, every X.Y.Z is
built from that branch and master's code first ships in X.(Y+1).0 or later, but
a merge down keeps master's VERSION, so --bump on master can still print X.Y.Z;
a nightly named after it would sort below X.Y.Z and apt would "upgrade" a
master tester back to the stable line. So <next> is the higher of the --bump
result and X.(Y+1).0, where X.Y is the newest line named by a stable/X.Y branch
(local or remote-tracking) or a vX.Y.Z[-beta.N] tag whose commit holds that
VERSION (the tags inherited from NINA, v1.10.1 among them, do not count). On stable/X.Y itself the
newest line is X.Y, so pass --branch to keep a stable branch's nightly on its
own line: then <next> is the --bump result alone.

Usage:
  nightly-version.py [--sha SHA] [--stamp YYYYMMDDHHMM] [--bump X.Y.Z]
                     [--current VERSION] [--branch NAME] [--refs-from-stdin]
    --sha     the commit (default: git rev-parse HEAD); 7 characters are used
    --stamp   UTC build time (default: now)
    --bump    the next version (default: changelog-fragments.py --bump with the
              VERSION file as the floor, a patch when there are no fragments)
    --current the VERSION being built (default: the VERSION file); a beta
              names the build after that beta
    --branch  the branch being built (default: the checked-out branch); a
              stable/X.Y branch does not move past its own line
    --refs-from-stdin  read ref names from stdin instead of git for-each-ref
  nightly-version.py --self-test
"""

from __future__ import annotations

import argparse
import datetime
import importlib.util
import os
import re
import subprocess
import sys
from pathlib import Path

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)
from version import BARE_PATTERN, BARE_RE, base_version, is_beta, read_version, version_tuple  # noqa: E402  (scripts/version.py)

LINE_REF_RE = re.compile(
    r"^refs/(?:(?:heads|remotes/[^/]+)/stable/(\d+)\.(\d+)|tags/v(\d+)\.(\d+)\.\d+(?:-beta\.[1-9]\d*)?)$"
)
STABLE_BRANCH_RE = re.compile(r"^stable/(\d+)\.(\d+)$")
SHA_RE = re.compile(r"^[0-9a-f]{7,40}$")
STAMP_RE = re.compile(r"^\d{12}$")


def newest_line(refs, below=None):
    """(X, Y) of the newest stable branch or release tag, or None; `below` caps it (a stable branch's own line)."""
    lines = []
    for ref in refs:
        m = LINE_REF_RE.match(ref.strip())
        if m:
            x, y = (m.group(1), m.group(2)) if m.group(1) is not None else (m.group(3), m.group(4))
            lines.append((int(x), int(y)))
    if below is not None:
        lines = [ln for ln in lines if ln < below]
    return max(lines) if lines else None


def nightly_base(bump, refs, branch=None):
    """The higher of `bump` and the minor release after the newest line.

    On stable/X.Y the branch's own line and any newer one are not counted: its
    nightlies lead to its own next X.Y.Z.
    """
    if not BARE_RE.match(bump):
        raise ValueError("--bump %r is not X.Y.Z" % bump)
    proposed = version_tuple(bump)
    m = STABLE_BRANCH_RE.match(branch or "")
    own = (int(m.group(1)), int(m.group(2))) if m else None
    line = newest_line(refs, below=own)
    if line:
        proposed = max(proposed, (line[0], line[1] + 1, 0))
    return ".".join(str(x) for x in proposed)


def nightly_version(base, sha, stamp, current=None):
    """<base>~~nightly..., or <current>+nightly... when `current` is a beta of `base`."""
    if not SHA_RE.match(sha):
        raise ValueError("--sha %r is not a hex commit id" % sha)
    if not STAMP_RE.match(stamp):
        raise ValueError("--stamp %r is not YYYYMMDDHHMM" % stamp)
    if current and is_beta(current) and base_version(current) == base:
        return "%s+nightly%s.g%s" % (current, stamp, sha[:7])
    return "%s~~nightly%s.g%s" % (base, stamp, sha[:7])


def default_current(root=ROOT):
    """The VERSION file's version, or None when it is missing or malformed."""
    try:
        return read_version(os.path.join(root, "VERSION"))
    except (OSError, ValueError):
        return None


def load_fragments_module():
    spec = importlib.util.spec_from_file_location("changelog_fragments", os.path.join(HERE, "changelog-fragments.py"))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def default_bump(root=ROOT):
    """changelog-fragments.py's proposal with the VERSION file as the floor (a patch with no fragments)."""
    cf = load_fragments_module()
    changelog = os.path.join(root, "CHANGELOG.md")
    with open(changelog, encoding="utf-8") as f:
        lines = f.read().split("\n")
    return cf.propose_bump(lines, Path(root) / "changelog.d", cf.default_version_floor(Path(changelog)))


def git_out(*args):
    r = subprocess.run(["git", "-C", ROOT, *args], capture_output=True, text=True)
    return r.stdout.strip() if r.returncode == 0 else ""


TAG_REF_RE = re.compile(r"^refs/tags/v(" + BARE_PATTERN + r")(?:-beta\.([1-9]\d*))?$")


def is_ara_release_tag(ref, version_at=None):
    """True when the tag's commit holds the VERSION it names.

    The repository still carries tags inherited from NINA (v1.10.1 among them)
    that look like release tags; none of them has a VERSION file, so they do not
    mark a line. `version_at(ref)` returns the VERSION text at the ref, or None.
    """
    m = TAG_REF_RE.match(ref)
    if not m:
        return True  # not a tag: a stable branch is taken as it is
    want = m.group(1) + ("~beta" + m.group(2) if m.group(2) else "")
    got = (version_at or git_version_at)(ref)
    return got is not None and got.strip() == want


def git_version_at(ref):
    r = subprocess.run(["git", "-C", ROOT, "show", "%s:VERSION" % ref], capture_output=True, text=True)
    return r.stdout if r.returncode == 0 else None


def git_refs():
    refs = git_out("for-each-ref", "--format=%(refname)", "refs/heads/stable", "refs/remotes", "refs/tags").splitlines()
    return [r for r in refs if LINE_REF_RE.match(r) and is_ara_release_tag(r)]


def self_test():
    failures = []

    def check(name, ok):
        if not ok:
            failures.append(name)

    cut = ["refs/heads/master", "refs/remotes/origin/stable/1.0", "refs/tags/v1.0.0-beta.4"]
    check("master after the 1.0 cut is past 1.0.0", nightly_base("1.0.0", cut) == "1.1.0")
    check("a breaking change on master still wins", nightly_base("2.0.0", cut) == "2.0.0")
    check("a beta tag alone marks the line", nightly_base("1.0.0", ["refs/tags/v1.0.0-beta.1"]) == "1.1.0")
    check("a local stable branch alone marks the line", nightly_base("1.0.0", ["refs/heads/stable/1.0"]) == "1.1.0")
    check("a remote stable branch alone marks the line", nightly_base("1.0.0", ["refs/remotes/upstream/stable/1.0"]) == "1.1.0")
    check("after the merge-down a fix-only master is still past 1.0",
          nightly_base("1.0.1", cut + ["refs/tags/v1.0.0"]) == "1.1.0")
    check("no line keeps the bump", nightly_base("0.0.2", ["refs/heads/master"]) == "0.0.2")
    check("pre-1.0 lines count", nightly_base("0.0.2", ["refs/tags/v0.0.1"]) == "0.1.0")
    check("lines compare numerically", newest_line(["refs/heads/stable/1.9", "refs/heads/stable/1.10"]) == (1, 10))
    check("stable/1.0 stays on its own line", nightly_base("1.0.1", cut + ["refs/tags/v1.0.0"], "stable/1.0") == "1.0.1")
    check("stable/1.0 ignores a newer stable/1.1",
          nightly_base("1.0.1", cut + ["refs/heads/stable/1.1"], "stable/1.0") == "1.0.1")
    check("stable/1.1 is past an older 1.0 line", nightly_base("1.0.0", cut, "stable/1.1") == "1.1.0")
    for ref in ("refs/heads/stable/1", "refs/heads/stable/1.0-x", "refs/tags/v1.0", "refs/tags/v1.0.0-rc.1",
                "refs/tags/v1.0.0-beta.0", "refs/tags/v0.0.1-ara.1", "refs/tags/nightly-build",
                "refs/heads/feature/stable/9.9", "refs/remotes/origin/feature/stable/9.9"):
        check("ignores %s" % ref, newest_line([ref]) is None)
    for bad in ("1.0", "1.0.0~beta1", ""):
        try:
            nightly_base(bad, cut)
            failures.append("accepted --bump %r" % bad)
        except ValueError:
            pass
    versions = {"refs/tags/v1.0.0": "1.0.0\n", "refs/tags/v1.0.0-beta.1": "1.0.0~beta1\n", "refs/tags/v1.10.1": None,
                "refs/tags/v1.1.0": "1.0.1\n"}
    check("an Ara release tag counts", is_ara_release_tag("refs/tags/v1.0.0", versions.get))
    check("an Ara beta tag counts", is_ara_release_tag("refs/tags/v1.0.0-beta.1", versions.get))
    check("an inherited NINA tag with no VERSION does not count", not is_ara_release_tag("refs/tags/v1.10.1", versions.get))
    check("a tag whose VERSION disagrees does not count", not is_ara_release_tag("refs/tags/v1.1.0", versions.get))
    check("a stable branch is not filtered", is_ara_release_tag("refs/heads/stable/1.0", versions.get))
    check("version shape", nightly_version("1.1.0", "abc1234def", "202611190317") == "1.1.0~~nightly202611190317.gabc1234")
    check("a stable VERSION keeps the ~~ shape",
          nightly_version("1.0.1", "abc1234", "202611190317", "1.0.0") == "1.0.1~~nightly202611190317.gabc1234")
    check("a beta VERSION names the build after that beta",
          nightly_version("1.0.0", "abc1234", "202611190317", "1.0.0~beta2") == "1.0.0~beta2+nightly202611190317.gabc1234")
    check("a beta of another release does not",
          nightly_version("1.1.0", "abc1234", "202611190317", "1.0.0~beta2") == "1.1.0~~nightly202611190317.gabc1234")
    for sha, stamp in (("xyz1234", "202611190317"), ("abc12", "202611190317"), ("abc1234", "2026111903")):
        try:
            nightly_version("1.1.0", sha, stamp)
            failures.append("accepted --sha %r --stamp %r" % (sha, stamp))
        except ValueError:
            pass
    order = ["0.0.1", "1.0.0~~nightly202611190317.gabc1234", "1.0.0~beta1", "1.0.0~beta2",
             "1.0.0~beta2+nightly202611190317.gabc1234", "1.0.0~beta2+nightly202611190318.g0000000",
             "1.0.0~beta3", "1.0.0~beta4", "1.0.0", "1.0.1~~nightly202611190317.gabc1234", "1.0.1",
             "1.1.0~~nightly202611190317.gabc1234", "1.1.0~~nightly202611190318.g0000000", "1.1.0~beta1", "1.1.0"]
    try:
        for lo, hi in zip(order, order[1:]):
            if subprocess.run(["dpkg", "--compare-versions", lo, "lt", hi]).returncode != 0:
                failures.append("dpkg order: %s is not below %s" % (lo, hi))
    except FileNotFoundError:
        print("dpkg not installed: version order not checked", file=sys.stderr)
    for f in failures:
        print("SELF-TEST FAIL: %s" % f, file=sys.stderr)
    print("nightly-version self-test %s." % ("FAILED" if failures else "OK"))
    return 1 if failures else 0


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--self-test", action="store_true")
    ap.add_argument("--sha")
    ap.add_argument("--stamp")
    ap.add_argument("--bump")
    ap.add_argument("--current")
    ap.add_argument("--branch")
    ap.add_argument("--refs-from-stdin", action="store_true")
    args = ap.parse_args(argv)
    if args.self_test:
        return self_test()
    try:
        sha = (args.sha or git_out("rev-parse", "HEAD")).lower()
        stamp = args.stamp or datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%d%H%M")
        bump = args.bump or default_bump()
        refs = sys.stdin.read().splitlines() if args.refs_from_stdin else git_refs()
        branch = args.branch if args.branch is not None else git_out("rev-parse", "--abbrev-ref", "HEAD")
        current = args.current if args.current is not None else default_current()
        print(nightly_version(nightly_base(bump, refs, branch), sha, stamp, current))
    except (OSError, ValueError) as e:
        print("ERROR: %s" % e, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
