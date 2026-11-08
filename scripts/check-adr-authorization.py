#!/usr/bin/env python3
"""Require a collaborator's explicit authorization for a change to a decision record.

The decision records in docs/adr/ are the rules the decision-record gate
holds every pull request to, so changing one changes the rules. A PR that adds,
edits, renames or deletes a file under docs/adr/ passes this check only
when a repository collaborator with write, maintain or admin permission has
posted, as a PR comment or a review, a line that reads exactly

    ADR authorized <head sha>

where <head sha> names a commit (7 to 40 hex digits of it), normally the PR
head at the time; any commit GitHub resolves counts, since only its record
content is compared. Naming the commit binds the authorization to the content of
the records: it holds for a later head as long as every decision-record path the
PR changes has the same content (git blob) at that head as at the named commit,
so a push that only changes code or tests needs no new authorization, and a push
that changes the content of one of those records (an edit, a new record, or the
deletion of a record the PR still changes) does. The comparison is by
content, not by a diff between the two commits, so a merge of the base branch
or a rebase that leaves the records alone keeps the authorization. A named
commit GitHub cannot resolve does not count. The author may authorize their own
PR; the authorization is still an explicit act on the record. A PR that touches
no decision record passes at once.

The result is posted as the commit status `adr-authorization` on the head, so
the check can be re-evaluated by a comment event as well as by a push. The
workflow (.github/workflows/decision-records.yml) runs this file from the base
branch, never from the PR.

Usage:
    check-adr-authorization.py --repo OWNER/NAME --pr N [--post-status]
"""

import json
import re
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from adr_grammar import DECISION_DIR  # noqa: E402  (the one owner of the record directory)

CONTEXT = "adr-authorization"
DECISION_PREFIX = DECISION_DIR + "/"
AUTHORIZE_RE = re.compile(r"^[ \t]*ADR authorized ([0-9a-fA-F]{7,40})[ \t]*$", re.M)
AUTHORIZING_PERMISSIONS = {"admin", "maintain", "write"}
# GitHub limits a commit status description to 140 characters.
MAX_DESCRIPTION = 140


def aged_description(sha, names, head):
    """Name the records that differ since an authorization that no longer covers the head.

    The instruction ends the text, so it is never cut: the names give way to a
    count when they do not fit or when one holds a control or bidi character
    (the names are PR-controlled and reach the job log).
    """
    prefix, suffix = "Authorized at %s; " % sha[:12], ": comment 'ADR authorized %s'" % head[:12]
    forms = ["%d records changed since" % len(names)]
    if all(n.isprintable() for n in names):
        forms.insert(0, ", ".join(names) + " changed since")
    for changed in forms:
        if len(prefix + changed + suffix) <= MAX_DESCRIPTION:
            return prefix + changed + suffix
    return (prefix + suffix)[:MAX_DESCRIPTION]


def decide(changed, head_sha, statements, permission_of, records_at=lambda _sha: None):
    """Return (state, description) for one PR.

    changed: every path the PR adds, edits, deletes or renames (old and new
    names). statements: [(login, body)] for every PR comment and review.
    permission_of: login -> the login's repository permission, or None.
    records_at: sha -> {path: blob sha} for the decision records at that
    commit, or None when the commit cannot be resolved.
    """
    records = sorted({p for p in changed if p.startswith(DECISION_PREFIX)})
    if not records:
        return "success", "No decision record changed"
    head = head_sha.lower()
    at_head = None
    aged = None  # (sha, names of the records that differ at the head) of the last aged authorization in statement order (issue comments, then reviews)
    for login, body in statements:
        if not login or login.endswith("[bot]"):
            continue
        for sha in AUTHORIZE_RE.findall(body or ""):
            if permission_of(login) not in AUTHORIZING_PERMISSIONS:
                continue
            if head.startswith(sha.lower()):
                return "success", ("Decision record change authorized by @%s for %s" % (login, head[:12]))[:MAX_DESCRIPTION]
            if at_head is None:
                at_head = records_at(head)
            at_sha = records_at(sha.lower())
            if at_head is not None and at_sha is not None and all(at_sha.get(r) == at_head.get(r) for r in records):
                return "success", ("Authorized by @%s at %s; the changed records are the same at %s"
                                   % (login, sha.lower()[:12], head[:12]))[:MAX_DESCRIPTION]
            if at_head is not None and at_sha is not None:
                aged = (sha.lower(), [r.rsplit("/", 1)[-1] for r in records if at_sha.get(r) != at_head.get(r)])
    if aged:
        return "failure", aged_description(aged[0], aged[1], head)
    return "failure", ("%d decision record file(s) changed: a collaborator must comment 'ADR authorized %s'"
                       % (len(records), head[:12]))[:MAX_DESCRIPTION]


def gh(*args):
    r = subprocess.run(["gh", *args], capture_output=True, text=True)
    if r.returncode != 0:
        raise RuntimeError("gh %s failed: %s" % (" ".join(args), r.stderr.strip()))
    return r.stdout


def gh_list(path):
    """Every item of a paginated list endpoint, one JSON object per line."""
    out = gh("api", "--paginate", path, "--jq", ".[] | tojson")
    return [json.loads(line) for line in out.splitlines() if line.strip()]


def permission_lookup(repo):
    cache = {}

    def permission_of(login):
        if login not in cache:
            r = subprocess.run(["gh", "api", "repos/%s/collaborators/%s/permission" % (repo, login),
                                "--jq", ".permission"], capture_output=True, text=True)
            # 404 for a user with no access: not a collaborator.
            cache[login] = r.stdout.strip() if r.returncode == 0 else None
        return cache[login]

    return permission_of


def records_lookup(repo, run=subprocess.run):
    """sha -> {path: object sha} for every entry under docs/adr/ at that commit, or None.

    The whole subtree, not only its direct files: a record added in a
    subdirectory, a symlink or a submodule entry must differ from its absence.
    None (the authorization does not count) when the commit cannot be resolved,
    a call fails, or the tree listing is truncated.
    """
    cache = {}
    parent, name = DECISION_PREFIX.rstrip("/").rsplit("/", 1)

    def api(path):
        r = run(["gh", "api", "repos/%s/%s" % (repo, path)], capture_output=True, text=True)
        if r.returncode != 0:
            return "404" if "(HTTP 404)" in r.stderr else None
        try:
            return json.loads(r.stdout)
        except ValueError:
            return None

    def snapshot(sha):
        commit = api("commits/%s" % sha)
        full = commit.get("sha", "") if isinstance(commit, dict) else ""
        if not re.fullmatch(r"[0-9a-f]{40}", full):
            return None
        listing = api("contents/%s?ref=%s" % (parent, full))
        if listing == "404":
            return {}  # no docs/ directory at that commit, so no record at all
        if not isinstance(listing, list):
            return None
        directory = next((e for e in listing if isinstance(e, dict) and e.get("name") == name), None)
        if directory is None:
            return {}
        if directory.get("type") != "dir":
            return None
        tree = api("git/trees/%s?recursive=1" % directory.get("sha", ""))
        if not isinstance(tree, dict) or tree.get("truncated") or not isinstance(tree.get("tree"), list):
            return None
        return {DECISION_PREFIX + e["path"]: e.get("sha") for e in tree["tree"]
                if isinstance(e, dict) and e.get("type") != "tree" and e.get("path")}

    def records_at(sha):
        if sha not in cache:
            cache[sha] = snapshot(sha)
        return cache[sha]

    return records_at


def run(repo, pr, post_status):
    head_sha = gh("api", "repos/%s/pulls/%s" % (repo, pr), "--jq", ".head.sha").strip()
    changed = []
    for f in gh_list("repos/%s/pulls/%s/files?per_page=100" % (repo, pr)):
        changed.append(f.get("filename", ""))
        if f.get("previous_filename"):
            changed.append(f["previous_filename"])
    statements = [(c.get("user", {}).get("login", ""), c.get("body", ""))
                  for c in gh_list("repos/%s/issues/%s/comments?per_page=100" % (repo, pr))]
    statements += [(r.get("user", {}).get("login", ""), r.get("body", ""))
                   for r in gh_list("repos/%s/pulls/%s/reviews?per_page=100" % (repo, pr))]
    state, description = decide(changed, head_sha, statements, permission_lookup(repo), records_lookup(repo))
    print("%s: %s on %s: %s" % (CONTEXT, state, head_sha[:12], description))
    if post_status:
        gh("api", "-X", "POST", "repos/%s/statuses/%s" % (repo, head_sha),
           "-f", "state=%s" % state, "-f", "context=%s" % CONTEXT, "-f", "description=%s" % description)
    return 0


def main(argv):
    repo = pr = None
    args = iter(argv)
    for arg in args:
        if arg == "--repo":
            repo = next(args, None)
        elif arg == "--pr":
            pr = next(args, None)
    if not repo or not pr or not re.fullmatch(r"[\w.-]+/[\w.-]+", repo) or not re.fullmatch(r"\d+", pr):
        print("usage: check-adr-authorization.py --repo OWNER/NAME --pr N [--post-status]",
              file=sys.stderr)
        return 2
    return run(repo, pr, "--post-status" in argv)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
