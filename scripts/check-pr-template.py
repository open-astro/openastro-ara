#!/usr/bin/env python3
"""Require the PR body to follow .github/pull_request_template.md.

GitHub fills the template in only when a PR is opened from the web compare
page with an empty body. A body passed up front (`gh pr create --body`, the
API, an agent) skips it, so the template's sections went missing on PRs that
were otherwise complete.

The rules are read from the template itself, so editing the template changes
the check with no edit here:

  * Every `## <Heading>` of the template appears in the body (case and
    surrounding spaces ignored; order not checked).
  * A section whose template holds `- [ ]` items (the checklist) keeps at least
    one `- [ ]` or `- [x]` item.
  * Every other section holds at least one line of its own: not blank, not a
    bare `-` or `>` marker, and not a line copied unchanged from the template's
    placeholder text in that section.

HTML comments are ignored on both sides, and `## ` lines inside fenced code
blocks are not headings. The script checks shape, not quality; review reads
the content.

The PR body is read from the PR_BODY environment variable (or --body-file),
never from a command line, because it is untrusted text.

Usage:
    check-pr-template.py [--body-file FILE] [--template FILE]
"""

import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TEMPLATE = os.path.join(ROOT, ".github", "pull_request_template.md")


COMMENT_RE = re.compile(r"<!--.*?-->", re.S)
HEADING_RE = re.compile(r"^##\s+(.+?)\s*$")
FENCE_RE = re.compile(r"^\s*(```|~~~)")
CHECKBOX_RE = re.compile(r"^\s*[-*]\s+\[[ xX]\]\s+\S")
FILLER_RE = re.compile(r"^[->*\s]*$")


def sections(text):
    """Return [(normalized heading, display heading, lines)] for each `## ` section."""
    text = COMMENT_RE.sub("", text.replace("\r\n", "\n"))
    out = []
    in_fence = False
    for line in text.split("\n"):
        if FENCE_RE.match(line):
            in_fence = not in_fence
        m = None if in_fence else HEADING_RE.match(line)
        if m:
            title = m.group(1)
            out.append((" ".join(title.lower().split()), title, []))
        elif out:
            out[-1][2].append(line.strip())
    return out


def check(body, template):
    """Return the template violations of one PR body."""
    if not body.strip():
        return ["the PR body is empty"]
    found = {}
    for key, _, lines in sections(body):
        found.setdefault(key, lines)
    errors = []
    for key, title, tlines in sections(template):
        # "A or B" offers a choice, so the body may name either path on its own
        names = [key] + key.split(" or ") if " or " in key else [key]
        hit = next((n for n in names if n in found), None)
        if hit is None:
            errors.append("missing section '## %s'" % title)
            continue
        lines = found[hit]
        if any(CHECKBOX_RE.match(t) for t in tlines):
            if not any(CHECKBOX_RE.match(line) for line in lines):
                errors.append("section '## %s' has no '- [ ]' or '- [x]' items" % title)
            continue
        placeholders = {t for t in tlines if t}
        own = [line for line in lines if not FILLER_RE.match(line) and line not in placeholders]
        if not own:
            errors.append("section '## %s' is empty or holds only the template's placeholder text" % title)
    return errors


def main(argv):
    body_file = None
    template = TEMPLATE
    args = iter(argv)
    for arg in args:
        if arg == "--body-file":
            body_file = next(args, None)
        elif arg == "--template":
            template = next(args, None)
        else:
            template = None
        if template is None:
            print("usage: check-pr-template.py [--body-file FILE] [--template FILE]", file=sys.stderr)
            return 2
    if body_file:
        with open(body_file, encoding="utf-8") as f:
            body = f.read()
    else:
        body = os.environ.get("PR_BODY", "")
    with open(template, encoding="utf-8") as f:
        template_text = f.read()
    errors = check(body, template_text)
    for e in errors:
        print("pr-template: " + e)
    if errors:
        print("Fill in the PR body from .github/pull_request_template.md (%d problem(s))." % len(errors))
        return 1
    print("PR body follows the template.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
