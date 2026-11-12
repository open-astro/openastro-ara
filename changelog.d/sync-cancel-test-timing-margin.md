### Fixed (tests)
- **Sync cancel test no longer flakes on a loaded runner**: `Sync_observes_the_callers_token_while_the_system_read_is_slow` waited 1.07 s against a 1 s bound in a merge-queue run. The slow read is now 2.5 s and the bound 2 s, so the test still fails when Sync ignores the caller's token.
