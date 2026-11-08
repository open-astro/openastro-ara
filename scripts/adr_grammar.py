"""The one owner of the decision-record grammar (docs/adr/README.md).

Imported by check-adr-shape.py, check-adr-scope.py and check-adr-authorization.py,
so the directory, the file-name pattern, the Status pattern, the sections, the
rule-line pattern, the exception words and the glob matcher exist once.
Not a command: the importable name has underscores, the commands have dashes.
"""

import re
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

DECISION_DIR = "docs/adr"
DECISION_FILE_RE = re.compile(r"^(\d{4})-[a-z0-9]+(?:-[a-z0-9]+)*\.md$")
DECISION_STATUS_RE = re.compile(r"^Status: (proposed|accepted|superseded by (\d{4})|retired \(#\d+\))$")
DECISION_SECTIONS = ("Context", "Decision", "Rules", "Alternatives rejected", "Consequences", "Links")
# How to reverse is required in every record, but may sit anywhere among the sections.
DECISION_OPTIONAL_SECTIONS = ("How to reverse",)
DECISION_RULE_RE = re.compile(
    r"^(?P<n>\d+)\. (?P<text>.+?) Scope: (?P<scope>`[^`]+`(?:, `[^`]+`)*)\. Checked by: (?P<check>.+?)\."
    r"(?P<allowed> <!-- allowed -->)?$")
DECISION_INDEX_LINK_RE = re.compile(r"^- \[[^\]]+\]\((\d{4}-[^)]+\.md)\)( \(retired\))?$", re.M)
# A rule states its own bound; a carve-out for code that deviates is a defect with
# an issue, never a clause. A rule whose bound needs one of these words ends with
# ` <!-- allowed -->`, which check-adr-scope.py reads as any other rule.
DECISION_EXCEPTION_WORD_RE = re.compile(r"\b(except|unless|until|for now)\b", re.I)
CODE_SPAN_ONLY_RE = re.compile(r"`([^`]+)`")


def glob_match(path, pattern):
    """`*` stays inside one directory, `**` crosses them."""
    expression = re.escape(pattern).replace(r"\*\*/", "(?:.*/)?")
    expression = expression.replace(r"\*\*", ".*").replace(r"\*", "[^/]*")
    return re.fullmatch(expression, path) is not None


def decision_sections(text):
    """[(title, body)] for every `## ` section, in order."""
    sections = []
    current = None
    for line in text.splitlines():
        if line.startswith("## "):
            current = [line[3:].strip(), []]
            sections.append(current)
        elif current is not None:
            current[1].append(line)
    return [(title, "\n".join(body)) for title, body in sections]


def tracked_paths(root=ROOT):
    out = subprocess.run(["git", "-C", str(root), "ls-files", "-z"], capture_output=True, text=True, check=True)
    return [p for p in out.stdout.split("\0") if p]


def record_findings(records, readme, tracked):
    """Every shape problem in `records` ({file name: text}) and the `readme` index.

    tracked: repo-relative paths the Rules scope globs are matched against."""
    findings = []
    retired = set()
    superseded_by = {}
    for name in sorted(records):
        text = records[name]
        m = DECISION_FILE_RE.match(name)
        where = "%s/%s" % (DECISION_DIR, name)
        if not m:
            findings.append("%s: the name is not NNNN-<lowercase-slug>.md" % where)
            continue
        lines = text.splitlines()
        if not lines or not lines[0].startswith("# "):
            findings.append("%s: the first line is not a `# ` title" % where)
        status = DECISION_STATUS_RE.match(lines[2]) if len(lines) > 2 else None
        if not status:
            findings.append("%s: line 3 is not `Status: proposed`, `Status: accepted`, "
                            "`Status: superseded by NNNN` or `Status: retired (#NNNN)`" % where)
            continue
        if status.group(2):
            superseded_by.setdefault(status.group(2), name)
            if not any(n.startswith(status.group(2) + "-") for n in records):
                findings.append("%s: superseded by %s, which does not exist" % (where, status.group(2)))
        is_retired = status.group(1).startswith("retired")
        if is_retired:
            retired.add(name)
        no_rules = bool(status.group(2)) or is_retired
        sections = decision_sections(text)
        titles = [t for t, _ in sections]
        for t in titles:
            if t not in DECISION_SECTIONS + DECISION_OPTIONAL_SECTIONS:
                findings.append("%s: unknown section `## %s` (README.md lists the allowed ones)" % (where, t))
        if "How to reverse" not in titles:
            findings.append("%s: missing section `## How to reverse`" % where)
        if is_retired and "Rules" in titles:
            findings.append("%s: a retired record drops its `## Rules` section" % where)
        required = [t for t in DECISION_SECTIONS if not (t == "Rules" and no_rules)]
        present = [t for t in titles if t in required]
        for t in required:
            if t not in titles:
                findings.append("%s: missing section `## %s`" % (where, t))
        if present != [t for t in required if t in present]:
            findings.append("%s: sections are out of order; expected %s" % (where, ", ".join(required)))
        for t in sorted(set(titles)):
            if titles.count(t) > 1:
                findings.append("%s: section `## %s` appears %d times" % (where, t, titles.count(t)))
        rules = dict(sections).get("Rules")
        if rules is None or is_retired:
            continue
        expected = 1
        for line in rules.splitlines():
            if not re.match(r"^\d+\. ", line):
                continue
            rule = DECISION_RULE_RE.match(line)
            if not rule:
                findings.append("%s: rule line does not end `Scope: `glob`[, `glob`]. Checked by: <check>.`: %s"
                                % (where, line[:80]))
                expected += 1
                continue
            if int(rule.group("n")) != expected:
                findings.append("%s: rule %s is out of sequence (expected %d)" % (where, rule.group("n"), expected))
            expected = int(rule.group("n")) + 1
            word = DECISION_EXCEPTION_WORD_RE.search(rule.group("text"))
            if word and not rule.group("allowed"):
                findings.append("%s: rule %s says `%s`; a carve-out is a defect with an issue, not a rule clause "
                                "(end the line with `<!-- allowed -->` when the bound is the rule)"
                                % (where, rule.group("n"), word.group(1)))
            if rule.group("check") == "review":
                findings.append("%s: rule %s says a bare `Checked by: review`; write `review (<what to look for>)`"
                                % (where, rule.group("n")))
            for pattern in CODE_SPAN_ONLY_RE.findall(rule.group("scope")):
                if pattern == "**":
                    findings.append("%s: rule %s scope `**` is not allowed; name the files" % (where, rule.group("n")))
                elif not any(glob_match(p, pattern) for p in tracked):
                    findings.append("%s: rule %s scope `%s` matches no tracked file" % (where, rule.group("n"), pattern))
        if expected == 1:
            findings.append("%s: `## Rules` has no numbered rule" % where)
    index_lines = DECISION_INDEX_LINK_RE.findall(readme)
    indexed = [n for n, _ in index_lines]
    for n, suffix in index_lines:
        if bool(suffix) != (n in retired):
            findings.append("%s/README.md: the index line for %s %s the `(retired)` suffix"
                            % (DECISION_DIR, n, "lacks" if n in retired else "carries"))
    record_names = sorted(n for n in records if DECISION_FILE_RE.match(n))
    if indexed != record_names:
        detail = []
        if set(record_names) - set(indexed):
            detail.append("not indexed: " + ", ".join(sorted(set(record_names) - set(indexed))))
        if set(indexed) - set(record_names):
            detail.append("indexed but absent: " + ", ".join(sorted(set(indexed) - set(record_names))))
        if not detail:
            detail.append("index order differs from the record numbers")
        findings.append("%s/README.md: the index does not list every record once, in order (%s)"
                        % (DECISION_DIR, "; ".join(detail)))
    return findings


def read_records(root=ROOT):
    directory = Path(root) / DECISION_DIR
    return {p.name: p.read_text(encoding="utf-8", errors="replace")
            for p in sorted(directory.glob("*.md")) if p.name != "README.md"}
