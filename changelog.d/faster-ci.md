### Changed (CI)

- Pull request CI finishes sooner. A new push cancels the run it replaces. The client tests run in three shards on Linux, macOS and Windows. The Unicode scan and the workflow audit run inside the Sanity job. The server job keeps the ASTAP solver binary between runs.
