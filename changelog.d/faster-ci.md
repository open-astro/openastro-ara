### Changed (CI)

- Pull request CI finishes sooner. A new push cancels the run it replaces. The client tests run on Linux in three shards; the macOS and Windows test legs run in the merge queue and on `master`, not on a pull request. The Unicode scan and the workflow audit run inside the Sanity job. The server job keeps the ASTAP solver binary between runs.
