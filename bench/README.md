# Virtual-observatory bench — Linux/arm64 lane (bench-5)

A hardware-free, reproducible **Linux/arm64** lane for the §42.2 virtual-observatory
bench. It builds and runs the bench suite inside a `linux/arm64` .NET 10 SDK
container so the bench is exercised on the same kernel + runtime family the §13
Raspberry Pi deployment targets — not just the developer's macOS host.

## What it runs

The three hardware-free bench suites, all driving in-process stubs (no OmniSim, no
PHD2 daemon, no devices):

- **`AlpacaFaultProxyTest`** — the loopback Alpaca fault proxy (bench-1) in front of
  an in-process `StubAlpaca`.
- **`FakeGuiderTest`** — the scriptable fake PHD2 guider protocol (bench-2).
- **`GuiderFakeIntegrationTest`** — the real `GuiderService`/`PHD2Guider` driven
  through the connect→guiding→RMS lifecycle (bench-3) and the star-lost / link-drop
  fault scenarios (bench-4).

## Why a dedicated Linux lane

The `AlpacaFaultProxy` connection-drop fault relies on the OS holding a partial HTTP
response open long enough for the client to observe it before the reset — behaviour
that is kernel- and runtime-sensitive. The macOS host can't catch a Linux-only
regression in that path, and GitHub's hosted runners are x64. This lane keeps a
standing `linux/arm64` check so a future kernel/runtime change that breaks the Drop
mechanic (see `design/PORT_TODO.md`, the bench-5 Drop-fault note) is caught here.

The image is **copy-in, not bind-mount**: the host builds for `osx-arm64` and this
lane for `linux-arm64`, and sharing the source tree's `obj/`/`bin/` across the two
RIDs corrupts each other's restore state. Copying the source into the image keeps
the Linux build fully isolated — run it without disturbing a local `dotnet test`.
The repo-root `.dockerignore` keeps those host `bin/`/`obj/` artifacts out of the
build context regardless of builder — it's honored by both BuildKit and the classic
builder that `run.sh` falls back to (a per-Dockerfile `.dockerignore` would be
BuildKit-only and silently skipped on that fallback path).

## Running it

Requires an **arm64** Docker engine. On Apple Silicon, that's
[colima](https://github.com/abiosoft/colima):

```sh
colima start --arch aarch64      # once per boot
bench/run.sh                     # build + run the bench suite on linux/arm64
```

Or directly with compose:

```sh
docker compose -f bench/docker-compose.yml run --rm --build bench
```

`run.sh` exits with the test pass/fail code, so it drops into a CI step or a
pre-release check unchanged.

## Adding a fixture

The lane selects its suite by the NUnit `[Category("bench")]` tag, so a new bench
fixture is picked up automatically — just add `[Category("bench")]` to the class.
No filter to update here or in the Dockerfile.
