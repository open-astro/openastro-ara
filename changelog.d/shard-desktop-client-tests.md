### Changed (CI)

- Merge-queue runs finish sooner. The macOS and Windows client tests run in three shards each, like the Linux tests; unsplit, the macOS leg spent 19-20 minutes in `flutter test`. The required `Client (analyze + test)` macOS and Windows contexts now report the result of their shards.
