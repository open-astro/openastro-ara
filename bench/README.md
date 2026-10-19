# Virtual-observatory bench — Linux/arm64 lane (bench-5)

A hardware-free, reproducible **Linux/arm64** lane for the §42.2 virtual-observatory
bench. It builds and runs the bench suite inside a `linux/arm64` .NET 10 SDK
container so the bench is exercised on the same kernel + runtime family the §13
Raspberry Pi deployment targets — not just the developer's macOS host.

## What it runs

Every fixture tagged `[Category("bench")]` in `OpenAstroAra.Test` (25 files as of
2026-10-19: `grep -rl 'Category("bench")' OpenAstroAra.Test`), all driving in-process
stubs (no OmniSim, no PHD2 daemon, no devices). The three founding suites:

- **`AlpacaFaultProxyTest`** — the loopback Alpaca fault proxy (bench-1) in front of
  an in-process `StubAlpaca`.
- **`FakeGuiderTest`** — the scriptable fake PHD2 guider protocol (bench-2).
- **`GuiderFakeIntegrationTest`** — the real `GuiderService`/`PHD2Guider` driven
  through the connect→guiding→RMS lifecycle (bench-3) and the star-lost / link-drop
  fault scenarios (bench-4).

The rest grew around them: the guider's live, advanced-settings, dark-library,
host-gating, reconnect and active-poll fixtures; the telescope's meridian, latched-fault
and equatorial-system fixtures; camera cooling and exposure events; the cooling-fan
interlock; switch readback; filter-wheel first-connect homing; slew and state-channel
fault watches; polar alignment; equipment fault detection; the storage exchange round
trip. The lane runs them all by category, so a new `[Category("bench")]` fixture joins
automatically.

## Why a dedicated Linux lane

The `AlpacaFaultProxy` connection-drop fault relies on the OS holding a partial HTTP
response open long enough for the client to observe it before the reset — behaviour
that is kernel- and runtime-sensitive. The macOS host can't catch a Linux-only
regression in that path. CI runs the `bench` test category natively on GitHub's hosted
`ubuntu-24.04-arm` runner on every server change (`Bench (linux-arm64, native)` in
`ci.yml`, #1173), so a future kernel/runtime change that breaks the Drop mechanic (see
the bench-5 Drop-fault note in the retired `PORT_TODO.md`, citation key in
`design/README.md`) is caught there. This Docker lane is the same check run from a Mac:
`bench/run.sh` builds and runs it under QEMU without touching the host's build tree.

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
