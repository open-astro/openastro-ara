#!/usr/bin/env python3
"""Require a `Falsified by:` line in the PR body for every new test case.

A test case that has never failed pins nothing. Every case a PR adds or
renames must be named in the PR body, under a `Falsified by:` heading, next to
the one production-code mutation that makes it fail:

    Falsified by:
    - "AlbumState.FetchMergesNeverRemoves": client/openastroara_client/lib/album/album_state.dart:212 delete the `rows.remove(id)` guard

Grammar: `- "<case name>": <path>:<line> <change>`. `<path>` must exist in the
checkout and be production code: `client/openastroara_client/lib/`, a daemon
project directory (`OpenAstroAra.*` whose name does not end in `.Test`,
`.Tests` or `.TestHarness`), `scripts/` outside `scripts/tests/`, or
`.github/workflows/`. A test-helper path (the client `test/` tree, the C# test
projects and the harness, `scripts/tests/`) is accepted only when the case
name says the helper is the subject (`Fake*`, `Mock*`, `TestHarness`).
`<change>` has at least 3 words.

What counts as a new case (read from `git diff <merge-base>...HEAD`; the name
must be absent from that file at the merge-base, unless the base declared it in
a file that no longer does it, a moved case; a renamed case is new):
  * Dart (`client/openastroara_client/test/**.dart`): the first argument of
    `test(` or `testWidgets(`, one string literal or adjacent literals (either
    quote, raw `r'...'` allowed, may start on the next line). `$` stays as
    written.
  * C# (`*.Test`, `*.Tests` projects, not `*.TestHarness`): `Class.Method` for
    a method with `[Test]`, `[TestCase(...)]`, `[TestCaseSource(...)]`,
    `[Fact]` or `[Theory]`. Attributes may stack. `TestCase` rows count once.
  * Python (`scripts/tests/*.py`): `Class.method` for `def test_*` in a class.
Comments are stripped before reading.

A line that names an existing case at HEAD is accepted (evidence for a fix to
an old case). A line that names no case at HEAD is stale.

The script checks shape, not truth; review applies the mutation.

The PR body is read from the PR_BODY environment variable (or --body-file),
never from a command line, because it is untrusted text.

Usage:
    check-falsified-by.py [--base REF] [--body-file FILE]
"""

import ast
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

DART_DIR = "client/openastroara_client/test/"
PY_DIR = "scripts/tests/"
TEST_PROJECT_RE = re.compile(r"^OpenAstroAra(?:\.[^/]+)?\.Tests?/")
HARNESS_PREFIX = "OpenAstroAra.TestHarness/"
DAEMON_RE = re.compile(r"^OpenAstroAra\.[^/]+/")
CLIENT_LIB = "client/openastroara_client/lib/"

HELPER_SUBJECT_RE = re.compile(r"\b(?:Fake|Mock)[A-Za-z0-9_]*|\bTestHarness\b")

HEADING_RE = re.compile(r"^\s*(?:#{1,6}\s*)?(?:\*\*|__)?\s*falsified by\s*:?\s*(?:\*\*|__)?\s*:?\s*$",
                        re.IGNORECASE)
LINE_RE = re.compile(r'^\s*[-*]\s+"(?P<name>.+)":\s+(?P<path>\S+?):(?P<line>\d+)\s+(?P<change>\S.*)$')


# --- reading test cases ----------------------------------------------------

def strip_comments(text, csharp=False):
    """`text` without // and /* */ comments; string literals are left alone."""
    out, i, n = [], 0, len(text)
    while i < n:
        c = text[i]
        two = text[i:i + 2]
        if two == "//":
            while i < n and text[i] != "\n":
                i += 1
        elif two == "/*":
            j = text.find("*/", i + 2)
            i = n if j < 0 else j + 2
            out.append(" ")
        elif c in "\"'":
            j = string_end(text, i, csharp, raw=(i > 0 and text[i - 1] == "r" and not csharp),
                           verbatim=(csharp and i > 0 and text[i - 1] == "@" or csharp and text[i - 2:i] in ("$@", "@$")))
            out.append(text[i:j])
            i = j
        else:
            out.append(c)
            i += 1
    return "".join(out)


def string_end(text, i, csharp, raw=False, verbatim=False):
    """Index just past the string literal that opens at text[i] (a quote)."""
    q = text[i]
    n = len(text)
    if not csharp and text.startswith(q * 3, i):
        j = text.find(q * 3, i + 3)
        return n if j < 0 else j + 3
    j = i + 1
    while j < n:
        c = text[j]
        if c == "\\" and not raw and not verbatim:
            j += 2
            continue
        if c == q:
            if verbatim and text[j + 1:j + 2] == q:
                j += 2
                continue
            return j + 1
        if c == "\n" and not verbatim:
            return j
        j += 1
    return n


DART_CALL_RE = re.compile(r"(?<![\w.$])(?:testWidgets|test)\s*\(\s*")
DART_LITERAL_RE = re.compile(r"""(?P<raw>r?)(?P<q>'''|\"\"\"|'|")""")


def dart_literal_run(text, i):
    """The concatenated body of adjacent string literals starting at text[i], or None."""
    parts = []
    while True:
        m = DART_LITERAL_RE.match(text, i)
        if not m:
            break
        q = m.group("q")
        start = m.end()
        end = string_end(text, m.start() + len(m.group("raw")), False, raw=bool(m.group("raw")))
        parts.append(text[start:end - len(q)])
        i = end
        while i < len(text) and text[i] in " \t\r\n":
            i += 1
    return "".join(parts) if parts else None


def dart_cases(text):
    text = strip_comments(text)
    names = set()
    for m in DART_CALL_RE.finditer(text):
        name = dart_literal_run(text, m.end())
        if name:
            names.add(name)
    return names


TEST_ATTR_RE = re.compile(r"[\[,]\s*(?:Test|TestCase|TestCaseSource|Fact|Theory)\b(?=\s*[\](,])")
CLASS_RE = re.compile(r"\bclass\s+(\w+)")


def skip_balanced(text, i, open_c, close_c):
    """Index just past the group that opens at text[i], skipping string literals."""
    depth, n = 0, len(text)
    while i < n:
        c = text[i]
        if c == '"':
            verbatim = text[i - 1:i] == "@" or text[i - 2:i] in ("$@", "@$")
            i = string_end(text, i, True, verbatim=verbatim)
            continue
        if c == "'":
            i = string_end(text, i, True)
            continue
        if c == open_c:
            depth += 1
        elif c == close_c:
            depth -= 1
            if depth == 0:
                return i + 1
        i += 1
    return n


def csharp_cases(text):
    text = strip_comments(text, csharp=True)
    classes = [(m.start(), m.group(1)) for m in CLASS_RE.finditer(text)]
    names, resume = set(), 0
    for m in TEST_ATTR_RE.finditer(text):
        if m.start() < resume:
            continue
        # walk back to the `[` that opens this attribute list
        start = text.rfind("[", 0, m.end())
        if text[m.start()] == "[":
            start = m.start()
        end = skip_balanced(text, start, "[", "]")
        while True:
            j = end
            while j < len(text) and text[j] in " \t\r\n":
                j += 1
            if text[j:j + 1] != "[":
                break
            end = skip_balanced(text, j, "[", "]")
        resume = end
        sig = text[end:end + 400].split("(", 1)[0]
        name = re.findall(r"(\w+)\s*(?:<[^<>]*>)?\s*$", sig)
        if not name or "(" not in text[end:end + 400]:
            continue
        owner = [c for pos, c in classes if pos < start]
        if owner:
            names.add("%s.%s" % (owner[-1], name[0]))
    return names


def python_cases(text):
    try:
        tree = ast.parse(text)
    except (SyntaxError, ValueError):
        return set()
    return {"%s.%s" % (c.name, f.name)
            for c in ast.walk(tree) if isinstance(c, ast.ClassDef)
            for f in c.body if isinstance(f, (ast.FunctionDef, ast.AsyncFunctionDef)) and f.name.startswith("test_")}


def kind(path):
    if path.startswith(DART_DIR) and path.endswith(".dart"):
        return dart_cases
    if TEST_PROJECT_RE.match(path) and path.endswith(".cs"):
        return csharp_cases
    if path.startswith(PY_DIR) and path.endswith(".py") and "/" not in path[len(PY_DIR):]:
        return python_cases
    return None


def case_names_in(path, text):
    reader = kind(path)
    return reader(text) if reader else set()


# --- the PR body -----------------------------------------------------------

def parse_body(body):
    """([(name, path, change)], [error]) from the `Falsified by:` section."""
    lines = body.replace("\r\n", "\n").split("\n")
    start = next((i for i, ln in enumerate(lines) if HEADING_RE.match(ln)), None)
    if start is None:
        return [], []
    entries, errors = [], []
    for ln in lines[start + 1:]:
        if not ln.strip():
            continue
        if re.match(r"^\s*#{1,6}\s", ln) or not re.match(r"^\s*[-*]\s", ln):
            break
        m = LINE_RE.match(ln)
        if not m:
            errors.append('malformed line (want `- "<case>": <path>:<line> <change>`): %r' % ln.strip())
            continue
        if len(m.group("change").split()) < 3:
            errors.append("change has fewer than 3 words for %r" % m.group("name"))
            continue
        entries.append((m.group("name"), m.group("path"), m.group("change")))
    return entries, errors


def is_production(path):
    if path.startswith((CLIENT_LIB, ".github/workflows/")):
        return True
    if path.startswith("scripts/") and not path.startswith(PY_DIR):
        return True
    return bool(DAEMON_RE.match(path)) and not TEST_PROJECT_RE.match(path) and not path.startswith(HARNESS_PREFIX)


def is_helper(path):
    return (path.startswith((DART_DIR, PY_DIR, HARNESS_PREFIX)) or bool(TEST_PROJECT_RE.match(path)))


def path_error(name, path, exists):
    if not exists(path):
        return "%r: path does not exist: %s" % (name, path)
    if is_production(path):
        return None
    if is_helper(path):
        if HELPER_SUBJECT_RE.search(name):
            return None
        return "%r: %s is a test helper and the case name does not say the helper is its subject" % (name, path)
    return ("%r: %s is not production code (the client lib, a daemon project, scripts/ outside scripts/tests/, "
            ".github/workflows/)" % (name, path))


def evaluate(new_cases, entries, body_errors, exists, head_cases=frozenset()):
    """Every problem found, given the set of new case names and the parsed body."""
    errors = list(body_errors)
    claimed = {}
    for name, path, _change in entries:
        claimed.setdefault(name, path)
    for name in sorted(new_cases):
        if name not in claimed:
            errors.append("new test case has no `Falsified by:` line: %r" % name)
    for name, path, _change in entries:
        if name not in new_cases and name not in head_cases:
            errors.append("`Falsified by:` line names no test case at HEAD (stale or typo): %r" % name)
            continue
        err = path_error(name, path, exists)
        if err:
            errors.append(err)
    return errors


# --- git plumbing ----------------------------------------------------------

def git(*args, check=True):
    r = subprocess.run(["git", "-C", ROOT, *args], capture_output=True, text=True, errors="replace")
    if check and r.returncode != 0:
        raise RuntimeError("git %s failed: %s" % (" ".join(args), r.stderr.strip()))
    return r.stdout


MERGE_PARENT = "merge-parent"
DEFAULT_BASES = ("origin/master", "upstream/master", "master")


def resolve_base(explicit):
    """Merge-base of HEAD and the first match among `explicit` and the defaults.

    `explicit == "merge-parent"` (the CI job) means HEAD^1 when HEAD is a merge
    commit: on a pull_request run HEAD is refs/pull/N/merge, built against the
    CURRENT tip of the base branch, so its first parent is the exact base and a
    base that moved after the PR opened adds none of the cases other PRs landed.
    Anywhere else HEAD has no second parent and the usual defaults apply.
    """
    if explicit == MERGE_PARENT:
        explicit = None
        if subprocess.run(["git", "-C", ROOT, "rev-parse", "--verify", "-q", "HEAD^2"],
                          capture_output=True).returncode == 0:
            r = subprocess.run(["git", "-C", ROOT, "rev-parse", "HEAD^1"], capture_output=True, text=True)
            if r.returncode == 0:
                return r.stdout.strip()
    for ref in ([explicit] if explicit else []) + list(DEFAULT_BASES):
        r = subprocess.run(["git", "-C", ROOT, "merge-base", ref, "HEAD"], capture_output=True, text=True)
        if r.returncode == 0:
            return r.stdout.strip()
    raise RuntimeError("cannot resolve a diff base (tried %s)" % (explicit or ", ".join(DEFAULT_BASES)))


def gather_new_cases(base):
    """(new, head_cases) name sets for the diff `base...HEAD`.

    Base names are kept per path, so a new case named like a case that stays in
    another file is still new. A name the base declared in a file that no longer
    declares it (cut and pasted, or carried by a renamed or deleted file) is moved."""
    changed = set(git("diff", "-z", "--name-only", "--diff-filter=AMR", base + "...HEAD").split("\0"))
    base_by_path = {}
    for path in git("ls-tree", "-r", "-z", "--name-only", base).split("\0"):
        if path and kind(path):
            base_by_path[path] = case_names_in(path, git("show", "%s:%s" % (base, path), check=False))
    head_by_path = {}
    for path in git("ls-files", "-z").split("\0"):
        full = os.path.join(ROOT, path)
        if path and kind(path) and os.path.isfile(full):
            with open(full, encoding="utf-8", errors="replace") as fh:
                head_by_path[path] = case_names_in(path, fh.read())
    moved = set()
    for path, names in base_by_path.items():
        moved |= names - head_by_path.get(path, set())
    new, head_cases = set(), set()
    for path, names in head_by_path.items():
        head_cases |= names
        if path in changed:
            new |= names - base_by_path.get(path, set()) - moved
    return new, head_cases


def main(argv):
    base_arg, body_file = None, None
    args = iter(argv)
    for a in args:
        if a == "--base":
            base_arg = next(args, None)
        elif a == "--body-file":
            body_file = next(args, None)
        else:
            print("unknown argument: %s" % a, file=sys.stderr)
            return 2
    if body_file:
        with open(body_file, encoding="utf-8", errors="replace") as fh:
            body = fh.read()
    else:
        body = os.environ.get("PR_BODY", "")
    try:
        base = resolve_base(base_arg or os.environ.get("FALSIFIED_BASE"))
        new, head_cases = gather_new_cases(base)
    except RuntimeError as exc:
        print("falsified-by: ERROR: %s" % exc, file=sys.stderr)
        return 2
    entries, body_errors = parse_body(body)
    errors = evaluate(new, entries, body_errors, lambda p: os.path.exists(os.path.join(ROOT, p)), head_cases)
    if not new and not entries and not errors:
        print("falsified-by: no new test cases; nothing to check")
        return 0
    if errors:
        for e in errors:
            print("FALSIFIED-BY: " + e)
        print("falsified-by: FAIL (%d problem(s)); see \"Pull request body\" in AGENTS.md" % len(errors))
        return 1
    print("falsified-by: OK (%d new case(s), each with a mutation line)" % len(new))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
