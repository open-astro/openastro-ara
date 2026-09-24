#!/usr/bin/env bash
# Dump the facts an investigation starts from, for one PR number.
# Usage: scripts/pr-facts.sh <pr-number>
# Read-only: never checks out, labels, comments, or merges.
set -euo pipefail
PR="${1:?usage: pr-facts.sh <pr-number>}"
cd "$(git rev-parse --show-toplevel)"

echo "=== PR #$PR ==="
gh pr view "$PR" --json number,title,author,state,isDraft,isCrossRepository,headRefName,headRepositoryOwner,baseRefName,mergeable,mergeStateStatus,additions,deletions,changedFiles,labels,createdAt,updatedAt \
  -q '"title:      \(.title)
author:     \(.author.login)
state:      \(.state)\(if .isDraft then " (draft)" else "" end)
head:       \(.headRepositoryOwner.login):\(.headRefName) -> \(.baseRefName)\(if .isCrossRepository then "   [FORK]" else "" end)
mergeable:  \(.mergeable) / \(.mergeStateStatus)
size:       +\(.additions) -\(.deletions) in \(.changedFiles) files
labels:     \([.labels[].name] | join(", "))
opened:     \(.createdAt)   updated: \(.updatedAt)"'

echo; echo "=== Body ==="
gh pr view "$PR" --json body -q .body

echo; echo "=== Files ==="
gh pr view "$PR" --json files -q '.files[] | "\(.additions)\t\(.deletions)\t\(.path)"'

echo; echo "=== CI buckets (scripts/classify-changed-paths.py) ==="
gh pr view "$PR" --json files -q '.files[].path' | python3 scripts/classify-changed-paths.py || echo "(classifier unavailable)"

echo; echo "=== Status checks ==="
gh pr view "$PR" --json statusCheckRollup \
  -q '.statusCheckRollup[] | "\(.conclusion // .state // "PENDING")\t\(.name // .context)"' | sort -u

echo; echo "=== Review bot ==="
gh pr view "$PR" --json comments,reviews,isCrossRepository,labels -q '
  (.comments + (.reviews | map({author: .author, body: .body, createdAt: .submittedAt})))
  | map(select(.author.login == "claude[bot]" or .author.login == "github-actions[bot]"))
  | sort_by(.createdAt)
  | if length == 0 then "no bot review posted" else (last | "last bot verdict \(.createdAt):\n\(.body[0:1500])") end'
gh pr view "$PR" --json isCrossRepository,labels -q '
  if .isCrossRepository and (([.labels[].name] | index("safe-to-review")) == null)
  then "NOTE: fork PR without the safe-to-review label -> claude[bot] will not run. This investigation is the only review it has."
  else empty end'

echo; echo "=== Human comments ==="
gh pr view "$PR" --json comments -q '.comments[] | select(.author.login != "claude[bot]" and .author.login != "github-actions[bot]") | "[\(.createdAt)] \(.author.login): \(.body[0:400])"' || true

echo; echo "=== Commits ==="
gh pr view "$PR" --json commits -q '.commits[] | "\(.oid[0:9])  \(.messageHeadline)"'

echo; echo "=== Behind base? ==="
# Forced refspec: a stale refs/pr/<n> from an earlier run is a non-fast-forward
# update after a contributor force-push, and an unforced fetch rejects it —
# every number below would then describe the previous head. Let a failure show.
git fetch -q origin "+pull/$PR/head:refs/pr/$PR" master || { echo "error: could not fetch pull/$PR/head" >&2; exit 1; }
if git rev-parse -q --verify "refs/pr/$PR" >/dev/null; then
  BASE=$(git merge-base "refs/pr/$PR" origin/master)
  echo "merge-base $(git rev-parse --short "$BASE"); origin/master is $(git rev-list --count "$BASE..origin/master") commits ahead of the PR base"
  echo "files also touched on master since the PR branched (conflict / stale-context risk):"
  comm -12 <(git diff --name-only "$BASE" "refs/pr/$PR" | sort) <(git diff --name-only "$BASE" origin/master | sort) | sed 's/^/  /' || true
fi
