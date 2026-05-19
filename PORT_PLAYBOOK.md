# NINA → OpenAstro Ara: Headless Server + Flutter Client (Port Playbook)

**Audience:** an AI agent operating in a private session with no user in the loop, running on Claude Opus via Claude Code with `--dangerously-skip-permissions`.

**Mandate:** transform NINA's WPF codebase on `master` (3.2 line) into a two-product system called **ARA** (short brand) / **OpenAstro Ara** (long brand):

1. **OpenAstroAra.Server** — a headless ASP.NET Core daemon on .NET 10, running on a Raspberry Pi 4/5 (ARM64 Linux). Owns all imaging-session state. Speaks ASCOM Alpaca to equipment (typically via AlpacaBridge on the same Pi). Exposes a REST + WebSocket API.

2. **OpenAstroAra.Client** — a Flutter + Dart application running on **WILMA** (= Windows, iOS, Linux, Mac, Android — the five client platforms, one codebase). Connects to a server when imaging; otherwise self-sufficient for planning, browsing the sky atlas, editing sequences, viewing past frames.

The product model is ASIAir-like: server runs the night, client is for planning and monitoring. Close the laptop, imaging keeps going.

**Responsibility split (important — informs all later sections):**

| WILMA owns ("plan + view") | ARA Core owns ("hardware orchestration") |
|---|---|
| Sky atlas (Aladin Lite + bundled catalogs) | Equipment control (Alpaca devices) |
| Sequence editor (build offline, push when ready) | Sequence execution (receives + runs) |
| Framing assistant (FOV overlay, client compute) | Plate solving (during the actual session) |
| Profile editor (drafts local, sync to Pi) | Guiding (PHD2) |
| Image library viewer (downloaded frames) | Image capture + FITS storage on disk |
| Catalog data (HYG, NGC/IC, Caldwell, comets) | Session state, telemetry, recovery |
| Settings UI + safety policy editor | Equipment-side state machine |

This split is why **WILMA is not a thin client** — it's a planning workstation that can do real work without the Pi connected. The Pi is the hardware orchestrator that runs at night.

**Naming:** all references to `NINA.X` in this document describe the *current* project layout you'll find on disk. Phase 0.5 renames everything to `OpenAstroAra.X` (server-side) or carves it out for deletion. See §17.

**Repository:** single monorepo at `github.com/open-astro/openastro-ara`. Server (.NET) and client (Flutter) live in the same repo because the client is generated from the server's OpenAPI spec — they must move together. Final layout after the port:

```
openastro-ara/                              (repo root, branch: port/ara)
├── README.md  NOTICE.md  LICENSE.txt  COPYING  AUTHORS
├── PORT_PLAYBOOK.md  PORT_DECISIONS.md  PORT_TODO.md  PORT_PROGRESS.md
├── API_CONTRACT.md  DEPLOY.md  RELEASE_NOTES.md  3rd-party-licenses.txt
├── global.json  OpenAstroAra.sln  .gitignore  Dockerfile
├── .github/workflows/   (ci.yml, release.yml)
│
├── OpenAstroAra.Core/                      ← server-side .NET projects at repo root
├── OpenAstroAra.Astrometry/                  (kept at root, matching NINA's layout —
├── OpenAstroAra.Profile/                      simpler port, no directory moves)
├── OpenAstroAra.Image/
├── OpenAstroAra.Equipment/
├── OpenAstroAra.Sequencer/
├── OpenAstroAra.PlateSolving/
├── OpenAstroAra.Server/
│   ├── openapi.yaml                        ← source of truth, regenerates Dart client
│   ├── Program.cs
│   └── Contracts/                          (DTOs)
├── OpenAstroAra.Test/
│
└── client/
    └── openastroara_client/                ← Flutter project (created in Phase 11)
        ├── pubspec.yaml
        ├── lib/
        │   ├── main.dart
        │   ├── api/generated/              ← regenerated from ../../OpenAstroAra.Server/openapi.yaml
        │   ├── screens/  state/  theme.dart
        │   └── ...
        ├── ios/  android/  macos/  windows/  linux/
        └── test/  integration_test/
```

**CI path filters** keep .NET jobs from running on `client/`-only changes and vice versa (see §14.3).

---

## 0. Read this first — operating rules

1. **No questions.** If you would otherwise ask "which option do you prefer?", pick the option this document recommends. If silent, pick the option that minimizes diff size, write a one-line note in `PORT_DECISIONS.md`, and continue.
2. **No scope creep.** This is a *port + restructure*, not a redesign. The sequencer, equipment state machines, profile schema, coordinate math, plate-solver integration, PHD2 client, and image processing logic all come from NINA as-is. Do not "improve" working logic — just move it across the new boundary.
3. **No half-finished states.** Always work on `port/ara` (the working branch for the entire port). Each commit must leave the solution buildable for everything ported so far.
4. **Cite when stuck.** When you genuinely cannot translate a construct, leave a `// TODO(port): <one sentence>` and a placeholder that compiles, log it in `PORT_TODO.md`, and move on. Sweep TODOs in Phase 15.
5. **Verify continuously.** After every phase, run the build + tests gate in §15. Do not start the next phase until the gate is green for everything completed so far.
6. **Commit cadence.** One commit per logical unit (one project converted, one endpoint implemented, one view ported). Commit messages: `port(<area>): <what>`. Never amend; always new commits. Never `--no-verify`.
7. **No upstream plugin compatibility.** ARA is a hard fork. The plugin SDK is **deferred to v0.1.0** — Phase 0.5 deletes the plugin loader and plugin browser UI entirely. Do not preserve any compatibility with NINA plugins.
8. **Full-auto operation.** You are running with auto-approve on. Hard git safety rails (§19) apply unconditionally — no force pushes, no `--no-verify`, no destructive ops outside the explicit deletion lists.
9. **Tag every phase boundary, never stop.** At the end of each phase, after the gate is green, run `git tag phase-N-complete && git push --tags`, update `PORT_PROGRESS.md`, and immediately begin the next phase. The tag IS the rollback point if something goes wrong.
10. **Quota interruption is normal.** When the model session hits its weekly limit and resumes, the first action is to read `PORT_PROGRESS.md` to find out where to continue. See §20.

---

## 1. Branch + tracking files

```bash
git fetch origin
git checkout port/ara   # branch already exists
```

Create four tracking files at the repo root and commit them empty:

- `PORT_DECISIONS.md` — append-only log of every non-obvious decision, with file:line refs.
- `PORT_TODO.md` — append-only list of every `TODO(port)` and `PORT_BLOCKED` you leave in code, grouped by phase.
- `PORT_PROGRESS.md` — single-page status, see §20.1.
- `API_CONTRACT.md` — append-only design log for the server↔client API; one entry per endpoint or wire-shape decision.

**First commit:** `port(setup): add port tracking files`.

---

## 2. Target stack (locked — do not deviate)

### 2.1 Server (OpenAstroAra.Server)

| Concern | Value |
|---|---|
| Language | C# |
| Runtime | **.NET 10** (current LTS through Nov 2028) |
| Web framework | ASP.NET Core minimal API |
| Target frameworks | pure `net10.0` — no multi-targeting, no `-windows` anywhere |
| SDK pin | `global.json` pinning SDK 10.0.x with `rollForward: latestFeature` |
| Hosting | Kestrel listening on configurable port (default 5400), no reverse proxy |
| Persistence | SQLite via `Microsoft.Data.Sqlite` 10.0.x for session/profile; FITS files on disk |
| Equipment | **Alpaca only.** `ASCOM.Alpaca.Components` 2.1.0+. No ASCOM COM, no native vendor SDKs. |
| Guiding | PHD2 JSON-RPC client (existing NINA code, repointed to openastro-phd2) |
| Plate solving | ASTAP external process (cross-platform binary) |
| Image format | FITS via existing NINA codecs; preview JPEGs generated server-side via **OpenCvSharp4** (replaces `System.Drawing.Common` which doesn't work on Linux ARM64 — see §26) |
| Logging | Serilog (already in NINA) → file sink at `/var/log/openastroara/` (Linux) |
| Discovery | mDNS announce `_openastroara._tcp.local` via `Zeroconf` NuGet |
| Deployment | systemd service `openastroara-server.service`, runs as `openastroara` user |
| Target hardware | RPi 4 (4GB+) or Pi 5 (any), ARM64 Linux (Raspberry Pi OS Bookworm / Ubuntu 24.04 ARM64). Also runs on x64 Linux for development. |
| Inherited from NINA | `Core`, `Astrometry`, `Profile`, `Image`, `Equipment`, `Sequencer`, `Platesolving` |
| Deleted from NINA | main `NINA` (WPF host), `NINA.WPF.Base`, `NINA.CustomControlLibrary`, `NINA.MGEN`, `NINA.Plugin`, `NINA.Setup`, `NINA.SetupBundle`, `nikoncswrapper`, all vendor SDK folders, DirectShow webcam code |

### 2.2 Client (OpenAstroAra.Client)

| Concern | Value |
|---|---|
| Language | Dart |
| Framework | Flutter (stable channel, 3.x+ — pick latest at port time) |
| Target platforms | macOS, iOS, Android, Windows, Linux desktop |
| HTTP client | `dio` (supports interceptors and progress callbacks for image downloads) |
| WebSocket | `web_socket_channel` |
| State management | Riverpod |
| API client | generated from server's OpenAPI spec via `openapi_generator` |
| mDNS discovery | `multicast_dns` plugin (cross-platform) |
| Secure storage | `flutter_secure_storage` for the auth token |
| File picker | `file_picker` plugin |
| Image rendering | Flutter's built-in `Image` widget for JPEG previews; FITS handled by a Dart FITS package (or inline parser if no suitable package exists) |
| Build outputs | macOS `.app` + `.dmg`, iOS `.ipa`, Android `.apk`/`.aab`, Windows `.exe`/`.zip`, Linux AppImage |
| App icons | placeholder during AI port; user supplies real assets pre-release |

### 2.3 Wire protocol

| Concern | Value |
|---|---|
| Transport | HTTP/1.1 over TCP (no TLS in v0.0.1 — local LAN only; opt-in TLS for v0.1.0) |
| Encoding | JSON for ops; JPEG bytes for image previews; FITS bytes for full-frame downloads |
| Operations | REST endpoints under `/api/v1/...` |
| Live updates | WebSocket at `/api/v1/stream` — server pushes sequence progress, frame complete, log lines, equipment state changes |
| Authentication | Shared token in `X-OpenAstroAra-Token` header. Token generated on first server startup, written to `/etc/openastroara/token`, also printed once in server logs. Client prompts user for token on first connect. |
| Discovery | mDNS service type `_openastroara._tcp.local`; TXT records expose version, hostname, port |
| Contract | OpenAPI 3.1 spec at `OpenAstroAra.Server/openapi.yaml` — source of truth; Dart client and server-side validation both derive from it |

---

## 3. Phased plan (execute strictly in order)

```
Phase 0.5 — Fork hygiene + project demolition
            (rename, license headers, delete WPF UI, delete plugin loader,
             delete vendor SDKs, delete WiX, delete WebView2, delete MGEN, delete ASCOM COM)
Phase 1   — Bump non-UI projects to .NET 10
Phase 2   — Equipment layer to Alpaca-only
Phase 3   — Repoint PHD2 client at openastro-phd2
Phase 4   — Create OpenAstroAra.Server project (ASP.NET Core skeleton)
Phase 5   — Define API contract (OpenAPI v1)
Phase 6   — Implement equipment endpoints
Phase 7   — Implement sequence endpoints
Phase 8   — Implement image endpoints
Phase 9   — Implement log + status endpoints + WebSocket stream
Phase 10  — Server smoke test on Linux x64 + ARM64 (Docker container + actual Pi if available)
Phase 11  — Flutter client scaffold (mDNS discovery, token prompt, handshake)
Phase 12  — Flutter views: equipment dashboard, sequence editor, image viewer, log tail
Phase 13  — Image preview pipeline (server-side JPEG generation, client-side display)
Phase 14  — Tests + GitHub Actions CI
Phase 15  — TODO sweep + smoke test + release v0.0.1-ara.1
```

Do **not** start Phase N+1 until Phase N passes the gate in §15.

---

## 4. Phase 0.5 — Fork hygiene + project demolition

### 4.1 Project rename

1. Rename every kept `NINA.X` csproj file and directory to `OpenAstroAra.X` (mapping below).
2. `NINA.sln` → `OpenAstroAra.sln` with all new paths.
3. Global namespace rename `NINA.X` → `OpenAstroAra.X` in every `.cs` file via bulk find-replace.
4. Update assembly name, root namespace, `AssemblyTitle` in each csproj.
5. `NINA.sln.licenseheader` → `OpenAstroAra.sln.licenseheader`.

Kept-project mapping:

| Old | New |
|---|---|
| `NINA.Core` | `OpenAstroAra.Core` |
| `NINA.Astrometry` | `OpenAstroAra.Astrometry` |
| `NINA.Profile` | `OpenAstroAra.Profile` |
| `NINA.Image` | `OpenAstroAra.Image` |
| `NINA.Equipment` | `OpenAstroAra.Equipment` |
| `NINA.Sequencer` | `OpenAstroAra.Sequencer` |
| `NINA.PlateSolving` | `OpenAstroAra.PlateSolving` |
| `NINA.Test` | `OpenAstroAra.Test` |

Build must succeed after rename on Windows (`dotnet build OpenAstroAra.sln -c Debug`). WPF UI is still present at this point — only renaming.

Commit: `port(fork): rename NINA.* projects and namespaces to OpenAstroAra.*`.

### 4.2 Delete WPF UI + irrelevant projects

Delete unconditionally:

- `NINA/` (main WPF project — App.xaml, MainWindow, all Views/ViewModels, `NINA/Resources/`)
- `NINA.WPF.Base/`
- `NINA.CustomControlLibrary/`
- `NINA.Plugin/`
- `NINA.MGEN/`
- `NINA.Setup/`, `NINA.SetupBundle/` (WiX)
- `nikoncswrapper/`
- All per-vendor folders under `OpenAstroAra.Equipment/SDK/CameraSDKs/` (Canon, Nikon, ZWO/ASI, QHY, Atik, PlayerOne, Altair, Touptek, FLI, SBIG, Omegon, Meade DSI, DirectShow webcams)
- All `NINA/External/<vendor>/` folders containing bundled DLLs
- `Accord.Imaging/` — audit first; if anything kept truly references it, vendor only the specific source files needed and delete the rest

For each deleted equipment device class, **leave the abstraction interface** (`ICamera`, `ITelescope`, `IFocuser`, `IFilterWheel`, `IRotator`, `IFlatPanel`, `ISwitch`, `IWeatherData`, `IDome`, `ISafetyMonitor`, `IGuider`) intact in `OpenAstroAra.Equipment` — only concrete implementations are deleted.

Update `OpenAstroAra.sln` to remove deleted project references.

Commit: `port(demolish): delete WPF UI, vendor SDKs, MGEN, WiX, plugin host`.

### 4.3 Strip Stefan branding

- Any hardcoded `nighttime-imaging.eu` URLs → `github.com/open-astro/openastro-ara`
- "NINA"/"N.I.N.A." in remaining log lines, config paths, exception messages → "OpenAstro Ara"
- Patreon/donate references → delete
- `crowdin.yml` → delete; non-English `Locale.*.resx` → delete (English-only, §18.E)
- Sectigo cert thumbprint + `signtool` invocation → delete
- `AstrophotographyBuddy_TemporaryKey.pfx` → delete
- Logo files (`Logo_Nina.ico`, splashes) → delete; server has no UI

Commit: `port(fork): strip Stefan branding, swap URLs to open-astro`.

### 4.4 License headers

Per §17.3, append the Open Astro copyright line on every modified file. MPL header stays. Stefan's existing copyright line stays.

### 4.5 Phase 0.5 gate

Before tagging `phase-0.5-complete`:

- `dotnet build OpenAstroAra.sln -c Debug` succeeds (kept projects build; deleted ones gone).
- `OpenAstroAra.sln` does not reference deleted projects.
- No "NINA" / "N.I.N.A." in user-visible strings in kept projects (verify with `grep`).
- `LICENSE.txt`, `COPYING`, `AUTHORS` unchanged. `NOTICE.md` exists at repo root.
- Previously-passing tests in `OpenAstroAra.Test` still pass (tests that depended on deleted WPF code are deleted, not skipped).
- `bin/Debug/` contains no vendor SDK DLLs, `nikoncswrapper.dll`, MGEN binaries, or WPF assemblies.

---

## 5. Phase 1 — Bump non-UI projects to .NET 10

1. Pin SDK with `global.json` at repo root:
   ```json
   {
     "sdk": {
       "version": "10.0.100",
       "rollForward": "latestFeature"
     }
   }
   ```
   (Use whatever 10.0.x is current — `dotnet --list-sdks`.)

2. Every kept `OpenAstroAra.*.csproj`:
   - `<TargetFramework>net8.0-windows</TargetFramework>` → `<TargetFramework>net10.0</TargetFramework>`
   - `<TargetFramework>net8.0</TargetFramework>` → `<TargetFramework>net10.0</TargetFramework>`
   - Delete `<UseWPF>`, `<UseWindowsForms>`, `<ImportWindowsDesktopTargets>`, `<EnableWindowsTargeting>`.

3. Bump every `Microsoft.Extensions.*` and `System.*` package from 8.0.x to 10.0.x (`dotnet list package --outdated` finds them):
   - `Microsoft.Extensions.DependencyInjection` 8.0.1 → 10.0.x
   - `System.Drawing.Common` 8.0.10 → 10.0.x
   - `System.Runtime.Caching` 8.0.1 → 10.0.x
   - `System.IO.Ports` 8.0.0 → 10.0.x
   - `System.ComponentModel.Composition` 8.0.0 → 10.0.x
   - `System.Management` → **delete** (WMI; not needed post-§4.2)
   - `System.Data.SQLite` → switch to `Microsoft.Data.Sqlite` 10.0.x (actively maintained, ARM64-native)

4. Verify cross-platform: `dotnet restore && dotnet build OpenAstroAra.sln -c Debug` on Windows, macOS, Linux ARM64.

5. Run tests. Fix any analyzer escalations or obsoleted APIs.

Commit: `port(net10): bump all projects to net10.0; add global.json`.

---

## 6. Phase 2 — Equipment to Alpaca-only

### 6.1 Csproj

`OpenAstroAra.Equipment/OpenAstroAra.Equipment.csproj`:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="ASCOM.Alpaca.Components" Version="2.1.0" />
  <PackageReference Include="ASCOM.Alpaca.Device" Version="2.1.0" />
  <PackageReference Include="ASCOM.Tools" Version="2.1.0" />
</ItemGroup>
```

Remove `ASCOM.Com.Components` if present. Delete `#if WINDOWS` blocks.

### 6.2 Provider

```csharp
public interface IEquipmentProvider {
    string Id { get; }              // "alpaca"
    string DisplayName { get; }     // "ASCOM Alpaca"
    Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(DeviceType type, CancellationToken ct);
    Task<T> ConnectAsync<T>(DiscoveredDevice device, CancellationToken ct) where T : class;
}
```

Single impl: `AlpacaEquipmentProvider`. Uses `AlpacaDiscovery` for broadcast UDP on port 32227. Hands out typed proxies from `ASCOM.Alpaca.Components`.

### 6.3 Strip COM call sites

Find and delete every `ASCOM.Com`, `ASCOM.DriverAccess` (COM path), `Marshal.GetActiveObject` reference in `OpenAstroAra.Equipment`. Replace device-instantiation paths with Alpaca equivalents.

Commit: `port(equipment): collapse to Alpaca-only`.

---

## 7. Phase 3 — PHD2 client repoint

Existing PHD2 client at `OpenAstroAra.Equipment/Equipment/MyGuider/PHD2/` speaks PHD2's JSON-RPC over TCP. openastro-phd2 preserves the protocol; minimal change:

- Default host: keep `localhost:4400`. Server runs on Pi; PHD2 typically runs on same or sibling Pi.
- On connect: call `get_app_state`, log version. If response identifies as openastro-phd2, log "Connected to openastro-phd2 vX on Linux."

Commit: `port(equipment): default PHD2 client to openastro-phd2`.

---

## 8. Phase 4 — OpenAstroAra.Server scaffold

```bash
dotnet new web -n OpenAstroAra.Server -f net10.0
```

`OpenAstroAra.Server.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\OpenAstroAra.Core\OpenAstroAra.Core.csproj" />
    <ProjectReference Include="..\OpenAstroAra.Astrometry\OpenAstroAra.Astrometry.csproj" />
    <ProjectReference Include="..\OpenAstroAra.Profile\OpenAstroAra.Profile.csproj" />
    <ProjectReference Include="..\OpenAstroAra.Image\OpenAstroAra.Image.csproj" />
    <ProjectReference Include="..\OpenAstroAra.Equipment\OpenAstroAra.Equipment.csproj" />
    <ProjectReference Include="..\OpenAstroAra.Sequencer\OpenAstroAra.Sequencer.csproj" />
    <ProjectReference Include="..\OpenAstroAra.PlateSolving\OpenAstroAra.PlateSolving.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.OpenApi" Version="10.0.*" />
    <PackageReference Include="Swashbuckle.AspNetCore" Version="7.*" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.*" />
    <PackageReference Include="Serilog.AspNetCore" Version="9.*" />
    <PackageReference Include="Zeroconf" Version="3.*" />
  </ItemGroup>
</Project>
```

`Program.cs` skeleton — mirror NINA's `CompositionRoot` DI registrations for non-UI services, add token auth, CORS, WebSocket support, mDNS announce. Endpoints are mapped in subsequent phases.

Commit: `port(server): scaffold OpenAstroAra.Server with DI from NINA CompositionRoot`.

---

## 9. Phase 5 — API contract

Hand-write `OpenAstroAra.Server/openapi.yaml`. Endpoint groups:

| Group | Path prefix | Purpose |
|---|---|---|
| Server | `/api/v1/server` | Version, capabilities, handshake, current state summary |
| Equipment | `/api/v1/equipment/{type}` | List/connect/control cameras, mounts, focusers, filter wheels, rotators, etc. |
| Sequence | `/api/v1/sequence` | Load JSON, validate, start/pause/resume/abort, status |
| Image | `/api/v1/image` | List frames, download FITS, request preview JPEG |
| Log | `/api/v1/log` | Recent log lines |
| Stream | `/api/v1/stream` | Single WebSocket for live updates |

**Auth:** every endpoint except `/api/v1/server/info` and `/api/v1/server/handshake` requires `X-OpenAstroAra-Token` header. Constant-time comparison. After 3 failed attempts from one IP within 60 s, return 429 for 5 minutes.

**Versioning:** URL versioning (`/api/v1/...`). Within v0.x, breaking changes within `/api/v1/` permitted and documented in `API_CONTRACT.md`.

**WebSocket protocol:** client connects with `?token=...`. Server sends JSON `{ "type": "equipment.state" | "sequence.progress" | "log.line" | "frame.complete" | ..., "ts": "...", "payload": {...} }`. Client may send `{"type":"subscribe","channels":["log","frames"]}` to filter. Default: all channels.

Commit: `port(api): define OpenAPI v1 contract`.

---

## 10. Phases 6–9 — Implement endpoints

Each phase implements one endpoint group. Pattern per endpoint:

1. Add route in the appropriate `Map*Endpoints` extension.
2. Inject the existing NINA service that owns the underlying state (e.g., `IEquipmentMediator` → rename to `IEquipmentService`, drop the UI-flavored "Mediator" naming).
3. Map request DTOs to internal types; map internal types back to response DTOs. DTOs live in `OpenAstroAra.Server/Contracts/`.
4. Hook progress/state-change events into the WebSocket broadcaster.
5. Write a `WebApplicationFactory`-based test in `OpenAstroAra.Test`.
6. Update `openapi.yaml`.

Commit per endpoint group: `port(api): equipment endpoints`, etc.

---

## 11. Phase 10 — Server smoke test

### 11.1 Cross-platform publish

```bash
dotnet publish OpenAstroAra.Server -c Release -r linux-arm64 --self-contained -o ./publish/arm64
dotnet publish OpenAstroAra.Server -c Release -r linux-x64   --self-contained -o ./publish/x64
```

Both must succeed.

### 11.2 Docker

`Dockerfile` at repo root:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0-bookworm-slim-arm64v8
WORKDIR /app
COPY publish/arm64/ ./
EXPOSE 5400
USER 1000
ENTRYPOINT ["./OpenAstroAra.Server"]
```

```bash
docker build -t openastroara-server:arm64 .
docker run --rm -p 5400:5400 openastroara-server:arm64
curl http://localhost:5400/api/v1/server/info     # expect 200
```

### 11.3 Real Pi (if available)

Copy `publish/arm64/` to `/opt/openastroara/`, create systemd unit (§13), `systemctl enable --now openastroara-server`. Hit the same endpoint from another LAN host. Verify mDNS via `dns-sd -B _openastroara._tcp` (macOS) or `avahi-browse -t _openastroara._tcp` (Linux).

If no physical Pi: the Docker arm64 image is sufficient.

Commit: `port(server): smoke test on linux-arm64`.

---

## 12. Phases 11–13 — Flutter client

### 12.1 Scaffold

```bash
mkdir client
cd client
flutter create --org org.openastro --project-name openastroara \
    --platforms macos,ios,android,windows,linux openastroara_client
cd openastroara_client
flutter pub add dio web_socket_channel multicast_dns riverpod flutter_riverpod \
    flutter_secure_storage file_picker
flutter pub add --dev openapi_generator build_runner
```

Configure `openapi_generator` to read `../../OpenAstroAra.Server/openapi.yaml`, generate Dart client into `lib/api/generated/`. Run via `dart run build_runner build`.

### 12.2 First-run flow

The first screen:
1. **Server discovery** — mDNS scan for `_openastroara._tcp.local`. List discovered servers with hostname, version. Manual "Add server by IP:port" option below.
2. **Token entry** — once a server is selected, prompt for the token. Test with `/api/v1/server/handshake`. On 200, save token to `flutter_secure_storage`. On 401, allow re-entry.
3. **Connected** — navigate to the main app shell.

### 12.3 App shell — clone NINA's layout

The client UX deliberately mirrors NINA so existing users feel at home. **See §25 for the full visual design reference**, which the AI must read before writing any client UI code. Brief summary here:

**Desktop layout** (macOS / Windows / Linux):
- **Top equipment bar** — horizontal strip of device icons (camera, filter wheel, focuser, mount, rotator, guider, dome, switch, flat panel, weather, safety monitor). Each shows a connection indicator (gray = disconnected, green = connected, red = error). Click to open chooser; double-click to connect/disconnect.
- **Left panel** (collapsible) — profile selector + active equipment chooser dropdowns + manual control widgets per connected device.
- **Center workspace** — tabbed:
  - **Imaging** — main image viewer, live preview, exposure controls, histogram overlay, plate-solve overlay
  - **Framing Assistant** — target search + sky chart + rotation preview
  - **Sequencer** — tree-based instruction editor (Areas → Targets → Instructions)
  - **Sky Atlas** — DSO catalog browser
  - **Options** — settings tree (Equipment, Imaging, Plate Solving, Astrometry, etc.)
- **Right panel** (collapsible) — histogram + image statistics, plate solve results, log tail.
- **Bottom status bar** — clock (local + sidereal), current sequence operation, progress bar, connection state to server.

**Mobile layout** (iOS / Android):
- Bottom tab bar with 5 destinations: **Status**, **Equipment**, **Sequence**, **Images**, **More** (settings, logs).
- The "Status" tab is the mobile-shaped Dashboard from §12.3's earlier description: at-a-glance state of the imaging session. Mobile is read-mostly / nudge-only — full editing UX is desktop-class.

**Theme:** dark, astro-friendly. Near-black background (`#1a1a1a`), darker panels (`#262626`), accent colors for status (green `#4caf50`, yellow `#ffb300`, red `#e53935`, blue `#42a5f5`), text in soft white (`#e0e0e0`). Exact tokens defined in §25.

**Resizable splits:** use `multi_split_view` Flutter package for resizable side panels. **Dockable/rearrangeable panels are a v0.1.0 feature** — for v0.0.1, panels are positioned fixed (left/right/bottom) and only resizable, not draggable.

**No bitmap icons from NINA.** All icons are placeholders (Flutter's `Icons.*` material set or simple SVG primitives) until the user supplies real ARA assets. Layout is the clone; pixels are original.

### 12.4 State management

Riverpod providers per section. WebSocket connection is a singleton provider streaming typed events. Each section subscribes to its slice.

### 12.5 Image preview pipeline (Phase 13)

Server side, on capture complete:
1. Sequencer writes FITS to disk (existing NINA path).
2. `IImagePreviewGenerator` produces downscaled JPEG (max 1920×1080, quality 80, stretched per user setting) saved as `<frame>.preview.jpg`.
3. WebSocket sends `{"type":"frame.complete","payload":{"id":"...","previewUrl":"/api/v1/image/{id}/preview","fitsUrl":"/api/v1/image/{id}/fits","previewBytes":N}}`.
4. Client receives event, fetches preview JPEG, displays. Full FITS pulled only on user-initiated download.

Commit per stage: `port(client): mDNS discovery + token`, `port(client): app shell`, `port(client): equipment view`, etc.

---

## 13. RPi deployment (Phase 10 + Phase 15)

Per-Pi setup (one-time — AI documents in `DEPLOY.md`):

```bash
sudo useradd -r -s /usr/sbin/nologin openastroara
sudo mkdir -p /opt/openastroara /etc/openastroara /var/log/openastroara
sudo chown -R openastroara:openastroara /opt/openastroara /var/log/openastroara
sudo chown root:openastroara /etc/openastroara
sudo chmod 750 /etc/openastroara
```

Per release, GitHub Actions builds:
- `linux-arm64` self-contained, gzipped as `openastroara-server-0.0.1-ara.N-linux-arm64.tar.gz`.
- Optional `.deb` via `dotnet-packaging` (lower priority; tarball works).

systemd unit at `/etc/systemd/system/openastroara-server.service`:

```ini
[Unit]
Description=OpenAstro Ara Server
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
User=openastroara
Group=openastroara
ExecStart=/opt/openastroara/OpenAstroAra.Server
EnvironmentFile=/etc/openastroara/server.env
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
```

---

## 14. Testing (Phase 14)

### 14.1 Server tests

`OpenAstroAra.Test` (already exists). Add:
- `WebApplicationFactory<Program>`-based integration tests per endpoint group.
- In-process mock Alpaca server for equipment endpoint tests.
- Sequencer unit tests: load known JSON sequence, step through, verify state transitions.

### 14.2 Flutter tests

In `client/openastroara_client/`:
- Widget tests per screen (`flutter_test`).
- Integration tests for the first-run flow (`integration_test`).
- Mock the generated API client (it takes a `Dio` — inject a mock).

### 14.3 CI matrix (`.github/workflows/ci.yml`)

| Job | Runner | Steps |
|---|---|---|
| server-build | `ubuntu-latest` | `dotnet build`, `dotnet test`, publish `linux-arm64` + `linux-x64` |
| client-macos | `macos-latest` | `flutter build macos --release`, `flutter build ios --no-codesign` |
| client-windows | `windows-latest` | `flutter build windows --release` |
| client-linux | `ubuntu-latest` | `flutter build linux --release`, `flutter build apk --release` |
| client-test | `ubuntu-latest` | `flutter test`, `flutter analyze` |

On tag `v0.0.1-ara.*`, also a `release` job uploading artifacts to a GitHub Release.

Commit: `port(ci): GitHub Actions for server + Flutter client`.

---

## 15. Build + verification gate (run after every phase)

```bash
# Server
dotnet restore OpenAstroAra.sln
dotnet build OpenAstroAra.sln -c Debug --nologo
dotnet test  OpenAstroAra.Test/OpenAstroAra.Test.csproj -c Debug --nologo --logger "console;verbosity=minimal"

# Client (Phase 11 onward)
cd client/openastroara_client
flutter analyze
flutter test
cd -

# Cross-platform publish smoke (Phase 10 onward)
dotnet publish OpenAstroAra.Server -c Release -r linux-arm64 --self-contained -o /tmp/publish-test
```

Gate is green when:
1. `dotnet build` succeeds with zero errors. Warnings logged in `PORT_DECISIONS.md`.
2. `dotnet test` green for every previously-passing test. Tests dependent on deleted WPF UI types from §4.2 are deleted (not skipped).
3. `flutter analyze` returns no errors (warnings OK, logged).
4. `flutter test` passes.
5. `dotnet publish -r linux-arm64` succeeds.
6. From Phase 10: published server responds to `/api/v1/server/info`.
7. From Phase 11: `flutter run -d macos` reaches the server-discovery screen without exceptions.

If the gate fails and you cannot fix it within ~5 attempts, revert the last commit, write up the failure in `PORT_DECISIONS.md`, try a different approach. **Do not push a broken commit.**

---

## 16. Stuck-state policy

- **Compile error you can't immediately solve:** comment out the smallest region with `// PORT_BLOCKED: <reason>`, make the file compile with `throw new NotImplementedException("PORT_BLOCKED: <reason>")`, log to `PORT_TODO.md`. Move on.
- **API design ambiguity:** pick a REST-conventional shape (nouns for resources, HTTP status codes per semantics), document in `API_CONTRACT.md`. Do not paralyze.
- **Flutter package missing for a need (e.g., FITS parsing):** vendor a minimal implementation in `client/openastroara_client/lib/<feature>/` rather than depending on an unmaintained package.
- **NINA logic depends on a WPF type internally (Dispatcher, RoutedEventArgs, etc.):** replace with `SynchronizationContext` or plain async/await. Patch in place.
- **Tempted to ask the user:** pick the option this document or §0 rule 1 prescribes. Write the decision down. Continue.

---

## 17. Fork hygiene — naming, identifiers, MPL preservation

### 17.1 Names and identifiers

| Identifier | Value |
|---|---|
| Brand short | **ARA** (all caps — hero, About large text, marketing) |
| Brand long / display name | **OpenAstro Ara** (window title, README hero, About publisher line) |
| Publisher / org | **Open Astro** |
| Solution file | `OpenAstroAra.sln` |
| Project namespace prefix | `OpenAstroAra.*` |
| Server executable | `OpenAstroAra.Server` |
| Client app name | `OpenAstro Ara` |
| iOS/macOS bundle ID | `org.openastro.ara` |
| Android app ID | `org.openastro.ara` |
| GitHub repo (monorepo) | `github.com/open-astro/openastro-ara` |
| Server log path | Linux: `/var/log/openastroara/`; macOS dev: `~/Library/Logs/OpenAstroAra/`; Windows dev: `%LOCALAPPDATA%\OpenAstroAra\Logs\` |
| Server config path | `/etc/openastroara/` (Linux); equivalents on dev OSes |
| Client local storage | platform-default app data dir, scoped to bundle ID |
| Version | Server: `0.0.1-ara.1` (assembly: `0.0.1.0`); Client: `0.0.1+1` (Flutter format) |
| Phase tags | `phase-N-complete` |
| Release tag form | `v0.0.1-ara.N` |
| App icon / branding assets | placeholder during AI port; `TODO(branding): replace before public release` markers wherever icons are referenced |

### 17.2 MPL 2.0 preservation

**Sacred files (do not modify, do not delete):**
- `LICENSE.txt`, `COPYING`, `AUTHORS` (append-only), `3rd-party-licenses.txt` (update as deps change)

**Add at repo root:**
- `NOTICE.md`:
  > OpenAstro Ara (ARA) is a derivative work of [N.I.N.A. (Nighttime Imaging 'N' Astronomy)](https://github.com/isbeorn/nina) by Stefan Berg and contributors, used under the Mozilla Public License 2.0. Original copyrights are preserved in every source file. ARA is not affiliated with or endorsed by the N.I.N.A. project.
- `README.md` rewrite (first paragraph mentions the lineage)
- `DEPLOY.md` (RPi server install)
- `API_CONTRACT.md` (API design log)

### 17.3 Per-file headers

C# (modified or new files in ported projects):

```csharp
#region "copyright"
/*
    Copyright © 2016 - 2025 Stefan Berg <isbeorn@hotmail.com> and the N.I.N.A. contributors
    Copyright © 2026 - present Open Astro contributors

    This file is part of the open-source OpenAstro Ara project.

    This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
    If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/
#endregion "copyright"
```

Dart (client — new code, no NINA lineage):

```dart
// Copyright (c) 2026 Open Astro contributors.
// Licensed under the Mozilla Public License, v. 2.0.
// https://mozilla.org/MPL/2.0/
```

Rules:
- File the AI does not modify → header unchanged.
- Existing NINA file the AI modifies → append Open Astro line below Stefan's existing line.
- New file in a port-touched C# project → both copyright lines, MPL header.
- New file in `OpenAstroAra.Server` or `client/` → Open Astro line only.

---

## 18. Feature decisions (baked-in)

### 18.A — Updater: **DROP**
No in-app updater. README points users to GitHub Releases. Server announces its version in `/api/v1/server/info`; client displays "Server version X — see GitHub for updates."

### 18.B — Plugin system: **DEFERRED to v0.1.0**
Phase 0.5 deletes `NINA.Plugin` entirely. No plugin loader, no browser UI in the client, no SDK published. Plugin design happens post-v0.0.1 once architecture is stable.

### 18.C — Telemetry: **LOCAL LOGS ONLY, NO NETWORK**
Server: Serilog file sink, daily rotation, 14-day retention.
Client: in-app log viewer fed by server's WebSocket log stream. Optional "Save logs to file" button.
**No network calls** for analytics, crash reporting, telemetry. Strip any pre-existing Sentry/AppInsights references.
Crash handling: server logs unhandled exceptions and continues where safe. Client shows a "Disconnected — server may have crashed; check Pi" toast.

### 18.D — Community / branding links
- README, About, Help → `github.com/open-astro/openastro-ara` and `github.com/open-astro/openastro-ara/discussions`
- No Patreon/donate/Discord links until those channels exist; `TODO(community): add links when channels exist`

### 18.E — Localization: **ENGLISH ONLY**
Delete `crowdin.yml`. Delete non-English `Locale.*.resx`. No language picker. Hard-code `CultureInfo.InvariantCulture` where formatting was locale-influenced. Client is English-only; localization is a v0.2.0 problem.

### 18.F — Code signing: **SHIP UNSIGNED**
- Server: no signing (Linux daemons don't typically sign).
- Client: unsigned macOS/iOS/Android/Windows. README documents per-OS bypass:
  - macOS: right-click → Open, or `xattr -d com.apple.quarantine "/Applications/OpenAstro Ara.app"`
  - iOS: out of scope for v0.0.1 (needs Apple Developer ID)
  - Android: enable "Install from unknown sources," install APK
  - Windows: SmartScreen → More info → Run anyway
  - Linux: `chmod +x` the AppImage and run
- `TODO(signing): revisit when project has funding` in release workflow

### 18.G — Distribution formats
- **Server**: `.tar.gz` of self-contained publish (`linux-arm64`, `linux-x64`). Optional `.deb` later.
- **Client**:
  - macOS: `.dmg` via `create-dmg`
  - iOS: `.ipa` unsigned (TestFlight is future work)
  - Android: `.apk` (sideload) + `.aab` (Play Store future)
  - Windows: `.zip` of release build; `.msix` future
  - Linux desktop: AppImage
- All built by GitHub Actions on tag push.

### 18.H — Branding assets
Placeholders during port. Every icon/splash/logo reference carries `TODO(branding): replace with ARA asset before public release`. User supplies real assets.

### 18.I — Plate solving
- **ASTAP**: only solver. Cross-platform; users download per OS from astap.nl. Server config exposes ASTAP binary path + star-database path; per-OS defaults attempted on first run:
  - Linux: `which astap` → `/usr/bin/astap` or `/opt/astap/astap`
  - macOS: `/Applications/ASTAP.app/Contents/MacOS/astap`
  - Windows: `%PROGRAMFILES%\astap\astap.exe`
- **Astrometry.net**: kept if NINA's existing integration works without WPF deps; audit during Phase 8.
- **PlateSolve2**: deleted entirely (Windows-only legacy).

---

## 19. Auto-approve safety rails

### 19.1 Git safety

- Branch locked to **`port/ara`**. No commits/pushes elsewhere.
- No `git push --force` or `--force-with-lease`. Plain `git push` only.
- No `--no-verify` on commits.
- No `git reset --hard` without first creating `backup-<timestamp>` tag.
- No deleting branches, remotes, or stashes on the remote.
- No history rewriting (`filter-branch`, `filter-repo`, interactive rebase).
- Tags: `phase-N-complete` at boundaries, `backup-<timestamp>` before destructive ops. Push via `git push --tags`.

### 19.2 Filesystem safety

- No `rm -rf` outside `bin/`, `obj/`, `client/openastroara_client/build/`, and the explicit deletion lists in §4.2.
- No modifications outside repo root.
- No installing global tools (`dotnet tool install -g`, `brew install`, `apt install`, `npm install -g`, `flutter pub global`). Local-to-repo only.
- No modifying system state (PATH, shell config, dotfiles outside repo).
- No writing to `~/`, `/etc/`, `/usr/`, `/var/`, `~/.ssh/`, `~/.aws/`, `~/.config/git/`, `~/.gitconfig`.

### 19.3 Network safety

- No authenticated network calls except `gh` (GitHub CLI) and `git push origin`.
- No POSTing telemetry, analytics, crash reports.
- Allowed: `dotnet restore` (NuGet), `flutter pub get` (pub.dev), `gh` commands, package metadata.

### 19.4 Secrets safety

- Do not commit `.pfx`, `.key`, `.pem`, `.env`, `appsettings.Secrets.json`, `secrets.dart`, or files containing `password`/`secret`/`token`. Add patterns to `.gitignore` if found.
- Server bootstrap token generated at first run on the deployment machine; never committed.
- Do not echo or log API keys, tokens, auth headers.

### 19.5 Scope safety

- Do not edit `PORT_PLAYBOOK.md`, `PORT_DECISIONS.md`, `PORT_TODO.md`, `PORT_PROGRESS.md`, `API_CONTRACT.md` except to append entries per documented rules.
- Do not edit `.git/`, `.github/workflows/` (until Phase 14), `.claude/`.

---

## 20. Quota-resume protocol

### 20.1 `PORT_PROGRESS.md` format

```markdown
# OpenAstro Ara — Port Progress

## Current
- Phase: 7 — Sequence endpoints
- Started: 2026-XX-XX
- Currently working on: <file or endpoint>

## Completed
- ✅ Phase 0.5 — Fork hygiene + project demolition (tag: phase-0.5-complete)
- ✅ Phase 1 — Bump non-UI projects to .NET 10 (tag: phase-1-complete)
- ... (one line per phase)

## Next
- After current task: <next file or endpoint>
- After current phase: Phase 8 — Image endpoints
```

Updated on every commit. "Currently working on" must point at a specific file or endpoint, never "various refactoring."

### 20.2 Resume procedure

On session start (fresh or resumed):

1. `git status` and `git log --oneline -20`.
2. `cat PORT_PROGRESS.md`.
3. `cat PORT_TODO.md`.

Then resume the current task. If `git diff HEAD` shows uncommitted changes, finish them and commit. Otherwise pick up at the next file/endpoint per `PORT_PROGRESS.md`.

---

## 21. Localization

ARA is English-only in v0.0.1. The English `Locale.resx` is preserved for remaining `Locale.Instance[...]` references in non-UI code. All other language files were deleted in §4.3.

When porting NINA logic into ASP.NET Core endpoints, replace `Loc.Instance[...]` in API responses with hard-coded English strings — the API does not localize. Client-side localization is a v0.2.0 feature.

---

## 22. Final pass (Phase 15)

1. Sweep `PORT_TODO.md`: every `// TODO(port)` and `// PORT_BLOCKED` resolved or explicitly accepted in `PORT_DECISIONS.md`.
2. Run the gate one more time including `-c Release` and `flutter build` for every platform.
3. Smoke test end-to-end:
   - Bring up `OpenAstroAra.Server` on a Linux ARM64 host (Pi or Docker).
   - Launch the Mac client. Discover via mDNS. Connect with the token.
   - Verify equipment dashboard shows AlpacaBridge-exposed simulator devices.
   - Run a 2-target sequence with simulator camera + simulator mount; openastro-phd2 connects and dithers.
   - Disconnect the client mid-sequence; wait 5 minutes; reconnect; verify session continued and frames were captured.
   - Open every section of the app. Note regressions.
4. Update `RELEASE_NOTES.md` with `## 0.0.1-ara.1 — first release` section:
   - Headless server + cross-platform client architecture
   - Alpaca-only equipment
   - Plugin support deferred to v0.1.0
   - Behavioral parity goals vs. upstream NINA where applicable
   - Lineage attribution
   - Known issues, install instructions
5. Bump `CommonAssemblyInfo.cs` to `0.0.1.0`; informational `0.0.1-ara.1`. Bump `pubspec.yaml` to `0.0.1+1`.
6. Open PR from `port/ara` to `master` (or fresh `develop`) with `PORT_DECISIONS.md` contents as description. **Do not merge** — user reviews.

---

## 23. Quick reference — bash one-liners

```bash
# Find leftovers from deleted things
grep -rln "System\.Windows\|UseWPF\|Microsoft\.Web\.WebView2\|ASCOM\.Com\|nikoncswrapper" --include="*.cs" --include="*.csproj" .

# Bulk-rename NINA → OpenAstroAra inside a directory
find $DIR -name "*.cs" -exec sed -i.bak 's/NINA\./OpenAstroAra\./g' {} \;
find $DIR -name "*.cs.bak" -delete

# Verify server builds for ARM64
dotnet publish OpenAstroAra.Server -c Release -r linux-arm64 --self-contained

# Tail server logs from a remote Pi
ssh pi 'sudo journalctl -u openastroara-server -f'

# Regenerate Dart client from OpenAPI spec
cd client/openastroara_client && dart run build_runner build --delete-conflicting-outputs

# Run client against a local-running server during dev
cd client/openastroara_client && OPENASTROARA_DEFAULT_HOST=localhost:5400 flutter run -d macos
```

---

## 24. What "done" looks like

- Server builds on Linux ARM64, x64, Windows, macOS via `dotnet build`. Linux ARM64 publish is the canonical artifact.
- Client builds on macOS (Apple Silicon), iOS, Android, Windows, Linux desktop via `flutter build`. Every platform produces a working binary.
- `OpenAstroAra.Server` runs as a systemd daemon on a Pi, discovered via mDNS, accepts authenticated client connections.
- `OpenAstro Ara` (Flutter client) on a Mac discovers the server, connects with a token, displays equipment status, runs a sequence to completion, displays preview JPEGs as frames complete, supports clean disconnect/reconnect mid-sequence.
- Smoke test in §22.3 passes end-to-end on a Mac + RPi setup with simulator equipment and openastro-phd2.
- No bundled native vendor SDKs, no WPF UI code, no plugin loader, no upstream-NINA branding (except attributions in NOTICE.md, AUTHORS, About, README per §17).
- All MPL license headers preserved per §17.3.
- `PORT_DECISIONS.md`, `PORT_TODO.md`, `PORT_PROGRESS.md`, `API_CONTRACT.md` reflect the full history.
- PR description summarizes the work and links the four tracking files.

Begin Phase 0.5.

---

## 25. Visual design reference — cloning NINA's UX

The Flutter client deliberately mirrors NINA's UX so existing astrophotographers feel at home. This section documents what to clone, what to substitute, and the IP boundaries.

### 25.1 IP boundary

**Free to clone (not copyrightable):**
- Layout: top bar + left panel + center tabs + right panel + bottom status bar
- UX flows: how a sequence is built, how plate solving is invoked, how the framing assistant works
- Control labels and terminology: "Exposure", "Gain", "Offset", "Filter", "Cooler Target", "Framing Assistant", "Sky Atlas", "Plate Solve", "Dither", etc.
- Color palette decisions (dark theme with status accents)
- Information density and arrangement
- Workflow patterns (equipment chooser → connect → manual control → sequence build → run)

**NOT free to copy — placeholder only:**
- Bitmap icons (`Logo_Nina.ico`, the camera/mount/focuser device icons, any custom rendered glyphs)
- Splash screen
- Specific photographs or imagery (any sample images that ship with NINA — do not include)
- The NINA wordmark or any stylized "N.I.N.A." rendering

For v0.0.1: every icon is a Flutter Material icon (`Icons.camera_alt`, `Icons.adjust`, etc.) or a simple SVG primitive drawn from scratch. Mark each with `// TODO(branding): replace with custom ARA icon before public release`.

### 25.2 Color tokens (Flutter theme)

```dart
class AraColors {
  // Backgrounds
  static const bgPrimary    = Color(0xFF1A1A1A);  // main app background
  static const bgPanel      = Color(0xFF262626);  // side panels
  static const bgPanelAlt   = Color(0xFF2E2E2E);  // alternate row / hover
  static const bgInput      = Color(0xFF333333);  // text fields, dropdowns
  static const border       = Color(0xFF404040);  // panel borders, dividers

  // Text
  static const textPrimary   = Color(0xFFE0E0E0);
  static const textSecondary = Color(0xFFA0A0A0);
  static const textDisabled  = Color(0xFF606060);

  // Accents (status indicators)
  static const accentConnected    = Color(0xFF4CAF50);  // green
  static const accentBusy         = Color(0xFFFFB300);  // amber
  static const accentError        = Color(0xFFE53935);  // red
  static const accentInfo         = Color(0xFF42A5F5);  // blue
  static const accentDisconnected = Color(0xFF606060);  // gray

  // Highlight
  static const selectionBg     = Color(0xFF1976D2);
  static const selectionFg     = Color(0xFFFFFFFF);
  static const buttonPrimary   = Color(0xFF1565C0);
  static const buttonSecondary = Color(0xFF424242);
}
```

Use Material 3 `ThemeData` with these tokens. Material's default dark theme is too light/colorful for an observatory app used in the dark.

### 25.3 Top equipment bar

A horizontal `Row` of equipment "chips" — one per device type. Each chip:
- Icon (Material icon for v0.0.1)
- Device-type label below the icon (small caps: "CAM", "FW", "FOC", "MOUNT", "ROT", "GUIDE", "DOME", "SW", "FLAT", "WX", "SAFE")
- Colored dot indicator (top-right of icon): gray (disconnected), green (connected + idle), amber (busy), red (error)
- Tap: opens chooser bottom sheet listing discovered Alpaca devices
- Long-press / right-click: disconnect, or open device-specific properties panel

Order (left-to-right, mirroring NINA's bar):
1. Camera
2. Filter Wheel
3. Focuser
4. Mount (telescope)
5. Rotator
6. Guider (PHD2)
7. Flat Panel
8. Switch
9. Weather
10. Safety Monitor
11. Dome

### 25.4 Left panel — profile + manual control

Two vertical sections:

**Profile selector** (top, fixed-height):
- Dropdown listing all profiles from `~/.config/openastroara/profiles/`
- "Active profile: <name>" label
- Gear icon → opens profile editor in a modal

**Manual control accordion** (below, scrollable):
- One expandable card per connected device
- Card contents = the device's relevant manual controls (camera: exposure/gain/offset/cooler; mount: slew controls + park/unpark; focuser: position + step; filter wheel: filter selector; rotator: angle + reverse; guider: connect/start/stop)
- Cards remain in the layout when disconnected, but content is disabled and shows "Not connected"

### 25.5 Center tabs — workspace

Five tabs along the top of the center area:

1. **Imaging** — the live capture workspace
   - Main area: the most-recent frame's preview JPEG, rendered with `Image.network(previewUrl)`. Zoom + pan via `InteractiveViewer`.
   - Overlay (toggle-able): plate-solve results (RA/Dec, rotation, pixel scale)
   - Right-side controls: exposure/gain/offset, "Take One" button, "Live View" toggle, sequence shortcut to start
   - Bottom: thumbnail strip of recent frames in the current session
2. **Framing Assistant**
   - Top: target search (DSO catalog query — Messier, NGC, IC, by name or coords)
   - Center: sky chart preview (basic for v0.0.1 — just a labeled scatter of stars from a small star catalog)
   - Right: framing parameters (FOV, rotation, mosaic panel grid)
   - "Set as Target" button → adds to sequence
3. **Sequencer**
   - Tree view of: Areas → Targets → Instructions (NINA's hierarchy)
   - Drag-and-drop reordering within a level
   - Per-instruction editor pane on the right when selected
   - Top toolbar: New / Load / Save / Validate / Run / Pause / Abort
   - Below toolbar: progress bar + currently-executing instruction label
4. **Sky Atlas** — **embedded Aladin Lite (CDS) with bundled catalogs + Tonight's Sky planetarium view**. See §36 for full spec. Two sub-modes:
   - **Catalog View** — Aladin Lite in standard equatorial mode, free pan/zoom, full HiPS survey browsing (21 surveys, see §36), universal search (Simbad online + bundled name index offline)
   - **Tonight's Sky** — same Aladin instance, zenith-centered, horizon-aware, time-controlled, solar system (Sun/Moon/planets) + comets overlaid, planetarium-style. Replaces NINA's external-planetarium integration (Cartes du Ciel, Stellarium) — ARA is the planetarium.
   - Tap an object → details panel + "Set as Target in Framing Assistant" CTA
   - Aladin logo + CDS attribution preserved at bottom-right per Aladin's GPLv3 license terms (see §17 + §36)
5. **Options**
   - Tree-based settings: Equipment (per device type), Imaging, Plate Solving, Astrometry, File Saving, Telescope, Astronomy, Sequence, Application
   - Right pane: settings for selected node
   - "Save Profile" / "Save Profile As..." buttons in the toolbar

### 25.6 Right panel — analysis

Three stacked sections, each collapsible:

1. **Histogram + image statistics** — built with `fl_chart` or a custom CustomPaint. Shows the last frame's RGB or luminance histogram, plus min/median/max/mean/std-dev/stars-detected.
2. **Plate solve panel** — last plate-solve result: solved RA/Dec, rotation, pixel scale, FOV; "Solve Last Frame" button.
3. **Log tail** — last 50 log lines from the server, color-coded by level (info/warning/error). "Pause", "Clear", "Open Full Log" buttons.

### 25.7 Bottom status bar

`Row` along the bottom of the main window:
- Left: local time + sidereal time (computed from server's reported lat/long)
- Center: current operation ("Capturing target M42 — frame 4/20, 180s") + progress bar
- Right: server connection state ("Connected: pi-observatory.local — v0.0.1-ara.1") + token-reset button

### 25.8 Mobile differences

iPad: same desktop layout, slightly more compact, side panels can be drawer-style if needed.

iPhone/Android: bottom-tab navigation as in §12.3. Per-tab the layout collapses to the relevant content only (no side panels). Sequence editing is read-only or limited on phone in v0.0.1 — full editing is a desktop-class workflow; phone is for monitoring.

### 25.9 Reference materials

Before writing client code, the AI should:
1. Browse NINA's documentation site for layout reference: `https://nighttime-imaging.eu/docs/`
2. Look at NINA's existing screenshots in `/Users/joey/Documents/GitHub/nina/README.md` and any docs subdirectory (these will be deleted in §4.3 — capture them via `git log master --name-only` or similar before deletion if needed)
3. Capture the layout in pseudo-mockups in `client/openastroara_client/docs/mockups/` as Markdown ASCII art if useful for self-reference

The implementation does not need to be pixel-perfect to NINA — it needs to be *recognizable* to a NINA user as the same workflow. Familiar enough that a user who knows NINA can navigate ARA on day one without reading docs.

### 25.10 Decisions that diverge from NINA

These are deliberate departures, documented up front so the AI doesn't try to clone them:

- **AvalonDock panel rearrangement** — not supported in v0.0.1. Static layout only.
- **MGEN guider tab** — gone (NINA.MGEN deleted). Guider section is PHD2-only.
- **Plugin browser tab** — gone (plugin support deferred to v0.1.0).
- **Built-in updater UI** — gone (per §18.A).
- **Patreon / donate banner** — gone (per §18.D).
- **Language picker** — gone (English-only, §18.E).
- **Web browser panel** (NINA's WebView2 panel for catalog access) — replaced by "Open in external browser" buttons.
- **Settings tree depth** — flatten where NINA goes deep for things that no longer exist (vendor-specific camera settings, MGEN settings, plugin settings, etc.).

---

## 26. Image processing on Linux — OpenCvSharp4 migration

This is the single biggest technical risk in the port and must be handled in Phase 5 (Image).

### 26.1 The problem

NINA's image pipeline uses `System.Drawing.Common` for `Bitmap`, `Graphics`, `ImageConverter`, etc. In .NET 6 and later, **`System.Drawing.Common` is Windows-only by default**. On Linux you get `PlatformNotSupportedException` at first use. The historical workaround `libgdiplus` is deprecated and crashes intermittently on ARM64.

NINA also uses `System.Windows.Media.Imaging` (`BitmapSource`, `WriteableBitmap`, `RenderTargetBitmap`, `FormatConvertedBitmap`, `CroppedBitmap`) — these are WPF types, not available on Linux at all.

Neither approach works for an ARM64 Linux daemon.

### 26.2 The solution — OpenCvSharp4

PI.N.S. proved this path: replace `System.Drawing` + `System.Windows.Media.Imaging` calls with **OpenCvSharp4** (`Mat`, `Cv2.ImEncode`, etc.). OpenCV is the standard image-processing library in astronomy (used by INDI, KStars, PixInsight scripts), ARM64 binaries ship in the NuGet package, license is BSD-3 (clean for MPL).

```xml
<PackageReference Include="OpenCvSharp4" Version="4.11.*" />
<PackageReference Include="OpenCvSharp4.runtime.linux-arm64" Version="4.11.*" Condition="'$(RuntimeIdentifier)' == 'linux-arm64'" />
<PackageReference Include="OpenCvSharp4.runtime.linux-x64"   Version="4.11.*" Condition="'$(RuntimeIdentifier)' == 'linux-x64'" />
<PackageReference Include="OpenCvSharp4.runtime.win"         Version="4.11.*" Condition="$([MSBuild]::IsOSPlatform('Windows'))" />
<PackageReference Include="OpenCvSharp4.runtime.osx"         Version="4.11.*" Condition="$([MSBuild]::IsOSPlatform('OSX'))" />
```

### 26.3 Translation cheatsheet

| WPF / System.Drawing | OpenCvSharp4 |
|---|---|
| `new Bitmap(width, height, PixelFormat.Format24bppRgb)` | `new Mat(height, width, MatType.CV_8UC3)` |
| `bitmap.Save(path, ImageFormat.Jpeg)` | `Cv2.ImWrite(path, mat)` (extension drives format) |
| `bitmap.LockBits(...)` then `Marshal.Copy` | `mat.GetArray<byte>()` / `mat.SetArray(byte[])` — direct buffer access |
| `WriteableBitmap.WritePixels(...)` | `mat.SetArray(...)` |
| `BitmapSource` parameter type | `Mat` (the OpenCV canonical type) |
| `Convert16BppTo8Bpp` (NINA helper) | `mat.ConvertTo(out8bit, MatType.CV_8U, 1.0/256.0)` |
| `FormatConvertedBitmap(source, PixelFormats.Gray8, ...)` | `Cv2.CvtColor(src, dst, ColorConversionCodes.BGR2GRAY)` (or appropriate code) |
| `CroppedBitmap(source, rect)` | `new Mat(source, new Rect(x, y, w, h))` — ROI view |
| `RenderTargetBitmap` (rendering a UI element to a bitmap) | **N/A** — no UI in server. Delete the call sites. |
| JPEG preview generation | `Cv2.ImEncode(".jpg", mat, out byte[] buf, new[] { (int)ImwriteFlags.JpegQuality, 80 })` |
| Debayering | `Cv2.CvtColor(rawMat, rgbMat, ColorConversionCodes.BayerRG2RGB)` |

### 26.4 What gets deleted vs. translated

| In NINA | Action |
|---|---|
| Anything using `RenderTargetBitmap` to capture a UI element | **Delete** — no UI |
| `ImageUtility.ConvertBitmap` (BitmapSource ↔ Bitmap conversions) | **Rewrite** — pipeline operates on `Mat` end-to-end |
| FITS reader (custom format, doesn't depend on System.Drawing) | **Keep as-is** — outputs `ushort[]` or `byte[]` buffers, fed into `Mat` |
| Stretch / histogram code (operates on raw buffers) | **Keep as-is** — just feeds Mat instead of Bitmap |
| Star detection (`HocusFocus`, `HFR`, etc.) | **Audit** — uses Accord.Math + raw arrays; should port cleanly. Some Accord types depend on System.Drawing; replace with OpenCV equivalents (`Cv2.HoughCircles`, `Cv2.FindContours`, etc.) where present. |

### 26.5 Phase 5 (Image) task order

1. Add OpenCvSharp4 references to `OpenAstroAra.Image.csproj`.
2. Run `grep -rn "System\.Drawing\|System\.Windows\.Media\.Imaging" --include="*.cs" OpenAstroAra.Image/` to enumerate all call sites.
3. Per call site, apply the translation cheatsheet. Commit one file at a time.
4. Delete `RenderTargetBitmap` call sites entirely (UI-only, server doesn't render visuals).
5. Verify image pipeline tests still pass on Linux: `dotnet test -c Debug --runtime linux-arm64` (cross-compile build, run in Docker emulation if no Pi available).
6. Server smoke test (Phase 10): capture from Alpaca simulator → JPEG preview generated and served → file size and dimensions sane.

### 26.6 What we don't borrow from PI.N.S.

PI.N.S.'s `System.Windows.Compat` library mocks `System.Windows.*` namespaces so NINA's WPF UI code still compiles on Linux. **We don't need that** — we delete the WPF UI entirely in §4.2. Trying to keep the compat shim would mean carrying a parallel WPF-look-alike API surface that nothing in ARA actually uses.

The OpenCvSharp4 *technique* we borrow. The compat layer we don't.

### 26.7 Lineage attribution update

`NOTICE.md` adds a paragraph crediting PI.N.S. for proving the Linux feasibility:

> The Linux image-processing approach (OpenCvSharp4 in place of System.Drawing.Common) was pioneered by [PI.N.S. (PI 'N' Stars)](https://github.com/nitr57/pins), a separate Linux fork of N.I.N.A. by nitr57. ARA uses the same library choice but does not depend on or fork the PI.N.S. codebase; we start fresh from NINA 3.2 master.

---

## 27. Connection policy — single-client at a time

ARA serves **one connected client at a time** to eliminate command-conflict edge cases entirely. New connection attempts go through a hand-off dance mediated by the currently-connected client.

### 27.1 Flow

```
new client → POST /api/v1/server/connect (token)
   │
   ├─ no current client      → 200 + session ID, new client takes over
   │
   ├─ current client online  → server sends current client a WebSocket event:
   │                              { "type": "connection.request",
   │                                "from": "ipad.local",
   │                                "request_id": "..." }
   │                          → current client shows modal:
   │                              "ipad.local wants to connect.
   │                               [Allow]  [Keep me connected]"
   │                          → current replies on WS:
   │                              { "type": "connection.response",
   │                                "request_id": "...",
   │                                "action": "allow" | "reject" }
   │                          → server replies to new client:
   │                              200 (allow — current gets a "disconnected" toast)
   │                              409 (reject — "Server in use by mac.local")
   │
   └─ current client unresponsive (no WS pong in 60s)
                              → server marks current as dead, accepts new client immediately
```

### 27.2 Timeouts

| Condition | Timeout | Action |
|---|---|---|
| Current client doesn't respond to `connection.request` | 30 s | 409 to new client: "Current client unresponsive, try again in 60s" |
| WS pong missing from current client | 60 s | Mark current as dead; next `connect` call succeeds |
| WS pong missing during normal operation | 60 s | Connection dropped, session ends, sequence keeps running on server |

### 27.3 Endpoints

- `POST /api/v1/server/connect` (token in body) → 200 + session, or 409 (in-use), or 503 (server starting / sequence-only mode)
- `POST /api/v1/server/disconnect` → graceful disconnect, releases the slot
- `GET /api/v1/server/session` → current session info (controller hostname, since when, idle time)

### 27.4 Out of scope for v0.0.1

- Multi-client read-only spectator mode (a "watch only" connection) — could be v0.1.0
- Persistent admin override token (force-disconnect anyone) — could be v0.1.0

---

## 28. Sequence durability & crash recovery

Power blips, server crashes, kernel hiccups, Wi-Fi resets — none should cost a night of imaging. ARA checkpoints sequence state to SQLite and runs a structured recovery on restart.

### 28.1 Checkpointing

- Persistence engine: SQLite at `${config}/openastroara.db`.
- `sessions` table: id, profile_id, sequence_json, started_at, ended_at, recovery_needed (bool), last_completed_instruction_id, current_target_id, frame_count
- `frames` table: id, session_id, target_id, instruction_id, fits_path, captured_at, filter, exposure_seconds, etc.
- **Write points:**
  - Session start → row inserted with `recovery_needed = true`
  - After every completed sequencer instruction → `last_completed_instruction_id` updated
  - After every FITS save → row inserted in `frames` (this is the canonical "frame succeeded" signal)
  - Graceful shutdown → `ended_at` set, `recovery_needed = false`
- **In-flight frame at crash time is lost** (that exposure was interrupted). Recovery resumes from the instruction *after* the last completed one.

### 28.2 Recovery routine (runs on server startup if `recovery_needed = true`)

1. **Reconnect equipment** — re-enumerate Alpaca devices from the saved session's profile. If any fail to connect: retry 3× with 5s spacing; log per-device status; continue without missing devices (graceful degradation).
2. **Mount home** — issue `FindHome` (if mount supports it) or slew to the configured park position. Provides a known reference; resolves meridian-flip ambiguity.
3. **Cooler setup** (if camera has cooler):
   - Set target temp from profile (e.g., −10°C)
   - Ramp at configured rate (default **1°C/min** — too fast risks condensation/thermal stress)
   - Wait for stabilization: within **±0.5°C for 60 s continuous**
   - Max wait timeout: **10 minutes**. If not stabilized (warm night, cooler failing), queue warning notification and proceed anyway.
   - Skip entirely if camera reports no cooler.
4. **Altitude check on active target:**
   - Hard floor: target's `MinAltitude` setting from profile, fallback **5°** if unset
   - If below hard floor: skip target, advance to next in sequence, repeat altitude check
   - If at or above hard floor but **< 30°**: queue soft-warning notification `{ "type": "altitude.warning", "payload": { "target": "M42", "altitude": 18.4, "actions": ["continue", "skip"] } }`; default action if no user response = **continue** (unattended operation keeps running)
   - If all remaining targets below hard floor: log "session ended early — all targets below horizon", park mount, mark session `ended_at`, stop
5. **Slew to target** at saved RA/Dec
6. **Plate solve** with rotation:
   - Position tolerance: **60 arcsec** (NINA default)
   - Rotation tolerance: **1°**
   - Retries: **3** with re-slew + re-solve between attempts
   - On failure: log + queue notification, abort recovery (await user)
7. **Filter selection** (if filter wheel connected):
   - Look ahead in the sequence to the next instruction that uses a filter
   - Switch wheel to that filter
   - If no filter wheel: skip
8. **Autofocus** (if focuser connected):
   - Run autofocus on the selected filter (correct wavelength → correct focus, since temperature may have drifted during the outage)
   - Use NINA's existing autofocus routine
   - If no focuser: skip with log warning ("focus may have drifted during outage")
9. **Resume guiding** (if guider configured + connected):
   - Start PHD2, wait for calibration / settle
   - If no guider: skip
10. **Continue sequence** from the instruction *after* `last_completed_instruction_id`

### 28.3 Total timeout

If recovery hasn't successfully reached step 10 within **15 minutes**, abort:
- Log the failure point
- Park mount
- Set session `ended_at`, clear `recovery_needed`
- Queue notification: *"Recovery failed at step X — please reconnect and review"*

User can reconnect and manually resume or skip.

### 28.4 Soft 30° altitude warning during normal operation

The same warning pattern fires during normal sequence execution, not just recovery. NINA's per-target `MinAltitude` is the hard floor (sequence will not image below it). The 30° soft warning is purely advisory:

- Before starting a target, check altitude
- If < 30° (but ≥ hard floor): queue `altitude.warning` notification with [Continue]/[Skip] actions
- Default (no response): continue imaging — the user put it in the sequence, server respects that

### 28.5 Out of scope for v0.0.1

- Resume mid-instruction (e.g., picking up frame 4 of 10 when the crash happened at frame 3 of 10). For v0.0.1 we resume at instruction granularity; the in-progress instruction restarts from its first frame.
- Multi-mount / multi-camera sessions.
- Resume across multiple nights with target re-acquisition based on actual time skew (assumes user resumes the same night).

---

## 29. Storage / disk-space policy

Two distinct storage domains: **Pi side** (FITS frames + session state + profiles, server-managed) and **WILMA side** (bundled catalogs + downloaded sky imagery surveys + cached tiles + draft sequences, client-managed).

### 29.0 WILMA-side storage (Flutter client)

Goes in platform-default app data directory:
- macOS: `~/Library/Application Support/OpenAstroAra/`
- iOS: app sandbox documents
- Android: app sandbox files dir
- Windows: `%APPDATA%\OpenAstroAra\`
- Linux: `~/.local/share/openastroara/`

Contents:
| Sub-directory | Contents | Size budget |
|---|---|---|
| `catalogs/` | Bundled HYG, Tycho-2, NGC/IC, Caldwell, Sharpless, MPC comets, constellation art, nebula vectors | ~200 MB (bundled, immutable) |
| `hips/<survey>/` | Downloaded HiPS tile sets per §36 (DSS2, Mellinger, eROSITA, etc.) | Variable — 0 GB to TB depending on what user downloads |
| `aladin-cache/` | Live-fetched HiPS tiles from CDS (auto-managed by Aladin Lite) | LRU-capped at user-configurable size (default 2 GB) |
| `sequences/` | Draft + saved sequence JSON files built in WILMA | Tiny (KB per file) |
| `profiles/` | Profile drafts (sync to Pi when connected) | Tiny |
| `frames-downloaded/` | FITS files the user pulled from the Pi for review | Variable, user-managed |
| `logs/` | Client-side logs | Rolling, capped at 100 MB |

Settings → Storage on WILMA shows total usage, per-survey breakdown, "Clear cache" button per category.

### 29.1 Save location (Pi side)

Uses the existing NINA image-save-path setting from the active profile, exposed via API. Server default if unset: `${data_dir}/captures/` (`/var/lib/openastroara/captures/` on Linux). Users are strongly encouraged to point this at a USB drive (see DEPLOY.md) rather than the Pi's SD card.

### 29.2 Storage info endpoint

`GET /api/v1/server/storage` returns:

```json
{
  "save_path": "/media/usb1/captures",
  "is_usb_mount": true,
  "total_bytes": 1099511627776,
  "available_bytes": 442381533184,
  "frame_size_estimate_bytes": 52175232,
  "frames_remaining_estimate": 8479,
  "camera": {
    "name": "ZWO ASI2600MM Pro",
    "dimensions": "6248×4176",
    "bin": "1×1",
    "bit_depth": 16
  }
}
```

If no camera connected: `frame_size_estimate_bytes` defaults to 40 MB (a typical mid-sized CMOS estimate), with `camera: null`.

### 29.3 Frame-size estimation formula

```
bytes_per_pixel = 2 if camera.MaxADU > 255 else 1
frame_size_bytes = ceil(width / binX) * ceil(height / binY) * bytes_per_pixel + 16384  // FITS header overhead
```

Uses **uncompressed worst-case** for safety — real frames may compress to 30-60% via FITS RICE or XISF compression, but estimating high prevents surprises mid-night.

### 29.4 Sequence-start validation

When client sends "start sequence," server first checks:

- If `available_bytes < 2 GB`: include a warning in the start response
- If `is_usb_mount = false` (save path is on SD card or internal): include a USB-recommendation warning

Client displays the result as a confirmation modal before actual sequence kickoff:

> Save location: `/media/usb1/captures` — 412 GB free.
> ZWO ASI2600MM Pro at 1×1 bin produces ~52 MB per frame; room for ~8,479 frames.
>
> [Start sequence]  [Cancel]

Or if low / on SD card:

> ⚠ Save location is the Pi's SD card with 8.2 GB free (~110 frames).
> Recommended: configure a USB drive in Settings → Storage.
>
> [Start anyway]  [Cancel]

### 29.5 Mid-sequence disk full

- Capture write fails (`IOException: No space left on device`)
- Sequencer pauses at that instruction; state is checkpointed
- Queued notification: *"Capture failed — disk full at frame N. Free space or change save location, then resume."*
- No automatic deletion or rotation in v0.0.1. User intervenes.
- v0.1.0 may add rotation policies (delete oldest unflagged, archive to cloud, etc.)

### 29.6 Settings → Storage panel (client)

- Current save path, free space, USB / SD indicator
- "Browse" picker → opens server-side folder browser endpoint (`GET /api/v1/server/filesystem?path=...`) so user picks a directory on the Pi
- "Reset to default" button
- Link to DEPLOY.md USB-mount instructions

### 29.7 DEPLOY.md content (USB on Raspberry Pi OS)

```
# Install auto-mount tooling
sudo apt install -y usbmount

# Create a stable mount point
sudo mkdir -p /media/openastroara

# Identify your USB drive (e.g., /dev/sda1)
lsblk

# Mount at a known path
sudo mount /dev/sda1 /media/openastroara

# Persistent via fstab (replace UUID with output of `blkid /dev/sda1`):
echo 'UUID=<uuid> /media/openastroara ext4 defaults,nofail,user 0 0' | sudo tee -a /etc/fstab

# Tell ARA to save there:
sudo nano /etc/openastroara/server.env
# Add: STORAGE_PATH=/media/openastroara/captures
sudo mkdir -p /media/openastroara/captures
sudo chown openastroara:openastroara /media/openastroara/captures
sudo systemctl restart openastroara-server
```

---

## 30. First-run + launch flow (client)

### 30.1 Launch sequence

1. **Splash screen** (1-2 seconds) — ARA logo placeholder. mDNS server discovery runs in background simultaneously.
2. **Server connect** — only shown when:
   - mDNS finds 0 servers (manual IP/port entry), OR
   - mDNS finds 2+ servers (user picks), OR
   - no saved token for the chosen server
   - If exactly 1 server is found AND a token is saved, skip this screen
3. **Profile box** — always shown, layout below
4. **Main app** — the NINA-style shell from §25

### 30.2 Profile box

```
┌──────────────────────────────────────────┐
│  OpenAstro Ara                           │
│  Connected to pi-observatory.local       │
│                                          │
│  Active profile:                         │
│    [▼ My Backyard Rig            ]       │   ← shown when ≥1 profile exists
│                                          │
│           [   Image   ]                  │   ← primary action, shown when ≥1 profile exists
│                                          │
│  ─────────  or  ─────────                │
│                                          │
│  [ + Add a Profile ]  [↗ Import Profile ]│   ← ALWAYS visible
└──────────────────────────────────────────┘
```

- **Existing profiles**: dropdown + [Image] visible; [Add] / [Import] also visible below
- **No profiles**: dropdown + [Image] hidden; [Add] / [Import] are the only actions
- Add / Import never gated behind picking an existing profile — users can experiment freely

### 30.3 Subsequent launches

Splash → (auto-connect using saved server + token) → Profile box pre-selects last-used profile → click [Image] → main app. **Three taps from cold-launch to imaging.**

### 30.4 Add a Profile (minimal walkthrough)

Modal with three fields:

- **Profile name** (required)
- **Site latitude / longitude** (optional — also accepts "Use my device's location" if Flutter location permission granted; useful for alt/az calculations and dawn/dusk timing)
- **Copy equipment settings from**: dropdown of existing profiles (optional; defaults to "None — start blank")

[Save] → server endpoint `POST /api/v1/profiles` → returns new profile, becomes selected in dropdown, modal closes.

**Equipment setup is intentionally NOT in the wizard.** Users add cameras / mounts / focusers / etc. via the Equipment tab once they're in the main app. Keeps the wizard from being overwhelming on first launch and gets users to "I'm in the app" quickly.

### 30.5 Import a Profile

Modal with a file picker:

- Accepted formats: `.profile.xml` (NINA's existing profile format) and `.profile.json` (future ARA-native format)
- Client uploads to `POST /api/v1/profiles/import` (multipart)
- Server validates schema, returns the parsed profile or a validation error
- Imported profile becomes selected
- If the import is a NINA profile that references equipment ARA can't replicate (e.g., a vendor-specific COM driver), profile imports successfully but those equipment slots are blanked with a one-time notification: *"Imported — your camera setting referenced ASCOM.QHYCCD.Camera which ARA doesn't support; please reselect via Alpaca."*

### 30.6 Token management

- Token saved per-server in `flutter_secure_storage`
- Settings → Server panel: shows current server + connection state, "Forget this server" / "Re-enter token" buttons
- Forget = wipes saved token; next launch shows the connect screen for that server again

---

## 31. Time + location sync (WILMA waterfall)

ARA Core needs accurate UTC time and lat/long/altitude for: sidereal time, alt/az transforms, dawn/dusk schedules, plate-solve search, sequence triggers. A Pi without internet doesn't keep time (no RTC by default). WILMA helps via a waterfall of sync sources.

### 31.1 Flow

After profile selection on WILMA:

```
GET /api/v1/server/time-sync  → server reports sync state
     │
     ├─ synced & fresh (< 1h ago, trust ≥ medium) → proceed
     │
     └─ unsynced or stale → walk the waterfall:

         1. WILMA has internet?           push device clock + GPS/location
                                          POST /api/v1/server/time-sync
              (modern phones/laptops on Wi-Fi or cellular are NTP-synced)
              │ no
              ↓
         2. Server detects USB GPS on Pi? server self-syncs from /dev/ttyUSB* or /dev/ttyACM*
              (gpsd or direct NMEA read; no user action required)
              │ no
              ↓
         3. WILMA is mobile (iOS/Android)? push device clock + GPS (Flutter geolocator)
              │ no
              ↓
         4. Prompt: "Plug a USB GPS into the Pi" + [Retry]
              │ user skips
              ↓
         5. Manual entry modal: UTC date/time + lat/long/altitude
```

### 31.2 Trust levels

| Source | Trust |
|---|---|
| Pi NTP (if internet reaches the Pi directly) | high |
| USB GPS via gpsd / NMEA | high |
| WILMA on Wi-Fi or cellular with internet (NTP-synced device clock) | medium |
| Mobile GPS (no internet, device geo-fix only) | medium |
| Manual entry | low |

Trust is stored alongside the sync. Soft warnings fire when:
- Trust = `low` and the sequence about to run uses schedule-based instructions (`Wait until dusk`, etc.)
- Drift > 30s detected during a session

### 31.3 Endpoints

```
GET /api/v1/server/time-sync
Response:
{
  "synced": true,
  "source": "ntp|gps-internal|gps-external|client|manual|none",
  "trust": "high|medium|low|none",
  "current_time_utc": "2026-05-19T03:14:15.123Z",
  "system_time_offset_seconds": 0.0,
  "location": { "lat": 30.27, "lng": -97.74, "alt": 165.0 },
  "internet_available_on_pi": false,
  "internal_gps_available": false
}

POST /api/v1/server/time-sync
{
  "source": "client|gps-mobile|manual",
  "time_utc": "2026-05-19T03:14:15.123Z",
  "location": { "lat": 30.27, "lng": -97.74, "alt": 165.0 },
  "trust": "medium"
}
Response: {
  "before": { "time_utc": "...", "offset_seconds": -42.5 },
  "after":  { "time_utc": "...", "offset_seconds": 0.0 },
  "location_updated": true
}
```

### 31.4 Implementation notes

- Server sets the system clock via a CAP_SYS_TIME-granted helper. DEPLOY.md adds `sudo setcap cap_sys_time+ep /opt/openastroara/OpenAstroAra.Server` post-install.
- USB GPS detection: server scans `/dev/ttyUSB*` and `/dev/ttyACM*` on startup for NMEA `$GPRMC` / `$GPGGA` sentences. If detected, server self-syncs without WILMA involvement.
- WILMA mobile GPS: `geolocator` Flutter plugin, permission-prompts on first use. Cached for the session.
- All astronomy computations use **UTC internally**; client displays in user's local TZ.
- Time-sync state is cached per-session; doesn't re-prompt unless the Pi reboots or > 12 hours pass.

---

## 32. Network resilience (WILMA ↔ Pi)

WILMA loses Wi-Fi mid-session. WebSocket dies. The sequence keeps running on the Pi regardless — disconnect ≠ pause. WILMA's job is to detect, communicate, and recover.

### 32.1 Disconnect detection

- WebSocket close event OR no pong within 60s (per §27.2)
- Client first attempts **silent reconnect for 5 seconds** (handles transient drops)
- After 5s of failed silent attempts, show the disconnect modal

### 32.2 Disconnect modal

```
┌──────────────────────────────────────────┐
│  ⚠ Disconnected from Ara Core            │
│                                          │
│  Your device lost connection. Check that │
│  you're still on the Ara Core Wi-Fi      │
│  network.                                │
│                                          │
│  [ Verify Network ]    [ Try Again ]     │
└──────────────────────────────────────────┘
```

### 32.3 [Verify Network] flow

1. Spinner: *"Searching for Ara Core..."*
2. Client runs mDNS scan for `_openastroara._tcp.local` + direct probe of last-known server IP/hostname (parallel)
3. Outcomes:
   - **Found + reachable** → *"Found Ara Core. Reconnecting..."* → re-open WebSocket → fetch server state snapshot → rehydrate UI → dismiss modal
   - **Not found** → *"Ara Core not found on this network. Make sure you're connected to the 'Ara Core' Wi-Fi network, then try again."* with [Verify Again]
   - **Found but unreachable** (mDNS resolves; ping/TCP fails) → *"Ara Core is on the network but not responding — it may have crashed or rebooted. Wait a moment and try again."*

### 32.4 During disconnect (no modal yet, 5s silent retry)

- Status bar shows a yellow indicator: *"Reconnecting..."*
- All mutating actions disabled
- Read-only views show last-known state (cached from prior WebSocket events)

### 32.5 Reconnect behavior

- Server is source of truth — client fetches `GET /api/v1/server/state` snapshot on reconnect and rehydrates UI
- Client-side in-flight mutations that didn't receive an HTTP response are **dropped** (v0.0.1) rather than retried — avoids double-execution risk
- Sequence keeps running on the Pi throughout — disconnect doesn't pause

### 32.6 Pi Wi-Fi mode (operational, not in scope for ARA Core .deb)

The Pi runs in one of two Wi-Fi modes, **configured outside ARA Core** per the [OpenAstro wiki](https://wiki.openastro.org):

- **AP mode (default for portable field use)** — Pi runs `hostapd`, creates network "Ara Core" (or whatever SSID user picks), WILMA devices connect to that
- **Client mode (indoor / observatory)** — Pi joins user's home Wi-Fi, accessible from any device on that network

Either way, the modal copy "Ara Core Wi-Fi network" works — it means whichever network the Pi is reachable on. ARA Core's .deb does **not** touch hostapd or Wi-Fi config — networking is user-managed per the wiki.

---

## 33. Version compatibility + WILMA-pushed updates (ASIAir model)

Update mechanism for ARA Core that doesn't require internet on the Pi. The WILMA app embeds the server binary as an asset and pushes it to the Pi over the local network. Field-friendly.

### 33.1 Embedded binary

- WILMA app bundles `linux-arm64` self-contained .NET 10 publish of `OpenAstroAra.Server`
- Trimmed + Native AOT to ~30-50 MB
- Stored at `assets/server/openastroara-server-linux-arm64.tar.gz`
- App build pipeline embeds this from CI before each release

### 33.2 Version check on every connection

Client embeds its own version (`OpenAstroAra.Client.AppInfo.version`). After WebSocket handshake:

```
GET /api/v1/server/info → { server_version: "0.0.1", api_version: "v1", protocol_minor: 3 }
```

Compare:

| Client vs Server | Action |
|---|---|
| Equal | Proceed normally |
| **Client newer** (semver) | Modal: *"Ara Core (v0.0.1) needs to update to match your app (v0.0.2). Update now?"* → [Update Ara Core] / [Cancel] |
| **Client older** | Modal: *"Your app (v0.0.1) is older than Ara Core (v0.0.2). Update via App Store / GitHub Releases — features may misbehave until you do."* → [Continue Anyway] / [Cancel] |
| API major mismatch | Hard block: *"This app cannot talk to Ara Core v1.0.0. Update your app to continue."* |

### 33.3 Update push flow

```
[Update Ara Core] clicked
     ↓
WILMA streams bundled tarball to POST /api/v1/server/update
   Headers: X-OpenAstroAra-Token, X-Update-Version, X-Update-Sha256
   Body: gzipped tarball, Content-Type: application/octet-stream
     ↓
Server:
  1. Validate token, version, sha256
  2. Save tarball to /opt/openastroara/staging/
  3. Verify checksum
  4. Extract to /opt/openastroara/staging/extracted/
  5. Pre-flight: run "new-binary --version" — must succeed in 5s
  6. Invoke /opt/openastroara/update.sh (privileged helper)
  7. Reply 202 Accepted, begin shutdown
     ↓
update.sh (run as root via NOPASSWD sudoers — see DEPLOY.md):
  - dpkg-divert old binary so APT respects local override
  - Atomic: mv current → previous; mv staging/extracted → current
  - systemctl restart openastroara-server
     ↓
New binary boots → smoke test (responds to /api/v1/server/info within 30s)
     ↓
   succeeds                          fails to start
     ↓                                      ↓
Client reconnects, versions match.    systemd watchdog triggers rollback:
Modal closes.                           mv previous → current; restart
                                       Client sees old version still; modal:
                                       "Update failed, rolled back."
```

### 33.4 Trust & integrity (v0.0.1)

- Token auth on the endpoint (existing)
- SHA-256 checksum match before swap
- **v0.1.0 addition**: Ed25519 signature verification with Open Astro's pinned public key (so the user can't push a tampered binary to their own Pi by accident or malice)

### 33.5 Coexistence with APT updates (per §34)

`update.sh` runs `dpkg-divert --add /opt/openastroara/OpenAstroAra.Server` so APT knows the binary is locally-overridden. On subsequent `apt upgrade`, the new APT version stages as a `.dpkg-new` file but does not replace the WILMA-pushed binary. User can manually clear the divert (`dpkg-divert --remove`) to return to APT-managed state.

### 33.6 v0.1.0 scope (noted, not implemented yet)

Same push-from-WILMA mechanism extended to:
- **AlpacaBridge** — bundled binary, `/opt/alpaca-bridge/`, restart via systemd
- **openastro-phd2** — same pattern, `/opt/openastro-phd2/`
- Endpoints: `POST /api/v1/server/components/{name}/update`
- Server detects component versions via the component's own status API (AlpacaBridge `/version`, PHD2 `get_app_state` JSON-RPC)

---

## 34. Distribution + install (apt.openastro.net)

### 34.1 Primary install path

```bash
# 1. User flashes Trixie (Debian 13) on RPi 4/5, Orange Pi, or RockChip SBC — see OpenAstro wiki
# 2. User configures Wi-Fi or Ethernet — see wiki
# 3. Add the OpenAstro APT repo (one-time):
curl -fsSL https://apt.openastro.net/gpg.key | sudo gpg --dearmor -o /usr/share/keyrings/openastro.gpg
echo "deb [signed-by=/usr/share/keyrings/openastro.gpg] https://apt.openastro.net stable main" \
  | sudo tee /etc/apt/sources.list.d/openastro.list
sudo apt update

# 4. Install (single command, pulls everything via Recommends):
sudo apt install openastroara-server
```

### 34.2 Package details

- Name: `openastroara-server` (lowercase, hyphens per Debian convention)
- Arch: **arm64** (works on RPi 4/5, Orange Pi 5, RockChip SBCs — anywhere Debian-family + ARM64 runs)
- Depends: `libc6`, `libgcc-s1`, `libstdc++6`, runtime essentials
- Recommends: `alpaca-bridge`, `openastro-phd2` (pulled in by default; opt-out with `--no-install-recommends`)
- Suggests: `gpsd` (for USB GPS time sync per §31)

### 34.3 Post-install hooks (handled by .deb's postinst script)

- Creates `openastroara` user + group (system user, no shell)
- Drops `/etc/systemd/system/openastroara-server.service`
- Sets `CAP_SYS_TIME` on the binary: `setcap cap_sys_time+ep /opt/openastroara/OpenAstroAra.Server`
- Installs sudoers drop-in: `openastroara ALL=(root) NOPASSWD: /opt/openastroara/update.sh` (for §33 WILMA push)
- Creates data + log + config dirs at proper permissions
- Generates initial token, writes to `/etc/openastroara/token` (mode 0640, owned by root:openastroara), prints it once to `journalctl -u openastroara-server`
- Enables + starts the service: `systemctl enable --now openastroara-server.service`

ARA Core's .deb does **only** these things. It does **not** touch:
- Wi-Fi or hostapd (per §32.6 — wiki handles this)
- OS install (wiki)
- Equipment driver configuration

### 34.4 Two update paths coexist

| Path | Internet required on Pi | Use case |
|---|---|---|
| **APT (primary)** | Yes | Home / observatory with internet — `sudo apt upgrade` |
| **WILMA push (§33)** | No | Field / offline — binary streamed from app |

Both coexist via `dpkg-divert` (§33.5). User can flip back to APT-managed state by clearing the divert.

### 34.5 Repo layout

```
apt.openastro.net/
├── dists/
│   └── stable/
│       ├── Release  Release.gpg  InRelease
│       └── main/
│           └── binary-arm64/
│               ├── Packages
│               └── Packages.gz
└── pool/
    └── main/
        ├── o/openastroara-server/
        │   └── openastroara-server_0.0.1-ara.1_arm64.deb
        ├── a/alpaca-bridge/
        │   └── alpaca-bridge_X.Y.Z_arm64.deb
        └── o/openastro-phd2/
            └── openastro-phd2_X.Y.Z_arm64.deb
```

GitHub Actions builds the .deb and publishes to the apt repo via `reprepro` (or `aptly`) on every `v0.0.1-*` tag push.

### 34.6 DEPLOY.md becomes lean

DEPLOY.md content:
1. Link to OpenAstro wiki for OS install + Wi-Fi/networking
2. The 4 commands from §34.1
3. Where to find the initial token (`journalctl -u openastroara-server`)
4. How to connect WILMA (already covered in §30)
5. USB drive setup for FITS storage (existing §29.7)
6. USB GPS plug-in (auto-detected per §31, no config needed)

That's it. No tarball install, no manual systemd setup, no manual user creation — all handled by the .deb.

---

## 35. Safety policies (user-configurable per profile)

ARA gives the user policy controls; the AI does not decide. Every safety reaction is set in the profile wizard or Settings → Safety. Sensible defaults pre-filled.

### 35.1 Configurable trigger → action matrix

| Trigger | Available actions | Threshold field |
|---|---|---|
| Cloud sensor unsafe (Alpaca SafetyMonitor or weather station) | Continue / Notify / Pause / Abort + park | — |
| Rain detected | Continue / Notify / Pause / Abort + park *(default abort)* | — |
| Wind speed | Continue / Notify / Pause / Abort + park | km/h or mph |
| Humidity | Continue / Notify / Pause / Abort + park | % |
| Dew point − ambient | Continue / Notify / Pause / Abort + park | °C delta |
| Generic SafetyMonitor `IsSafe = false` | Notify / Pause / Abort + park | — |
| Mount tracking error / lost guide star (>N seconds) | Continue / Pause / Abort + park | seconds |
| WILMA disconnected during a safety event | Wait for reconnect / Auto-abort after N min / Auto-abort immediately | minutes |
| Server unexpected restart | Auto-resume per §28 / Stop and wait for user | — |
| User unreachable (alarm unanswered after delay) | Loop alarm forever / Auto-abort after N min / Sequence continues | minutes |

Each trigger also has:
- **Audible alarm** toggle
- **Alarm delay** (seconds of silent popup before audio starts; default 5s)
- **Alarm sound** (pick from 3-4 bundled tones)
- **Vibration** (mobile only)

### 35.2 Sensible defaults (pre-fill in wizard)

| Event | Default |
|---|---|
| Cloud | Pause |
| Rain | Abort + park |
| Wind > 30 km/h | Pause |
| Humidity > 95% | Notify |
| Dew within 2°C | Notify |
| Generic IsSafe = false | Pause + alert |
| WILMA disconnected + unsafe condition | Auto-abort after 5 min |
| User unreachable | Continue alarm |
| Alarm | On, 5s delay, default tone |

User can override every single default.

### 35.3 User-triggered emergency abort

Persistent **[Emergency Stop]** button in WILMA's main app shell (top-right of status bar). Single tap = immediate abort, no confirmation (the button IS the confirmation). Fires `POST /api/v1/server/emergency-stop`:

Server shutdown sequence:
1. Camera: abort current exposure
2. Guider (PHD2): stop guiding
3. Mount: park (or home if no park position configured)
4. Filter wheel / focuser / rotator: leave in place (no auto-move; user can intervene)
5. Flat panel: turn off (if connected, and configured to do so)
6. Sequence state → `aborted`
7. WebSocket event `sequence.aborted` to all connected clients

### 35.4 SafetyMonitor poll loop (server-side)

- Server polls `IsSafe` on every connected `SafetyMonitor` device every 10 seconds (configurable)
- Subscribes to weather-station Alpaca events if available (push instead of poll)
- On transition to unsafe → triggers profile-configured action
- WebSocket event: `safety.unsafe` with details, fired before action so client can display alarm modal

### 35.5 Audible alarm asset

Bundle small audio in WILMA app: `assets/audio/safety_alarm.wav` (~30s loop, ~50 KB), plus 3-4 alternative tones. Use `audioplayers` Flutter plugin. Volume forced to max during alert. Vibration on mobile via `vibration` plugin.

### 35.6 API

Profile schema gains a `safety_policy` object:
```json
{
  "cloud":     { "action": "pause" },
  "rain":      { "action": "abort" },
  "wind":      { "action": "pause",  "threshold_kph": 30 },
  "humidity":  { "action": "notify", "threshold_pct": 95 },
  "dew_delta": { "action": "notify", "threshold_c": 2 },
  "is_safe_generic": { "action": "pause" },
  "tracking_error":  { "action": "pause", "threshold_seconds": 60 },
  "wilma_disconnect_during_unsafe": { "action": "abort", "timeout_min": 5 },
  "server_restart":  { "action": "auto_resume" },
  "user_unreachable": { "action": "alarm" },
  "alarm": { "enabled": true, "delay_sec": 5, "sound": "default", "vibrate": true }
}
```

Server reads this at session start and behaves accordingly.

---

## 36. Sky imagery + survey management (WILMA)

WILMA owns the sky atlas (per §2 responsibility split). This section specifies the bundled assets, the Survey Manager UI, the Tonight's Sky planetarium, and the universal search.

### 36.1 Bundled assets (ship with the app)

| Bundle | Approximate size | Purpose |
|---|---|---|
| HYG star database (~120k Hipparcos stars) | ~10 MB | Naked-eye and binocular-class stars |
| Tycho-2 brightest subset (~2.5M stars, packed binary) | ~30-50 MB | Smooth rendering at all zooms |
| GAIA DR3 brightest subset (~10M stars, packed binary) | ~80-100 MB | High-density backdrops |
| NGC + IC + Caldwell + Sharpless + Abell + UGC supplementary catalogs | ~30 MB | All DSO targeting |
| MPC comet snapshot (`CometEls.txt`) | ~5 MB | Bundled at app build time |
| Constellation art (Urania's Mirror or modern art) | ~10 MB | Beautiful overlay at low zoom |
| Nebula contour vectors | ~20 MB | Crisp outlines for HII / planetary nebulae |
| Pre-baked DSO target thumbnails (~500 famous targets) | ~150 MB | Aladin-quality previews offline |
| Bundled HiPS tiles: DSS2 color + Mellinger at HEALPix orders 4-6 | ~500 MB | Offline navigation imagery out of the box |
| Solar system: DE440 analytical model (truncated) OR full DE440 | ~50 KB or ~50 MB | Sun/Moon/planets ephemerides for Tonight's Sky |
| Common name resolver index (~50-100k entries) | ~5-10 MB | Universal search offline |
| Bundled audio (safety alarms per §35) | ~200 KB | — |
| **Total bundled app** | **~900 MB-1 GB** | — |

Users see a one-time "large download" warning on cellular install and can proceed (App Store + Play Store both allow this). Desktop installer ships the full bundle directly.

### 36.2 Survey Manager UI

Settings → Sky Imagery → Survey Manager:

```
┌────────────────────────────────────────────────────────────┐
│  Sky Imagery                       412 GB used / 1.2 TB    │
│                                                             │
│  Optical (broadband)                                        │
│  ☑ DSS2 (color)             order 8, 47 GB        [Update] │
│  ☐ DSS2 blue                not downloaded, ~30 GB [Download]│
│  ☐ DSS2 red                 not downloaded, ~30 GB [Download]│
│  ☑ Mellinger (color)        order 6, 4 GB          [Update]│
│  ☐ SDSS9                    not downloaded, ~120 GB[Download]│
│  ☐ PanSTARRS DR1 color      not downloaded, ~280 GB[Download]│
│  ☐ DECaPS DR2               not downloaded, ~150 GB[Download]│
│  ☐ DESI Legacy DR10         not downloaded, ~290 GB[Download]│
│                                                             │
│  Hα                                                         │
│  ☑ Finkbeiner Hα            order 7, 8 GB          [Update]│
│  ☐ VTSS Hα                  not downloaded, ~6 GB  [Download]│
│                                                             │
│  Infrared                                                   │
│  ☑ 2MASS (J+H+K)            order 8, 38 GB         [Update]│
│  ☐ GLIMPSE360               not downloaded, ~52 GB [Download]│
│  ☐ Spitzer                  not downloaded, ~58 GB [Download]│
│  ☐ allWISE                  not downloaded, ~64 GB [Download]│
│  ☐ IRIS                     not downloaded, ~7 GB  [Download]│
│  ☐ AKARI FIS                not downloaded, ~14 GB [Download]│
│                                                             │
│  Ultraviolet                                                │
│  ☐ GALEX GR6/7              not downloaded, ~16 GB [Download]│
│                                                             │
│  X-ray                                                      │
│  ☐ eROSITA DR1              not downloaded, ~8 GB  [Download]│
│  ☐ XMM-Newton (PN)          not downloaded, ~7 GB  [Download]│
│  ☐ Chandra                  not downloaded, ~5 GB  [Download]│
│                                                             │
│  Gamma-ray                                                  │
│  ☐ Fermi                    not downloaded, ~3 GB  [Download]│
│                                                             │
│  [Download All]  [Pick a Preset ▼]  [Clear All]             │
└────────────────────────────────────────────────────────────┘
```

### 36.3 Per-survey controls

- Choose HEALPix resolution depth (e.g., order 4 = ~6 GB DSS2 color; order 8 = ~47 GB)
- Download / Pause / Resume / Cancel / Remove
- Verify integrity (SHA-256 against CDS manifest)
- Storage location (default app data dir; user can point at external drive on desktop)

### 36.4 Presets

- **"Optical only"** — DSS2 color + Mellinger + Finkbeiner Hα (~60 GB)
- **"All-wavelength essentials"** — one survey per band (~150 GB)
- **"Everything full resolution"** — ~2 TB. Confirmation gate. For real users with real storage.

### 36.5 Politeness considerations (CDS bandwidth)

- Parallel tile fetcher with per-CDS-host rate limiting (default 8 parallel connections, user-configurable)
- README + Settings explainer notes: CDS infrastructure is shared by astronomers worldwide. Download "Everything full res" only when you actually need it, preferably overnight.
- Implement HTTP `If-Modified-Since` so updates only fetch changed tiles

### 36.6 Survey-serving to Aladin Lite

Once a survey is downloaded:
- Tiles stored at `<wilma data>/hips/<survey-id>/Norder<n>/Dir<m>/Npix<k>.jpg` (standard HiPS layout)
- WILMA runs an embedded local HTTP server (Dart `shelf` package) on a random localhost port, serving from the hips dir
- Aladin Lite's `hipsUrl` config points at that local server when WILMA is offline OR when the user prefers local
- Online + survey not downloaded → falls back to CDS

### 36.7 Tonight's Sky (planetarium mode)

The Sky Atlas tab has a sub-mode toggle: **[Catalog View]** ↔ **[Tonight's Sky]**.

**Tonight's Sky implementation:**
- Aladin Lite driven by WILMA — view centered programmatically on current zenith RA/Dec, stereographic projection
- WILMA computes (Dart, using inherited Astrometry library):
  - Current zenith RA/Dec from profile lat/long + UTC
  - Alt/az for every bundled catalog object
  - Horizon great-circle in equatorial coordinates
- WILMA pushes overlays into Aladin via JS bridge:
  - **Horizon polyline** as a catalog
  - **Cardinal direction markers** (N/E/S/W) at the horizon edge
  - **Below-horizon shading** (darken half-sky via custom overlay)
  - **Solar system bodies** — Sun, Moon (with phase glyph), 8 planets, computed from DE440, fed as a custom Aladin catalog updated every 60s
  - **Comets** — visible comets from bundled MPC snapshot, with motion trails for next N days
  - **Currently-tracked target** — highlighted if user has one picked
- Time slider — scrub forward/backward. Each frame recomputes. "Now" button snaps to current real time. Auto-advance every 60s by default.
- Object filtering — catalog browser shows only objects above the horizon (or user-configurable altitude limit) at the current time. "Best transit tonight" sort option.

### 36.8 Universal search

Search bar at the top of the Sky Atlas tab:

- **Online** (WILMA has internet): query Aladin's Simbad integration. Type "wolf" → resolves to Wolf 359, Wolf-Rayet stars, candidate matches. Type "M31" / "NGC 6188" / "Andromeda Galaxy" / coordinates → resolves.
- **Offline**: fall back to bundled name resolver index (HYG common names + NGC/IC/M/HD/HIP/Tycho-2 designations + Bayer/Flamsteed + bundled comets). ~5-10 MB index, ~50-100k entries.
- **Coordinate parsing**: accept RA/Dec strings in multiple formats (HH:MM:SS / decimal degrees / mixed).
- **Comets**: searchable by designation (`C/2023 A3`) or common name (`Tsuchinshan-ATLAS`).
- **Asteroids** (v0.0.1): targeted lookup only (type "Ceres", "(1) Ceres", "433 Eros" → WILMA fetches that single object from MPC on demand). Bulk asteroid catalog deferred to v0.1.0.

### 36.9 Comet support

- Bundled `CometEls.txt` snapshot at app build time (~5 MB, ~5,000 comets)
- WILMA computes positions from Keplerian elements (a, e, i, Ω, ω, M, T) in Dart — ~500 lines, well-documented math
- Fed to Aladin as a catalog with custom marker (comet glyph + name + magnitude)
- **"Refresh comet data"** button (Settings → Sky Imagery) pulls latest `CometEls.txt` from MPC (requires WILMA internet, ~5 MB download, seconds)
- **Motion trail** option — shows comet's path over next 7/30/90 days as polyline overlay

### 36.10 Recommended catalog depth based on telescope (wizard hint per user's spec)

In the wizard's Telescope screen, after the user enters focal length:
- Short focal length (< 500mm): recommend "Optical essentials" preset (~60 GB)
- Medium (500-1500mm): recommend "Optical + Infrared essentials" (~110 GB)
- Long (> 1500mm): recommend "All-wavelength essentials" (~150 GB) + deeper PanSTARRS or DECaPS if storage allows
- Recommendation is a *suggestion*, not enforced. User can still pick anything.

### 36.11 Aladin Lite license requirements

- Aladin Lite v3 is **GPL v3**; ARA is MPL 2.0
- These mix because the WebView is a separate process boundary — Aladin JS and Dart code communicate via `postMessage`, not statically linked. GPL FAQ explicitly permits this pattern.
- **Required by Aladin license**: keep Aladin logo + link visible bottom-right of the view. Don't strip the attribution.
- Credit in NOTICE.md: "Sky Atlas rendering powered by [Aladin Lite](https://aladin.cds.unistra.fr/) (CDS, Strasbourg) under GPL v3."

---

## 37. Profile setup wizard

The wizard is **mandatory on first launch** (after server connect + profile creation entry from §30). It walks the user through every essential configuration with sensible defaults and per-screen [Skip — use defaults]. Each screen also has [< Back] and [Next >]. Progress bar at top: "Step X of N." User can [Save & Exit Wizard] at any point — profile saves with what's been configured, defaults for the rest.

### 37.1 Stage 1 — Profile basics

**Screen 1 — Profile name + location**

- Profile name (required)
- Site latitude / longitude / altitude (optional)
  - "Use device GPS" button (WILMA mobile) — `geolocator` Flutter plugin
  - Manual entry alternative
  - "Skip — set later" fallback
- Site name (optional, e.g., "Backyard Texas", "Bortle 4 site")
- Timezone (auto-detect from location, or pick from list)

### 37.2 Stage 2 — Equipment discovery

**Screen 2 — Connect to AlpacaBridge**

- Field: AlpacaBridge address (default: auto-discover via Alpaca's broadcast UDP on port 32227)
- "Test Connection" button — server pings AlpacaBridge, shows results
- (v0.0.1: protocol is Alpaca only; INDI/INDIGO listed as "future" placeholder)

**Screen 3 — Discover + assign equipment**

Server enumerates Alpaca devices, groups by type. User assigns each device-type slot (or leaves "— None"):
- Camera
- Filter Wheel
- Focuser
- Mount (Telescope)
- Rotator
- Dome
- Observing Conditions (weather)
- Switch
- Safety Monitor
- Flat Panel
- Guider (PHD2 — server reaches out to PHD2's JSON-RPC, not Alpaca)

### 37.3 Stage 3 — Per-device setup (one screen per connected slot; skipped if "— None")

**Screen 4 — Telescope**

- Telescope name (free text, e.g., "ES ED102")
- Focal length (mm) — required
- Aperture (mm) — required
- Focal ratio — auto-computed but editable
- **Aladin survey recommendation** appears here based on focal length (per §36.10) — user can [Download recommended] or [Skip — configure in Sky Imagery later]

**Screen 5 — Camera**

- Cooling target temperature (°C, default −10° or "ambient minus 30°")
- Cooler ramp rate (°C/min, default 1°C/min)
- Default gain
- Default offset
- Default bin
- Pixel size (mm) — auto-filled from Alpaca, editable
- Image scale computed and displayed: "1.49 arcsec/pixel — wide-field DSO" (or similar)

**Screen 6 — Filter Wheel**

- For each slot detected by Alpaca:
  - Name (L / R / G / B / Hα / OIII / SII / Clear / etc.)
  - Type (broadband / narrowband / clear / luminance)
  - Wavelength (nm) — optional metadata
  - Focus offset (steps) — left blank; populated automatically by first autofocus run per §28.5

**Screen 7 — Focuser**

- Step size (microns/step) — pulled from Alpaca if reported
- Backlash compensation: in / out steps
- Temperature compensation toggle + slope (steps/°C, defaults to 0 = disabled)
- Max travel — pulled from Alpaca

**Screen 8 — Mount**

- Mount name — auto-pulled from Alpaca driver
- Slew rate (deg/sec)
- Park position: [Sync to current pointing] / [Define manually]
- Meridian flip behavior: Auto / Prompt / Never
- **Settle time after slew** — auto-pulled from Alpaca driver's `SlewSettleTime` property (per user's spec), editable

**Screen 9 — Rotator** (if connected)

- Mechanical limits (min/max angle)
- Angle step size
- Reverse direction toggle

**Screen 10 — Guider (PHD2)**

- Host:port (default `localhost:4400`)
- Dither pixels (default 5 px)
- Settle threshold (default 1.5 px for 10s)
- Calibration cadence: [Each session] / [Once, then reuse] / [Never recalibrate]

### 37.4 Stage 4 — Imaging tools

**Screen 11 — Plate solving (ASTAP)**

- ASTAP binary path — auto-detect per OS (Linux: `which astap`; macOS: `/Applications/ASTAP.app/...`; Windows: `%PROGRAMFILES%\astap\astap.exe`), editable
- Star database path — browse, recommend external/USB drive
- Search radius (deg, default 30)
- Downsample factor (default 2)
- Test button: "Solve a test image" — feeds a bundled known image, verifies ASTAP works

**Screen 12 — Autofocus**

- Exposure time (default 5s)
- Step size (microns)
- Max retries (default 3)
- "Auto-discover filter offsets" toggle (default on — first AF run per filter populates filter wheel offset)

**Screen 13 — File saving + naming**

- Save directory (browse — USB drive recommended per §29; shows free space warning if SD card)
- File format: [FITS] / [XISF]
- Compression on/off (default on)
- Filename template — default per user's spec:
  ```
  $$DATEMINUS12$$\$$IMAGETYPE$$\$$DATETIME$$_$$FILTER$$_$$SENSORTEMP$$_$$EXPOSURETIME$$s_$$FRAMENR$$
  ```
- Template variable reference shown inline

**Screen 14 — Imaging defaults**

- Default exposure (s)
- Default gain / offset
- Default frame type: [Light] / [Dark] / [Bias] / [Flat]
- Cooling target inherited from camera screen

### 37.5 Stage 5 — Safety + site

**Screen 15 — Safety policies** (per §35)

Compact wizard layout:
```
When weather goes bad:
  Clouds: [Pause ▼]
  Wind:   [Pause ▼] above [30] km/h
  Rain:   [Abort + Park ▼]

When something's wrong and I'm not here:
  WILMA offline:        [Auto-abort after  5 ] min
  Alarm unanswered:     [Continue alarm ▼]

Alarm:
  Sound: [Default ▼]  [▶ test]
  Vibrate: ☑
```

Full editor is available later in Settings → Safety.

**Screen 16 — Site preferences**

- Hard min altitude (default 5°)
- Soft warning altitude (default 30°)
- Astronomical twilight margins (default: start at end-of-evening-astro, stop at start-of-morning-astro)
- Max sequence runtime (default: no limit)

### 37.6 Stage 6 — Sky Imagery

**Screen 17 — Survey downloads** (per §36)

- Shows bundled imagery state ("DSS2 color + Mellinger pre-loaded — ~500 MB")
- Recommended additional surveys based on focal length (echo of Screen 4 recommendation)
- Quick preset buttons + "Open Survey Manager for full control"
- Skip to defer all survey downloads to Settings → Sky Imagery

### 37.7 Stage 7 — Done

**Screen 18 — Review + Save**

- Single-page summary of every setting (per stage)
- [Make Changes — jump to any screen]
- [Save Profile]
- After save: navigate to main app shell

### 37.8 Wizard behavior rules

- Every screen has [Skip — use defaults] and [< Back] [Next >]
- User can [Save & Exit] at any point — profile saves partial state, defaults fill the rest
- Skipped screens are flagged in the profile: "Default — please review in Settings"
- Wizard can be re-run from Settings → Profile → "Run Wizard Again" (useful when changing rigs)
- Each "Use device GPS" / "Pull from driver" / "Auto-detect" interaction is non-blocking — wizard never hangs waiting on equipment

---

## 38. Sequence file format + NINA `.json` import

ARA adopts NINA's sequence JSON schema verbatim as the canonical storage format, adds a `schemaVersion` field for forward compatibility, exposes a validated OpenAPI schema, and imports existing NINA `.json` sequence files with equipment-remap + unsupported-instruction handling.

### 38.1 Canonical schema

- Top-level field: `"schemaVersion": "openastroara-sequence-v1"`
- All NINA container types preserved verbatim: `SequenceRootContainer`, `DeepSkyObjectContainer`, `SequentialContainer`, `ParallelContainer`, `ConditionalContainer`, etc.
- All NINA instruction types preserved verbatim where they have ARA equivalents (capture, slew, switch filter, run autofocus, plate solve, center, wait, etc.)
- Trigger system (BeforeEach, AfterEach) and condition system (ConditionalRunner, LoopCondition) preserved
- Schema documented in `OpenAstroAra.Server/openapi.yaml` (or `OpenAstroAra.Server/schemas/sequence.yaml` if pulled out for size). Used for:
  - Server-side validation on `POST`/`PUT` upload
  - IntelliSense in editors that consume OpenAPI
  - Swagger UI rendering at `/api/v1/docs`

### 38.2 Storage layout

**Pi side** (`/var/lib/openastroara/sequences/`):
```
sequences/
├── library/           user's saved/pushed sequences (canonical, served via API)
├── imported/          NINA imports, source preserved per dated subfolder
│   └── from-nina-YYYY-MM-DD/<original-name>.json
├── templates/         starter templates shipped with the .deb
│   ├── lrgb-dso.json
│   ├── narrowband-shoo.json
│   ├── lunar.json
│   └── planetary.json
└── active/            checkpoint state of currently-running sequence (per §28)
    └── current.json
```

**WILMA side** (app data `/sequences/`):
```
sequences/
├── drafts/            locally-edited sequences not yet pushed to Pi
└── synced/            read-only cache of last-fetched Pi library
```

When connected, WILMA periodically refreshes `synced/` from `GET /api/v1/sequences`. Drafts live only locally until user explicitly pushes via "Save to Server" or "Run on Pi" buttons.

### 38.3 Endpoints

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/v1/sequences` | List saved sequences on Pi (id, name, target, last-modified, last-run) |
| `GET` | `/api/v1/sequences/{id}` | Fetch one sequence (full JSON) |
| `POST` | `/api/v1/sequences` | Upload new sequence (validates, assigns id, saves to library/) |
| `PUT` | `/api/v1/sequences/{id}` | Update existing |
| `DELETE` | `/api/v1/sequences/{id}` | Remove |
| `POST` | `/api/v1/sequences/import` | Multipart upload of a NINA `.json` file → returns parsed sequence + warnings array |
| `POST` | `/api/v1/sequences/{id}/start` | Start running (kicks off the sequence executor) |
| `GET` | `/api/v1/sequences/templates` | List bundled starter templates |
| `POST` | `/api/v1/sequences/templates/{name}/instantiate` | Copy a template into the user's library, optionally fill in target via request body |

### 38.4 NINA import flow

1. WILMA: Sequencer tab → [Import from NINA] → file picker → user selects `.json`
2. WILMA uploads via multipart to `POST /api/v1/sequences/import`
3. Server processing:
   - Parse JSON
   - Detect NINA format (no `schemaVersion`, has NINA container/instruction type names)
   - Add `"schemaVersion": "openastroara-sequence-v1"`
   - **Equipment-ID remapping**: any `CameraId`, `MountId`, `FilterId`, `FocuserId`, `RotatorId`, `DomeId`, `GuiderId` etc. referencing NINA's ASCOM ProgIDs are:
     - Fuzzy-matched to user's currently-connected Alpaca devices by name where possible
     - Otherwise set to `null` and added to the `warnings` array
   - **Unsupported instruction types** (MGEN-specific, plugin instructions, vendor-specific commands not exposed via Alpaca) are wrapped in a `SkippedInstruction { reason: "...", originalPayload: {...} }` placeholder
   - Save under `imported/from-nina-YYYY-MM-DD/<original-filename>.json`
   - Return the parsed sequence + `warnings: [...]` array shape:
     ```json
     {
       "sequence": { ... },
       "warnings": [
         { "type": "equipment_unmatched", "instruction": "...", "field": "CameraId", "originalValue": "ASCOM.QHYCCD.Camera" },
         { "type": "instruction_skipped", "originalType": "MGENGuiderInstruction", "reason": "MGEN not supported in ARA" },
         { "type": "filter_unknown", "filterName": "OIII6nm" }
       ]
     }
     ```
4. WILMA: displays sequence in editor with a warnings banner — *"3 issues need attention: 2 cameras need reassignment, 1 instruction skipped (MGEN)"* — user clicks through each warning to resolve in the editor
5. Once user resolves all warnings (or accepts them), sequence is functional and can be saved to library + run

### 38.5 Validation rules (server-side on `POST` / `PUT`)

- `schemaVersion` recognized (v1 currently; future schemas added incrementally)
- All referenced equipment IDs resolve to known Alpaca devices in the active profile, OR explicitly `null` with user acknowledgment
- All referenced filters exist in active profile's filter wheel slot configuration
- At least one capturable instruction reachable from root
- Time-based conditions reference valid astronomical events (`dusk`, `dawn`, `astronomical_twilight_start`, etc.)
- No infinite loops (a `LoopContainer` must have a terminating condition that evaluates to false reachably)
- Equipment slot uses match capability — e.g., `RunAutofocus` requires a focuser slot filled

Validation failures return 422 with detailed errors per failing instruction path.

### 38.6 Template variable system

Inherits NINA's syntax:

**Filename templates** (per §37 wizard, screen 13):
- `$$TARGETNAME$$`, `$$FILTER$$`, `$$EXPOSURETIME$$`, `$$DATE$$`, `$$DATETIME$$`, `$$DATEMINUS12$$`, `$$SENSORTEMP$$`, `$$FRAMENR$$`, `$$IMAGETYPE$$`, `$$BINNING$$`, `$$GAIN$$`, `$$OFFSET$$`

**Sequence template variables** (for `templates/` files that get instantiated against a user-picked target):
- `{{target_name}}`, `{{target_ra}}`, `{{target_dec}}`, `{{target_rotation}}`
- `{{integration_minutes}}`, `{{frames_per_filter}}`
- `{{filter_set}}` (a named filter combination from the profile, e.g., "LRGB" or "SHO")
- Substituted server-side at `POST /api/v1/sequences/templates/{name}/instantiate`

### 38.7 Bundled starter templates (v0.0.1)

Ship 4 templates with the `openastroara-server` .deb at `/opt/openastroara/templates/`:

| Template | Use case |
|---|---|
| `lrgb-dso.json` | LRGB on a DSO — luminance + RGB filters, dither cadence, auto-focus on temp change |
| `narrowband-shoo.json` | SHO narrowband — Ha, OIII, SII filters with longer exposures |
| `lunar.json` | Short-exposure lunar capture, no guiding required, high frame count |
| `planetary.json` | High-frame-rate planetary with ROI cropping (small subframe), no guiding |

Each template uses placeholder target slots. User picks target via WILMA's "Apply Template" → "Pick Target" flow, which calls `POST /api/v1/sequences/templates/{name}/instantiate` with the target details.

### 38.8 Schema evolution policy

- v0.0.1 ships `openastroara-sequence-v1` (NINA-compatible)
- Backwards-compatible additions within v1 (new optional fields) are allowed; ARA bumps a `protocol_minor` in `/api/v1/server/info` and clients respect missing-field defaults
- Breaking changes go to `openastroara-sequence-v2` — server reads both, writes current; user-managed migration only if they want new v2-only features
- Schema version is independent from API version and from app version

### 38.9 WILMA sequence editor essentials (Phase 12)

- Tree view of: Sequence → Targets → Containers → Instructions
- Drag-drop reordering within a level
- Per-instruction editor pane on the right when selected
- Validation runs live as user edits; warnings shown inline
- "Push to Pi" button (validates + uploads + tracks server id)
- "Run Now" button (push + start; shortcut)
- "Apply Template" entry point in the sequence picker
- "Import from NINA" entry point in the same picker
- Local draft auto-save every 30 seconds to WILMA app data
- Conflict resolution: if WILMA's draft diverges from Pi's saved version, prompt user [Keep Local] / [Keep Pi] / [View Diff]
