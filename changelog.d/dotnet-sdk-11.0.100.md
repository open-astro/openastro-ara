### Changed (tooling)

- **Building from source needs the .NET 11 SDK** (build): `global.json` now pins SDK 11.0.100; the daemon still targets and runs on the .NET 10 runtime, so running tests or the daemon from source needs both the .NET 11 SDK and the .NET 10 runtime (see `docs/RUNNING.md`). Installed packages are not affected.
