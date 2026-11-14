### Changed (CI)

- **The merge queue runs only the Linux client tests**: the macOS and Windows client test shards moved from the merge queue to the pull request. `scripts/merge-gate.sh` already requires every check green on the PR head, so they still gate the merge; in the queue each entry's three macOS jobs waited for the same few macOS runners and set the length of every queue run. The desktop shards no longer repeat `flutter analyze`, which the Linux shard runs once.

### Fixed (CI)

- **The merge gate no longer waits out a check GitHub left at "in progress"**: a check run with a conclusion counts as finished whatever its status says. On 2026-11-14 GitHub served `Changed paths` on #1886's head as `in_progress` with `conclusion: success`, and the gate waited its full 50 minutes twice, failing the entry and rebuilding the ones queued behind it.
