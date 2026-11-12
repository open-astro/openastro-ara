### Changed (tooling)
- **The merge queue skips the native client builds**: `Client (native build)` (macOS, iOS, Android, Linux, Windows) no longer runs on `merge_group`. The merge gate already requires those builds green on the PR head, and in the queue each group's three macOS jobs waited for the same few macOS runners. Queue groups now need one macOS job (the required macOS client tests).
