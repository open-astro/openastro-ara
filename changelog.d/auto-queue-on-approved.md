### Changed (CI)

- **A PR enters the merge queue only after the review approves it**: after each review run, the new `Auto-queue` workflow enables auto-merge on the PR whose head the bot signed off "✅ Approved", and disables it and takes the PR out of the queue on "⚠️ Issues found". Auto-merge is no longer enabled by hand, so a PR no longer reaches the queue before its review and fails the merge gate there.
