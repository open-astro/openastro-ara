#!/usr/bin/env bash
# Auto-queue: after a review run, the open pull request whose head is the run's
# commit enters the merge queue when the review bot's latest verdict there is
# "✅ Approved", and leaves it when the verdict is "⚠️ Issues found". Nobody
# enables auto-merge by hand, so a PR cannot reach the queue before its review.
#
# Usage: auto-queue.sh <owner/repo> <head sha of the review run> <run start time>
# Needs GH_TOKEN with pull-requests and contents write.
# scripts/tests/test_auto_queue.py runs it against a fake gh on PATH; every gh
# call returns raw JSON, and jq does the selecting here.
set -euo pipefail

repo="$1"
sha="$2"
since="$3"

# workflow_run.pull_requests is empty for a fork PR, so find the PR by its head.
pr="$(gh pr list --repo "$repo" --state open --limit 200 --json number,headRefOid \
  | jq -r --arg sha "$sha" '[.[] | select(.headRefOid == $sha) | .number] | first // ""')"
if [ -z "$pr" ]; then
  echo "No open pull request has head ${sha}."
  exit 0
fi

state() {
  gh api graphql -F owner="${repo%%/*}" -F name="${repo#*/}" -F n="$pr" -f query='
    query($owner: String!, $name: String!, $n: Int!) {
      repository(owner: $owner, name: $name) {
        pullRequest(number: $n) { id headRefOid isDraft isInMergeQueue autoMergeRequest { enabledAt } }
      }
    }' --jq '.data.repository.pullRequest'
}
s="$(state)"
if [ "$(jq -r .headRefOid <<<"$s")" != "$sha" ]; then
  echo "Pull request #${pr} has moved on from ${sha}; the next review decides."
  exit 0
fi
if [ "$(jq -r .isDraft <<<"$s")" = true ]; then
  echo "Pull request #${pr} is a draft."
  exit 0
fi

# The same verdict rule as scripts/merge-gate.sh: only a bot comment written
# during this review run counts, and the sign-off is its last non-blank line.
verdict="$(gh api --paginate "repos/${repo}/issues/${pr}/comments?per_page=100" \
  | jq -s -r --arg since "$since" \
    '[.[][] | select(.user.login | test("^(claude|github-actions)(\\[bot\\])?$"))
            | select(.updated_at >= $since)
            | (.body | split("\n") | map(select(test("\\S"))) | last // "") as $last
            | select($last | test("^\\s*(✅ Approved|⚠️ Issues found)\\s*$"))
            | {updated_at, verdict: $last}]
     | max_by(.updated_at) | .verdict // ""')"

queued="$(jq -r .isInMergeQueue <<<"$s")"
auto="$(jq -r '.autoMergeRequest != null' <<<"$s")"
case "$verdict" in
  *"✅ Approved"*)
    if [ "$queued" = true ] || [ "$auto" = true ]; then
      echo "Pull request #${pr} is approved and already on its way to the queue."
    else
      # No method flag: the merge queue sets it, and gh refuses one here.
      gh pr merge "$pr" --repo "$repo" --auto
      echo "Pull request #${pr} is approved on ${sha}: auto-merge enabled."
    fi
    ;;
  *"⚠️ Issues found"*)
    if [ "$auto" = true ]; then
      gh pr merge "$pr" --repo "$repo" --disable-auto
      queued="$(state | jq -r .isInMergeQueue)"
    fi
    if [ "$queued" = true ]; then
      gh api graphql -f id="$(jq -r .id <<<"$s")" -f query='
        mutation($id: ID!) { dequeuePullRequest(input: {id: $id}) { clientMutationId } }' >/dev/null
    fi
    echo "Pull request #${pr} has issues on ${sha}: out of the merge queue."
    ;;
  *)
    # A skipped review (the pull_request run of a fork PR, or a PR that edits
    # the review workflow) posts no verdict.
    echo "No verdict from this review run on ${sha}."
    ;;
esac
