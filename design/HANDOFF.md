# Handoff — OpenAstroAra §14e equipment layer (for Fable)

Written 2026-06-09 by the prior agent, mid-flight on PR #336. Read this top-to-bottom before
touching code. It captures the standing mandate, the two patterns in play, exact process rules, and
the gotchas the CI review bot will hammer you on if you don't pre-empt them.

---

## 1. The standing mandate (don't re-litigate)

- You are running point as the engineer/designer. Work from the `design/` markdown specs, ship in
  **gated, PR-sized increments**, and **decide design yourself** — the user explicitly does not want
  to be asked to confirm routine choices ("I thought I was leaving decisions to you?").
- **Complete the whole solution.** Do not pause at milestones to check in. Keep shipping gated PRs
  autonomously through the whole project.
- **Run the full merge-gate on every PR, every time** (see §3). The user reinforced this repeatedly.
- Status lives in `design/PORT_PROGRESS.md` (one-line "Last merged" / "Currently working on", updated
  every PR) and `design/PORT_TODO.md`. Update both in each PR.

## 2. Where things stand

The project is a **headless .NET 10 NINA fork** (no WPF; a Flutter client drives state over REST/WS).
The §14e thread replaces placeholder equipment with real ASCOM **Alpaca**-backed services.

**Done — 9 real REST device services** (each a gated PR, #323–#333): SafetyMonitor, ObservingConditions,
Switch, Focuser, Rotator, FilterWheel, FlatDevice, Dome, Telescope. All follow the **control-device
template** (§4).

**Done — mediator unifications** (#324 SafetyMonitor read-only; #334 Focuser; #335 Rotator): the real
service *also* implements its `I*Mediator` so Sequencer instructions drive live hardware (§5).

**In flight:** PR #336 — **Dome mediator unification**. If it's merged by the time you read this, great;
if not, finish its merge-gate first.

**Next (the owed mediator follow-ups), in order:**
1. **Telescope mediator** (`ITelescopeMediator`) — the largest surface: `SlewToCoordinatesAsync`,
   `Sync`, `MeridianFlip`, `SetTrackingEnabled`/`Mode`, `ParkTelescope`/`UnparkTelescope`,
   `FindHome`, `StopSlew`, `MoveAxis`, `PulseGuide`, `GetCurrentPosition`, `DestinationSideOfPier`,
   `WaitForSlew`, plus `GetInfo()` → a populated `TelescopeInfo`. The `TelescopeService` REST already
   has the moves; reuse the bounded-blocking-move pattern. Stub the parts no headless instruction
   consumes (document why).
2. **Switch mediator** (`ISwitchMediator`) — heavier: `SetSwitchValue.Validate()` reads
   `GetInfo().WritableSwitches` (a `ReadOnlyCollection<IWritableSwitch>`) and uses each switch's
   `Minimum`/`Maximum`/`StepSize`. You must populate that collection with `IWritableSwitch` wrappers
   over the Alpaca ports — more work than the flat-info devices. Look for an existing
   Alpaca/`IWritableSwitch` impl in `OpenAstroAra.Equipment` before writing your own.
3. **FilterWheel (`SwitchFilter`) + FlatDevice** mediators — these involve `IProfileService`
   (`SwitchFilter` needs the profile's filter list; `Dither` too). Wire `IProfileService` into the
   headless daemon first — that's its own milestone (profile source-of-truth).

**After the mediators — the big remaining subsystems (each multi-PR):**
- **Image pipeline / Camera** (§ around line 2105 in the playbook): `OpenCvSharp4` + `libraw`. The
  Camera REST `StartExposureAsync` returns `ExposureResponseDto(FrameId, PreviewUrl, …)` — i.e. a real
  capture needs frame storage + preview generation. The whole `TakeExposure` capture path lives here.
  This is why Camera was NOT done as a simple template swap. `ICameraMediator` + `IImagingMediator`
  come with it.
- **Guider** — needs PHD2 wiring (§63); it's NOT an Alpaca device. `IGuiderMediator` / `StartGuiding`
  / `Dither` are PHD2-driven.
- **PolarAlign** — orchestrates camera + plate-solve; comes after those.
- **Sequence templates** (§38.7), **profile source-of-truth**, **Phase 15 release**.

`design/PORT_TODO.md` has the authoritative owed-list; `PORT_PROGRESS.md` the current pointer.

## 3. The merge-gate (run EVERY PR)

1. **All CI checks green.** Required: `analyzer-gate` (full-solution, warnings=errors), `server-build`,
   the 3 `Client (analyze + test)` OSes, and the `review` check. Plus non-required smoke /
   `alpaca-sim-integration` / CodeQL / zizmor / unicode / sanity / registry-gate. The **Windows client**
   check is reliably the slowest — expect to wait on it last.
2. **Read the `review` comment BODY, not just the green check color.** The check goes green even when
   the bot found issues. `gh pr view <#> --json comments` and read the latest `claude` comment.
   Address **every** finding with either a fix or a reasoned reply. Iterate rounds until the review is
   clean / approved with no new substantive finding. (The bot is thorough and will keep surfacing
   incremental robustness items — once the substance is approved and only cosmetics remain, it's fine
   to merge; say so in your reply.)
3. Quiesce, then `gh pr merge <#> --merge --delete-branch`, then sync local master
   (`git checkout master && git pull --ff-only origin master`).

You are authorized to merge under this gate.

**Process constraints (non-negotiable):**
- Branch from `master` (protected) — `git checkout -b phase/<...>`.
- Commit trailer: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>` (adjust the
  name to your model if you prefer, but keep a Co-Authored-By trailer).
- PR body footer: `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- **Never commit** `.claude/scheduled_tasks.lock` (it shows as modified — leave it unstaged; stage
  files explicitly, never `git add -A`).
- Gitignored sim binaries are never committed.

## 4. The control-device template (REST services)

`public sealed partial class XService : IXService, IDisposable`. Backed by `AlpacaX` from
`ASCOM.Alpaca.Clients` (package `ASCOM.Alpaca.Components` 2.1.0, ctor
`new AlpacaX(ServiceType, host, port, deviceNumber, strictCasing:false, logger:null)`).

Members: `_logger` (defaults `NullLogger<X>.Instance`), `_gate` lock, `_refreshTimer`
(`System.Threading.Timer`, 2s), `_client`, `_device`, `_state`, `_runtime` (a `*StateDto` cache),
`_connectGeneration` (long, bumped every Connect/Disconnect to supersede stale background work),
`_refreshing` (int, `Interlocked` single-flight guard), `_disposed`, plus device-specific caches.

Key invariants the review bot enforced over ~30 PRs — bake these in from the start:
- **§32.4 cache.** A 2s timer reads device state into `_runtime` under one lock; `GetAsync` serves the
  cache (no per-poll blocking HTTP). `RefreshCacheOnce()` is the single guarded reader, used by both
  the timer and the connect-time seed. Single-flight via `Interlocked.CompareExchange(ref _refreshing…)`.
- **Connect in background** (`ConnectInBackground(device, generation)`): build client, set
  `Connected = true` (authoritative — DO NOT re-GET `Connected`, it transiently reads false), then
  adopt under lock only if `!_disposed && _connectGeneration == generation`. Declare `adopted` outside
  the try so the catch knows ownership transferred. On any failure → `Error` state.
- **Capabilities split** (Focuser/FilterWheel/Telescope/Dome): static caps read once (lazily, cached
  separately from runtime), reset to null on adopt. If an *essential* cap read fails, return null so
  you don't cache a bogus range that permanently rejects ops.
- **Control ops are §60.5 202-Accepted background ops:** validate in the order **dispose → argument
  range → connected**, then `Task.Run(() => …InBackground(client, …), CancellationToken.None)`, return
  `Accepted(...)`. Validate-before-cast for narrowing casts (e.g. `(short)PortId` — reject out of
  range first, else `(short)32768` wraps to a different port).
- **Teardown:** `SafeDisconnectDispose` = Halt/AbortSlew → `Connected = false` → `DisposeQuietly`.
  But `Dispose()` itself disposes the client **directly** (guarded), NOT via the courtesy
  `Connected=false` path — that's a blocking HTTP call (~3s ASCOM timeout) that would hang container
  shutdown if the device is unreachable.
- **Extract pure validators** as `internal static` (e.g. `IsAzimuthOutOfRange`, `IsCoordinateOutOfRange`,
  `IsTargetOutOfRange`) so they're unit-testable sim-free (Server has
  `InternalsVisibleTo OpenAstroAra.Test`).

## 5. The mediator-unification pattern (the current thread)

Goal: the real `XService` also implements `OpenAstroAra.Equipment.Interfaces.Mediator.IXMediator` so the
Sequencer's instructions drive live hardware instead of the `Headless*Mediator` no-op stub.

Recipe (see `FocuserService.Mediator.cs` / `RotatorService.Mediator.cs` / `DomeService.Mediator.cs`):
1. New partial file `XService.Mediator.cs`: `public sealed partial class XService : IXMediator`.
   Top of file: `#pragma warning disable CS0067` (the device events satisfy the interface but are
   never raised server-side).
2. **`GetInfo()`** → the NINA `XInfo` model, built from the §32.4 cache under `_gate`. **Must never
   throw after Dispose** (a running sequence may poll during shutdown) — gate on `_disposed` and
   report "not connected", unlike the REST `GetAsync` which throws `ObjectDisposedException`. Populate
   the fields the consuming instructions' `Validate()` actually read (check the instruction source!) —
   e.g. Dome's `FindHomeDome.Validate` needs `CanFindHome`, `SlewDomeAzimuth.Validate` needs
   `CanSetAzimuth`.
3. **Command members** drive the device. For moves/long ops, use the **hardened blocking-op launcher**
   (copy from `FocuserService.Mediator.cs::MoveFocuserBlockingAsync`):
   - Launch the blocking ASCOM call on `Task.Run(..., CancellationToken.None)`.
   - Race it via `Task.WhenAny(opTask, Task.Delay(Timeout.Infinite, linked.Token))` where
     `linked = CreateLinkedTokenSource(ct)` with `linked.CancelAfter(HardTimeout)` — so a hung HTTP
     call can't pin the sequence thread even when `ct` never fires. `await linked.CancelAsync()` after
     the op wins (avoid leaking the delay); `ObserveQuietly(opTask)` the abandoned task (logs at Debug,
     prevents `UnobservedTaskException`); on the timeout branch throw `TimeoutException`.
   - Then a **settle-wait** that polls the device **directly** (not via the single-flight cache, which
     can no-op against a concurrent timer tick): read `IsMoving`/terminal-condition, refresh the cache
     each tick for `GetInfo`, **stop on a dropped/superseded connection** (check
     `ReferenceEquals(_client, client)` under lock — a disconnect must NOT read as "settled"), tolerate
     a transient read-blip streak but cap it for drivers that never implement the property.
   - Catch: `catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }` (genuine
     sequencer cancel propagates) then `catch (Exception ex) { Log…; return <failure> }` — a device/HTTP
     OCE (ct not cancelled) is a fault, NOT a cancel.
   - Return the device's **actual** post-op state (direct read), falling back to cache, then to a
     neutral "unknown" value — **never the requested target** (don't let a failed op look successful).
4. **DI (dual-singleton):** in `Program.cs`, replace `AddSingleton<IXService, XService>()` with
   `AddSingleton<XService>()` + `AddSingleton<IXService>(sp => sp.GetRequiredService<XService>())`, and
   replace the `AddSingleton<IXMediator, HeadlessXMediator>()` with
   `AddSingleton<IXMediator>(sp => sp.GetRequiredService<XService>())`. **Keep** the `HeadlessXMediator`
   class — `HeadlessSequencerFactory` still defaults to it for headless/test sequences with no device.
5. Mirror the remaining `IDeviceMediator` base members + events from the `Headless*Mediator` stub
   (connection lifecycle is REST-driven; `GetDevice()` throws `NotSupportedException` — this is the
   established contract, matching the stub; do NOT change it to return null — that just relocates the
   failure to an NRE).
6. **Tests:** a sim-free `XMediatorTest.cs` (GetInfo not-connected + disposed-safe never-throws, each
   command not-connected → neutral failure without throwing, pure helpers) + a live mediator op added
   to the existing `XConnectIntegrationTest.cs`.

## 6. Gotchas that cost rounds — pre-empt them

- **Local SDK under-reports CA analyzers.** `dotnet build … -p:TreatWarningsAsErrors=true` locally
  catches most, but the CI `analyzer-gate` (pinned SDK band) catches more. Build with TWAE=true before
  pushing and still expect CI to find the occasional extra. Recurring rules:
  - **CA1725** — an interface impl's parameter names MUST match the interface declaration exactly
    (e.g. `cancellationToken` not `ct`; the focuser interface even declares `double Slope` PascalCase —
    you can't "fix" the casing, it must match). This bites every mediator method.
  - **CA1859** — in tests, don't store `var m = (IXMediator)svc;` (it wants the concrete type); call
    `((IXMediator)svc).Method()` inline instead, or call on the concrete `svc` directly.
  - **CA1849** — use `await cts.CancelAsync()`, not `cts.Cancel()`.
  - **CA1031** — every general `catch (Exception)` needs a `[SuppressMessage(... CA1031 ...)]` with a
    log-and-recover justification. Standard at timer-callback / per-field-read / background-op / teardown
    boundaries.
  - **CA1305 / CA2016 / CA1848/CA1873 (LoggerMessage source-gen) / CA2000** also appear — copy the
    patterns from existing services.
  - **`LogLevel` ambiguity** — when a mediator file imports `OpenAstroAra.Core.Enums` (for `PierSide`
    etc.), `[LoggerMessage(Level = LogLevel.Warning…)]` is ambiguous; fully-qualify
    `Microsoft.Extensions.Logging.LogLevel.Warning`.
- **`AlpacaEquipmentDiscoveryService` is NOT `IDisposable`** — the review bot repeatedly false-flags it.
  It's `sealed`, holds no fields; the static `AlpacaDiscovery` owns the UDP socket per call. Reasoned-
  reply, don't "fix."
- **Two `ShutterState` enums.** ASCOM's `ASCOM.Common.DeviceInterfaces.ShutterState`
  (Open/Closed/Opening/Closing/Error) vs NINA's `OpenAstroAra.Equipment.Interfaces.ShutterState`
  (ShutterNone=-1/ShutterOpen/ShutterClosed/ShutterOpening/ShutterClosing/ShutterError). Alias them
  (`using AscomShutter = …; using NinaShutter = …;`) and map explicitly in Dome's `GetInfo`.
- **Sim binaries / OmniSim:** integration tests (`[Category("Integration")]`) probe `127.0.0.1:32323`
  (HTTP) and `Assert.Ignore` if absent; they run live only in the `alpaca-sim-integration` CI job
  against a real ASCOM OmniSim (32323 HTTP / 32227 UDP discovery). The macOS-x64 sim won't run locally
  under Rosetta — CI linux-x64 is the verification. To reflect ASCOM client member names, build a tiny
  throwaway console with the `ASCOM.Alpaca.Components` PackageReference and reflect `typeof(AlpacaX)` —
  faster than guessing.

## 7. Environment

- `dotnet` is at `~/.dotnet/dotnet` and **not on PATH by default** — prefix:
  `export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1`.
- Server build: `dotnet build OpenAstroAra.Server/OpenAstroAra.Server.csproj -c Release -p:TreatWarningsAsErrors=true`
  (filter out the pre-existing `NU1701` VVVV.FreeImage / `NU5104` Accord.Math restore warnings — those
  are noise, not yours).
- Run a service's tests: `dotnet test OpenAstroAra.Test/OpenAstroAra.Test.csproj -c Release --filter "FullyQualifiedName~XMediatorTest"`.
- `gh` CLI for all PR/issue/review ops. Background-poll CI with a bash `until ! gh pr checks <#> | grep -q pending; do sleep 20; done` loop (run_in_background) so you're notified on completion rather than busy-polling.

Good luck. The patterns are solid and converge fast now — each new mediator should pass review in
fewer rounds than the last. — prior agent
