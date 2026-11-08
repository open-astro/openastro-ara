#!/usr/bin/env python3
"""Map a pull request's changed files to the decision-record rules they fall under.

Every decision record in docs/adr/ has a `## Rules` section whose numbered
rules each name a `Scope:` of repository globs (docs/adr/README.md). A
changed file is under a rule when one of the rule's globs matches it.

Four modes:

  --list   reads changed paths from stdin, one per line, and prints one line
           per record in scope, `<record file>: rules <n>, <n>`. The review
           bot runs the default branch's copy over the base branch's records
           and reads those rules against the diff
           (.github/workflows/claude-review.yml).

  default  the PR-body gate: the `## Decision records` section of the PR body
           (read from PR_BODY or --body-file, never the command line) must name,
           by its four-digit number, every decision record the PR adds, edits,
           renames or deletes. It also prints the records in scope, for the
           author. The diff base is resolved as check-falsified-by.py does it.

  --checklist SCOPE REVIEW
           the review bot's gate before it posts: SCOPE holds the --list
           output ("none" when nothing is in scope) and REVIEW the bot's
           review-comment.md. Every listed rule needs a checklist line
           `- <record file> rule <n>: pass|defect|n/a: <reason>`, a rule may
           not get two verdicts, and a defect line needs the "Issues found"
           sign-off as the last line (bold, italics or a closing period or
           exclamation mark around it still count). Any finding exits 1, so nothing posts.

  --skeleton SCOPE
           prints one `- <record file> rule <n>: VERDICT: reason` line per
           listed rule, for the review prompt to fill in; a malformed
           scope line exits 1.

Usage:
    check-adr-scope.py --list [--root DIR] < paths
    check-adr-scope.py --checklist SCOPE_FILE REVIEW_FILE
    check-adr-scope.py --skeleton SCOPE_FILE
    check-adr-scope.py [--body-file FILE] [--base REV]
"""

import os
import re
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from adr_grammar import (  # noqa: E402  (the one owner of the Rules grammar)
    DECISION_DIR, DECISION_FILE_RE, DECISION_RULE_RE, CODE_SPAN_ONLY_RE, ROOT,
    decision_sections as _decision_sections, glob_match as _glob_match)
SECTION = "decision records"
# A record number, not an issue or PR number such as #1415 (#1416 review).
RECORD_NUMBER_RE = re.compile(r"(?<![#\d])\b(\d{4})\b")
HEADING_RE = re.compile(r"^##\s+(.+?)\s*$")
COMMENT_RE = re.compile(r"<!--.*?-->", re.S)
SCOPE_LINE_RE = re.compile(r"^(\d{4}-[a-z0-9-]+\.md): rules (\d+(?:, \d+)*)$")
CHECKLIST_RE = re.compile(r"^\s*[-*]\s*`?(\d{4}-[a-z0-9-]+\.md)`?\s+rule\s+(\d+)\s*:\s*(pass|defect|n/a)\s*:\s*\S", re.I)
ISSUES_SIGN_OFF = "\u26a0\ufe0f Issues found"


def sign_off(line):
    """The line without the Markdown emphasis or closing period a sign-off may carry."""
    return line.strip().strip("*_ ").rstrip(".!").strip("*_ ")


def record_rules(records):
    """{record file: [(rule number, [glob])]} for every record that is not superseded or retired."""
    out = {}
    for name, text in sorted(records.items()):
        if not DECISION_FILE_RE.match(name) or re.search(r"^Status: (superseded by|retired)", text, re.M):
            continue
        rules = dict(_decision_sections(text)).get("Rules", "")
        parsed = []
        for line in rules.splitlines():
            m = DECISION_RULE_RE.match(line)
            if m:
                parsed.append((int(m.group("n")), CODE_SPAN_ONLY_RE.findall(m.group("scope"))))
        out[name] = parsed
    return out


def in_scope(changed, rules_by_record):
    """[(record file, [rule number])] for every record with a rule a changed path falls under."""
    result = []
    for record, rules in rules_by_record.items():
        hit = [n for n, globs in rules if any(_glob_match(p, g) for p in changed for g in globs)]
        if hit:
            result.append((record, hit))
    return result


def body_section(body, title=SECTION):
    """The lines of the body's `## <title>` section, or None when it is missing."""
    lines, inside = None, False
    for line in COMMENT_RE.sub("", body.replace("\r\n", "\n")).split("\n"):
        m = HEADING_RE.match(line)
        if m:
            inside = " ".join(m.group(1).lower().split()) == title
            if inside and lines is None:
                lines = []
            continue
        if inside:
            lines.append(line)
    return lines


def body_findings(body, changed):
    section = body_section(body)
    records = sorted({p for p in changed if p.startswith(DECISION_DIR + "/") and DECISION_FILE_RE.match(p.split("/")[-1])})
    if section is None:
        return ["the PR body has no '## Decision records' section"]
    named = set(RECORD_NUMBER_RE.findall("\n".join(section)))
    return ["the '## Decision records' section does not name %s, which this PR changes" % p
            for p in records if p.split("/")[-1][:4] not in named]


def scope_pairs(scope):
    """({(record file, rule number)}, [malformed line]) from --list output, where "none..." lists nothing."""
    pairs, bad = set(), []
    for line in scope.splitlines():
        line = line.strip()
        if not line or line.lower().startswith("none"):
            continue
        m = SCOPE_LINE_RE.match(line)
        if not m:
            bad.append(line)
            continue
        pairs.update((m.group(1), int(n)) for n in m.group(2).split(", "))
    return pairs, bad


def scope_order(scope):
    """[(record file, rule number)] from --list output, in the order it lists them; malformed lines are skipped."""
    pairs = []
    for line in scope.splitlines():
        m = SCOPE_LINE_RE.match(line.strip())
        if m:
            pairs.extend((m.group(1), int(n)) for n in m.group(2).split(", "))
    return pairs


def skeleton_line(pair):
    return "- %s rule %d: VERDICT: reason" % pair


def skeleton(scope):
    """One checklist line per listed rule for the review to fill in, so it never has to expand the list itself (#1655).

    A malformed scope line raises ValueError, as --checklist reports it, so the two never disagree on the rules."""
    bad = scope_pairs(scope)[1]
    if bad:
        raise ValueError("the rule scope has a line that is not `<record file>: rules <n>`: %r" % bad[0])
    return [skeleton_line(pair) for pair in scope_order(scope)]


def checklist_findings(scope, review):
    """What keeps a review from posting: a listed rule with no verdict, two verdicts, or a defect signed off clean."""
    expected, bad = scope_pairs(scope)
    findings = ["the rule scope has a line that is not `<record file>: rules <n>`: %r" % line for line in bad]
    verdicts = {}
    for line in review.splitlines():
        m = CHECKLIST_RE.match(line)
        if m:
            verdicts.setdefault((m.group(1), int(m.group(2))), set()).add(m.group(3).lower())
    findings += ["the review has no checklist line for %s rule %d" % key for key in sorted(expected - set(verdicts))]
    findings += ["the review gives %s rule %d more than one verdict: %s" % (key + (", ".join(sorted(v)),))
                 for key, v in sorted(verdicts.items()) if len(v) > 1]
    lines = [line.strip() for line in review.splitlines() if line.strip()]
    last = lines[-1] if lines else ""
    # Markdown emphasis or a closing period around the sign-off is still the sign-off.
    bare = sign_off(last)
    findings += ["the review marks %s rule %d as a defect but its last line is %r, not the Issues-found sign-off"
                 % (key + (last,)) for key, v in sorted(verdicts.items()) if "defect" in v and bare != ISSUES_SIGN_OFF]
    return findings


def read_records(root):
    directory = Path(root) / DECISION_DIR
    return {p.name: p.read_text(encoding="utf-8", errors="replace") for p in directory.glob("0*.md")}


def changed_since(base):
    out = subprocess.run(["git", "-C", str(ROOT), "diff", "--name-status", "-M", base, "HEAD"],
                         capture_output=True, text=True, check=True).stdout
    paths = []
    for line in out.splitlines():
        parts = line.split("\t")
        paths.extend(parts[1:])
    return paths


def main(argv):
    if argv[:1] == ["--checklist"]:
        if len(argv) != 3:
            print("usage: check-adr-scope.py --checklist SCOPE_FILE REVIEW_FILE", file=sys.stderr)
            return 2
        scope, review = (Path(a).read_text(encoding="utf-8", errors="replace") for a in argv[1:])
        findings = checklist_findings(scope, review)
        for f in findings:
            print("adr-checklist: " + f)
        return 1 if findings else 0
    if argv[:1] == ["--skeleton"]:
        if len(argv) != 2:
            print("usage: check-adr-scope.py --skeleton SCOPE_FILE", file=sys.stderr)
            return 2
        try:
            lines = skeleton(Path(argv[1]).read_text(encoding="utf-8", errors="replace"))
        except ValueError as e:
            print("adr-skeleton: %s" % e, file=sys.stderr)
            return 1
        print("\n".join(lines))
        return 0
    args = iter(argv)
    root, body_file, base = ROOT, None, None
    for arg in args:
        if arg == "--root":
            root = Path(next(args, ""))
        elif arg == "--body-file":
            body_file = next(args, None)
        elif arg == "--base":
            base = next(args, None)
    rules = record_rules(read_records(root))
    if not rules:
        print("check-adr-scope: no decision record found under %s" % (Path(root) / DECISION_DIR), file=sys.stderr)
        return 2
    if "--list" in argv:
        changed = [line.strip() for line in sys.stdin if line.strip()]
        for record, hit in in_scope(changed, rules):
            print("%s: rules %s" % (record, ", ".join(str(n) for n in hit)))
        return 0
    import importlib.util
    spec = importlib.util.spec_from_file_location("check_falsified_by", Path(__file__).resolve().parent / "check-falsified-by.py")
    falsified = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(falsified)
    resolve_base = falsified.resolve_base
    changed = changed_since(resolve_base(base or os.environ.get("FALSIFIED_BASE")))
    if body_file:
        body = Path(body_file).read_text(encoding="utf-8")
    else:
        body = os.environ.get("PR_BODY", "")
    for record, hit in in_scope(changed, rules):
        print("in scope: %s rules %s" % (record, ", ".join(str(n) for n in hit)))
    errors = body_findings(body, changed)
    for e in errors:
        print("decision-records: " + e)
    if errors:
        return 1
    print("Decision records section names every record this PR changes.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
