#!/usr/bin/env python3
"""Classify a PR's changed paths into the CI buckets that must run.

Reads newline-separated paths on stdin, writes `key=value` lines on stdout in
`$GITHUB_OUTPUT` form. Called by the `changes` job in .github/workflows/ci.yml;
see #1020 for why this is a job-level gate rather than a workflow `paths:`
filter.

Three outputs:

  docs_only  the pre-existing gate (#1020). Unchanged semantics, deliberately:
             it gates REQUIRED contexts, where a wrong skip is counted by
             GitHub as a pass and merges a broken PR with nothing red.
  dotnet     the .NET build/test graph -- gates `analyzer-gate` and the two
             Alpaca jobs, none of which are required contexts.
  client     the Flutter client -- gates `client-build`, not a required
             context either.

The direction of failure is the whole design. A bucket is dropped only when
*every* changed path is provably irrelevant to it, and an unrecognised path is
never assumed irrelevant. Adding an entry below is a deliberate act with a
test; forgetting to costs a full matrix run, not a silent skip.

The two buckets are not symmetrical, on purpose:

  dotnet  OPEN. An unknown path sets it. The .NET graph spans a dozen
          top-level project directories, the solution, Directory.Build.props,
          vendored C sources and the packaging tree, and a new one must not
          have to be registered here before CI notices it.
  client  CLOSED over DIRECTORIES. Every step of `client-test` and
          `client-build` runs with `working-directory:
          client/openastroara_client`, and the single file they read outside
          it -- `.flutter-version` -- is under `client/` too. So a change in
          any other top-level directory cannot affect a Flutter analyze,
          test or build. Root-level FILES are not covered by that argument:
          `.gitattributes` can rewrite the bytes of `client/**` at checkout,
          so an unrecognised root file sets both buckets.
          `.github/workflows/ci.yml` is the one directory-path exception.
"""

from __future__ import annotations

import sys

# Every bucket this script can emit, besides the separate `docs_only` gate.
BUCKETS = ("dotnet", "client")
ALL = frozenset(BUCKETS)
NONE: frozenset = frozenset()

# Directory prefixes that contain no build input of any kind.
INERT_DIRS = (
    "design/",
    "docs/",
    ".claude/",
    ".github/ISSUE_TEMPLATE/",
)

# Root-level files that are prose and nothing else. Deliberately short:
# every other root file falls through to the open `dotnet` default. In
# particular `.editorconfig` is NOT here -- it carries the
# `dotnet_diagnostic.*.severity` lines that decide whether the analyzer
# gate's `dotnet build -c Release` passes at all, and `.gitattributes`
# changes the bytes a checkout writes. Neither claim of inertness is
# checkable, so neither is made.
INERT_ROOT_FILES = frozenset(
    {
        "AUTHORS",
        "CONTRIBUTING.md",
        "CHANGELOG.md",
        "COPYING",
        "LICENSE.txt",
        "NOTICE.md",
        "README.md",
    }
)

# Root-level files that ARE build inputs for the .NET graph. Documentation
# only: an unlisted root file reaches the same answer via the open default,
# and the tests assert both routes agree.
DOTNET_ROOT_FILES = frozenset(
    {
        ".editorconfig",  # dotnet_diagnostic.* severities feed the analyzer
        "CommonAssemblyInfo.cs",
        "Directory.Build.props",
        "Dockerfile",
        "OpenAstroAra.sln",
        "OpenAstroAra.sln.licenseheader",
        "global.json",
        # §15/§17.2: server-build regenerates and diffs this file. That job
        # is gated on docs_only, so this entry does not change what runs; it
        # is here so the classification is stated rather than reached by
        # the root-file fallthrough.
        "3rd-party-licenses.txt",
    }
)

# scripts/ is mixed: some of it feeds the .NET jobs, some is standalone
# tooling the Sanity job already covers. Anything here that is NOT listed
# falls through to ALL.
SCRIPT_RULES = {
    "scripts/build-astrometry-natives.sh": frozenset({"dotnet"}),
    "scripts/get-alpaca-simulators.sh": frozenset({"dotnet"}),
    # Produces 3rd-party-licenses.txt, which server-build diffs; server-build
    # runs on docs_only regardless, so this is stated, not load-bearing.
    "scripts/generate-3rd-party-licenses.py": frozenset({"dotnet"}),
    # Standalone tooling, unit-tested by the Sanity job, read by nothing else.
    "scripts/check-flutter-release.py": NONE,
    "scripts/check-settings-registry.mjs": NONE,
    "scripts/check-help-registry.mjs": NONE,
    "scripts/fit-star-count-model.py": NONE,
}

# .github/ is mixed the same way. An explicit list, not a suffix rule: a
# `.github/actions/*/action.yml` composite consumed by analyzer-gate would
# be a .yml that very much affects the .NET graph, so anything under
# .github/ that is not named here (or under ISSUE_TEMPLATE/) falls through
# to the open `dotnet` default.
GITHUB_RULES = {
    # CI itself changed: validate the whole matrix, whatever else is in the PR.
    ".github/workflows/ci.yml": ALL,
    # Sibling workflows: none of them builds or tests anything ci.yml gates.
    ".github/workflows/check-flutter.yml": NONE,
    ".github/workflows/claude-review.yml": NONE,
    ".github/workflows/codeql.yml": NONE,
    ".github/workflows/label-trusted-authors.yml": NONE,
    ".github/dependabot.yml": NONE,
    ".github/FUNDING.yml": NONE,
    ".github/pull_request_template.md": NONE,
    # Run by the (ungated) Unicode scan job only.
    ".github/scripts/check-unicode.py": NONE,
}


def buckets_for(path: str) -> frozenset:
    """The buckets `path` can affect. Unrecognised paths affect everything."""
    if not path:
        return NONE

    if path in GITHUB_RULES:
        return GITHUB_RULES[path]
    if path in SCRIPT_RULES:
        return SCRIPT_RULES[path]

    for prefix in INERT_DIRS:
        if path.startswith(prefix):
            return NONE

    # Python tooling for the repo's own scripts, run by the Sanity job.
    if path.startswith("scripts/tests/"):
        return NONE

    if path.startswith("client/"):
        # pubspec.lock is read by scripts/generate-3rd-party-licenses.py,
        # whose output server-build diffs -- but server-build is gated on
        # docs_only, not dotnet, so it runs for this path regardless. No job
        # gated on `dotnet` reads anything under client/, so a lockfile bump
        # (every Dependabot pub PR) must not pay for the analyzer and the two
        # Alpaca jobs. If server-build is ever moved onto `dotnet`, this is
        # the carve-out to bring back; test_the_client_lockfile_stays_client_only
        # says so.
        return frozenset({"client"})

    if "/" not in path:
        if path in INERT_ROOT_FILES:
            return NONE
        # A root-level *.md that is not listed is still prose.
        if path.endswith(".md"):
            return NONE
        if path in DOTNET_ROOT_FILES:
            return frozenset({"dotnet"})
        # An unrecognised root file. Not `{"dotnet"}`: a `.gitattributes`
        # line like `client/** text eol=lf` changes what checkout writes
        # under client/, so the closed-over-directories argument does not
        # apply here. Both buckets, and the path is named on stderr.
        print(f"unclassified root file, running everything: {path}", file=sys.stderr)
        return ALL

    # Unknown, and in a top-level directory other than client/ -- so it cannot
    # reach the Flutter jobs (see the module docstring), but it may well reach
    # the .NET graph.
    print(f"unclassified path, running the .NET jobs: {path}", file=sys.stderr)
    return frozenset({"dotnet"})


def is_docs_path(path: str) -> bool:
    """The ORIGINAL #1020 docs_only rule, preserved exactly.

    Not re-expressed in terms of `buckets_for`: this one gates required
    contexts, and the two must be able to disagree without one quietly
    widening the other. Notably a nested `*.md` is NOT docs here --
    OpenAstroAra.Test/fixtures/SIMULATORS_VERSION.md exists to be verified by
    analyzer-gate and re-downloaded by the Alpaca jobs, and the §14.5.1
    auto-bump PR touches that file and nothing else.
    """
    for prefix in INERT_DIRS:
        if path.startswith(prefix):
            return True
    if "/" in path:
        return False
    return path.endswith(".md") or path in ("LICENSE.txt", "COPYING")


def classify(paths) -> dict:
    """Map changed paths to CI outputs. An empty diff runs everything."""
    paths = [p.strip() for p in paths if p.strip()]
    if not paths:
        return {"docs_only": False, "dotnet": True, "client": True}

    affected: set = set()
    for path in paths:
        affected |= buckets_for(path)

    return {
        "docs_only": all(is_docs_path(p) for p in paths),
        "dotnet": "dotnet" in affected,
        "client": "client" in affected,
    }


def main() -> int:
    result = classify(sys.stdin.read().splitlines())
    for key, value in result.items():
        print(f"{key}={'true' if value else 'false'}")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as exc:  # noqa: BLE001 - fail safe, never fail closed
        # Match the workflow's own trap: a classifier that cannot decide must
        # run everything rather than skip something it failed to reason about.
        print(f"classifier failed ({exc}) -> running everything", file=sys.stderr)
        print("docs_only=false")
        print("dotnet=true")
        print("client=true")
        sys.exit(0)
