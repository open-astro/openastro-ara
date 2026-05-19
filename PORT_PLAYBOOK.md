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

Two distinct storage domains: **Pi side** (FITS frames + session state + profiles + sequences + calibration library + logs, server-managed, **on a mandatory USB drive**) and **WILMA side** (bundled catalogs + downloaded sky imagery surveys + cached tiles + draft sequences, client-managed).

**Pi-side storage is on a USB drive — REQUIRED, not optional.** The Pi's SD card holds only the OS, the `openastroara-server` binary, the systemd unit, and `/etc/openastroara/`. All ARA persistent data (frames, DB, profiles, sequences, logs) lives on an external USB drive that the user provides and configures during first-run setup. Reasons:

- SD cards have limited write endurance (typically 1,000-10,000 P/E cycles on consumer cards). A typical astrophotography night writes 50-100+ GB of FITS data; on an SD card that's months-to-a-year of life before failure.
- SD card failure during a session = lost imaging data plus a bricked Pi.
- USB SSDs (or even quality USB 3.0 sticks) handle sustained writes orders of magnitude better.
- DEPLOY.md recommends USB 3.0 SSDs or quality USB 3.0 sticks from reputable brands. Strongly discourages "free promotional" USB sticks of unknown provenance.

The server refuses to enter "ready" state without a configured USB drive (§29.1.1).

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

Server stores ALL persistent data on the configured USB drive at `/media/openastroara/`. Layout:

```
/media/openastroara/                            (mandatory USB drive)
├── captures/<session-id>/<target>/<filter>/    FITS frames
│   └── <frame>.fits + .thumb.jpg + .preview.jpg
├── calibration/                                Calibration library (§39.9)
│   ├── darks/<camera-id>/<gain>_<temp>_<exp>/
│   ├── bias/<camera-id>/<gain>_<offset>/
│   └── flats/<camera-id>/<filter>_<rot>_<focus>/
├── db/openastroara.db                          SQLite session + frames + profiles + sequences + faults DB
├── profiles/                                   Profile JSON files (canonical)
├── sequences/                                  Sequence library (§38.2)
│   ├── library/
│   ├── imported/
│   └── active/
├── templates/                                  User-customized templates
├── logs/                                       Serilog rotating output, capped 14 days
└── .araback/                                   Auto-generated backup zips (per §43)
```

The Pi's SD card holds only: OS, `openastroara-server` binary (under `/opt/openastroara/`), systemd unit, `/etc/openastroara/{token, storage.conf}`, and a tiny placeholder `/var/lib/openastroara/` (used only briefly during first-run before USB is configured).

### 29.1.1 USB drive configuration (first-run)

After `sudo apt install openastroara-server` completes:

1. Server starts in **`needs_storage`** mode. `GET /api/v1/server/info` returns `{ "status": "needs_storage", "available_usb_drives": [{...}, {...}] }`.
2. `GET /api/v1/server/storage/candidates` enumerates mounted USB block devices (via `lsblk -J --output NAME,UUID,SIZE,MOUNTPOINT,LABEL`) excluding the OS root + boot partitions.
3. WILMA prompts user: *"Select a USB drive for ARA data:"* with size + label + free space per option.
4. User picks → WILMA calls `POST /api/v1/server/storage/configure { "uuid": "..." }`.
5. Server:
   - Writes UUID to `/etc/openastroara/storage.conf` (permanent pin)
   - Creates directory structure on the USB drive
   - Initializes the SQLite DB
   - If USB already has existing ARA data (re-using a drive from another Pi): asks WILMA `"Found existing ARA data on this drive (12 profiles, 47 sessions). Use this data?"` → either continue with existing data or initialize fresh
   - Transitions to **`ready`** mode

The configured UUID is pinned. If the user replaces the USB drive, they go back through the configuration flow.

### 29.1.2 USB unplug detection

Server watches the USB mount point (filesystem watcher on the parent dir). On unmount/disconnect:

- Active sequence pauses immediately at next safe point (no new exposures)
- Active capture: in-flight frame's bytes go to a tmpfs buffer, flushed once the drive returns OR discarded after 60s
- WebSocket event: `{ "type": "storage.unmounted", "severity": "critical" }`
- WILMA shows full-screen alert: *"USB drive disconnected. Sequence paused. Reconnect the drive to resume."*
- On remount (same UUID): server detects, resumes sequence; users sees "Storage reconnected" toast

If a different UUID mounts: server ignores it (not the configured drive).

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

- Current USB drive: label, total / free space, throughput meter, "Healthy" indicator
- **Configured UUID** display (so user knows which drive is "the one")
- "Switch storage drive" → re-runs the §29.1.1 configuration flow (warns about migrating data)
- "Eject safely" → unmounts the drive cleanly so user can swap (sequence must not be running)
- Link to DEPLOY.md USB hardware recommendations + backup instructions
- Auto-prune policy editor (per §29.5): never / monthly / weekly with rating-based rules

### 29.7 DEPLOY.md content (USB drive setup)

```bash
# 1. Plug in your USB drive (USB 3.0 SSD recommended; quality USB 3.0 stick acceptable).
#    DO NOT use a cheap promotional USB stick — they fail.

# 2. Find the drive's UUID:
lsblk -f
# Look for your drive (e.g., /dev/sda1) and note its UUID.

# 3. (Optional, recommended) Format the drive as ext4 for best Linux performance
#    and reliability. This WIPES the drive — back up first.
sudo mkfs.ext4 -L openastroara /dev/sda1

# 4. Create the mount point + persistent mount:
sudo mkdir -p /media/openastroara
echo 'UUID=<your-uuid> /media/openastroara ext4 defaults,nofail,x-systemd.device-timeout=10 0 0' \
  | sudo tee -a /etc/fstab
sudo systemctl daemon-reload
sudo mount -a

# 5. Set ownership so the openastroara user can write:
sudo chown -R openastroara:openastroara /media/openastroara

# 6. Tell ARA to use this drive (via WILMA's first-run storage config —
#    or manually edit /etc/openastroara/storage.conf):
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

---

## 39. Calibration frames + session-metadata-driven auto-flats

ARA preserves NINA's separate `Light`/`Dark`/`Bias`/`Flat` instruction types and adds a workflow unique to ARA: **automatically generate a flat (or dark) sequence that matches the exact equipment state of a past imaging session.** Filter, focus position, rotator angle (CAA), gain, offset, cooler target — all replayed from the session's recorded metadata.

### 39.1 Frame types and instructions

Sequence instruction types (all inherited from NINA verbatim per §38):

| Instruction | Purpose |
|---|---|
| `TakeManyExposures` (light frames) | Standard imaging captures during a session |
| `TakeDarkExposures` | Dark frames at specified exposure + gain + temp |
| `TakeBiasExposures` | Bias frames (shortest possible exposure, shutter closed) |
| `TakeFlatExposures` | Flat frames at user-specified exposure |
| `FlatPanelFlats` | Coordinates with flat panel: turn on, set brightness, capture flats per filter, turn off |
| `DarkLibraryInstruction` | Builds a dark library across a matrix of (exposure × gain × temp) tuples |
| `SkyFlats` | Captures sky flats during twilight using auto-exposure to target ADU |

### 39.2 Calibration philosophy: capture-only

ARA Core captures calibration frames and writes them to disk with rich FITS headers. ARA Core does **not** apply calibration during capture (no live dark subtraction, no live flat division). Calibration is the responsibility of the user's post-processing tools (PixInsight, Siril, GraXpert, AstroPixelProcessor, etc.) which read the FITS headers to match calibration frames to lights.

Rationale: applying calibration at capture-time doubles disk usage, locks the user into a calibration strategy chosen at capture-time, slows the imaging cadence, and removes the user's ability to re-calibrate with improved master frames later. NINA does not do this either; ARA matches that behavior.

### 39.3 Session metadata (the foundation for matching flats)

Every captured frame's FITS header includes the complete equipment state at capture time:

| FITS keyword | Meaning |
|---|---|
| `DATE-OBS` | UTC capture start |
| `INSTRUME` | Camera name (Alpaca-reported) |
| `FILTER` | Filter slot name |
| `FOCUSPOS` | Focuser absolute position |
| `ROTANG` | Rotator angle (degrees) — the "CAA" |
| `SET-TEMP` | Cooler target temp (°C) |
| `CCD-TEMP` | Achieved sensor temp (°C) |
| `AMBTEMP` | Ambient temp from weather (if connected) |
| `HUMIDITY` | Ambient humidity from weather (if connected) |
| `EXPTIME` | Exposure duration (s) |
| `GAIN` | Sensor gain |
| `OFFSET` | Sensor offset |
| `XBINNING` / `YBINNING` | Binning |
| `OBJECT` | Target name (from sequence) |
| `OBJCTRA` / `OBJCTDEC` | Target RA / Dec |
| `OBJCTROT` | Target rotation angle |
| `IMAGETYP` | `LIGHT` / `DARK` / `BIAS` / `FLAT` |
| `SESSIONID` | UUID linking to Pi's session database row |
| `FRAMENR` | Frame number within the session |
| `SITELAT` / `SITELON` / `SITEALT` | Observatory location |

Pi-side session database (SQLite) mirrors this data in queryable form for cross-session analytics:

```sql
CREATE TABLE sessions (
  id TEXT PRIMARY KEY,
  profile_id TEXT,
  started_at TIMESTAMP,
  ended_at TIMESTAMP,
  target_name TEXT,
  target_ra REAL,
  target_dec REAL,
  target_rotation REAL,
  rotator_angle REAL,
  total_frames INT,
  notes TEXT
);

CREATE TABLE frames (
  id TEXT PRIMARY KEY,
  session_id TEXT REFERENCES sessions(id),
  fits_path TEXT,
  captured_at TIMESTAMP,
  image_type TEXT,
  filter TEXT,
  focus_pos INT,
  rotator_angle REAL,
  set_temp REAL,
  ccd_temp REAL,
  ambient_temp REAL,
  exposure_seconds REAL,
  gain INT,
  offset INT,
  binning TEXT
);
```

### 39.4 Session library API

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/v1/sessions` | List sessions (id, target, date, frame count, filters used) |
| `GET` | `/api/v1/sessions/{id}` | Full session metadata + summary stats |
| `GET` | `/api/v1/sessions/{id}/frames` | List frames within the session |
| `GET` | `/api/v1/sessions/{id}/calibration-suggestions` | Server analyzes session and returns a suggested calibration sequence (flats per filter, darks for the session's exposures, bias) |
| `POST` | `/api/v1/sessions/{id}/generate-flat-sequence` | Server constructs a `TakeFlatExposures` sequence matching this session's equipment state, returns the sequence JSON (user can review + push + run) |
| `POST` | `/api/v1/sessions/{id}/generate-dark-sequence` | Same, for darks |

### 39.5 "Capture matching flats from session" workflow (the killer feature)

User flow in WILMA:

1. Open the **Image Library** tab → list of past sessions, grouped by target + date
2. Pick a session → details page shows frame counts per filter, total integration, equipment used
3. Click **[Capture Matching Flats]** button
4. Server analyzes the session and returns a suggested flat sequence. **Because the server recorded the per-filter equipment state during the session, it physically commands the equipment back to those exact positions before capturing flats** — no manual reconfiguration required:

   ```
   For each filter used (L, R, G, B, Hα):
     1. Filter wheel: rotate to the same slot used during the session
     2. Focuser: move to the focus position recorded for THAT filter in
        the session (focus often differs per filter — server pulls the
        right value from the session's per-frame metadata)
     3. Rotator (CAA): slew to the same angle used during the session,
        so dust mote shadows align with the lights
     4. Cooler: set target to the session's SET-TEMP and wait for
        stabilization (subject to ambient limits — see §39.6)
     5. Camera: configure same gain, offset, binning
     6. Flat panel: turn on, auto-adjust brightness to target ADU
        (~30000 for 16-bit), capture 30 flat frames
     7. Flat panel: turn off
   Mount can stay parked or wherever — flats don't depend on sky position
   ```

   The result: a flat library that calibrates the original lights exactly, because every optical-train variable that affects vignetting and dust shadows (filter, rotation, focus distance) is identical between lights and flats.
5. WILMA displays the suggested sequence for review with any **warnings**:
   - "Session sensor temp was −10°C; current cooler achievable max is −3°C — flats will be captured at −3°C instead (post-processing tools may show minor calibration noise)"
   - "Rotator angle differs from current position by 47° — will rotate before flat capture"
   - "Filter wheel position for 'OIII' was slot 3; current slot 3 is 'SII' — please verify filter wheel hasn't been reconfigured since the session"
6. User confirms (or adjusts) → sequence pushed to Pi → runs
7. Captured flats are tagged with `SESSIONID` matching the original lights, plus a sidecar JSON noting "calibration-for-session: <id>"

### 39.6 Temp mismatch handling

The cooler-temp problem the user flagged is real: a session at −10°C in winter (ambient 5°C, delta 15°C) cannot be exactly replicated in summer (ambient 30°C, cooler max delta ~30°C → achievable target ~0°C).

Strategy:

- Server queries the camera's reported cooler capability (typically max delta below ambient, ~30-40°C for most CMOS cameras)
- Computes whether session's `SET-TEMP` is achievable at current ambient
- If not: warn user, offer:
  - **[Use closest achievable temp]** (recommended) — flats at the new temp; FITS header records both target and achieved
  - **[Wait until conditions allow]** — sequence scheduled for early morning when ambient is coolest, or deferred
  - **[Cancel — capture flats during the next imaging session instead]**
- The achieved temp is recorded in `CCD-TEMP` regardless; post-processing tools will handle the mismatch (they may warn the user, but most modern stacking pipelines handle small temp deltas gracefully)

### 39.7 Auto-flats at dusk (during a session)

Inherits NINA's `FlatPanelFlats` instruction. Typical sequence structure:

```
Container: Tonight's Session
  Instructions:
    - WaitForTimeOf(astronomical_dusk)
    - Container: Lights
        - TakeManyExposures (× target1, all filters)
    - WaitForTimeOf(astronomical_dawn)
    - Container: Flats
        - FlatPanelFlats (× each filter used tonight, 30 frames each)
```

Server-side `FlatPanelFlats` reads which filters were used in the session (live), automatically generates the flat captures for each.

### 39.8 Dark library auto-generation

Inherits NINA's `DarkLibraryInstruction`. User defines a matrix:

```json
{
  "type": "DarkLibraryInstruction",
  "exposures": [30, 60, 120, 300, 600],
  "gains": [0, 100, 200],
  "temps": [-10, -5, 0],
  "framesPerCombination": 50
}
```

Server captures `5 × 3 × 3 × 50 = 2,250` dark frames over many hours (typically a moonless or cloudy night when imaging is impossible anyway). Stores at `/var/lib/openastroara/calibration/darks/`.

### 39.9 Calibration library storage on Pi

```
/var/lib/openastroara/calibration/
├── darks/
│   └── <camera-id>/
│       └── exp_<seconds>_gain_<n>_temp_<c>/
│           └── frame_001.fits
├── bias/
│   └── <camera-id>/
│       └── gain_<n>_offset_<n>/
│           └── frame_001.fits
└── flats/
    └── <camera-id>/
        └── filter_<name>_rot_<angle>_focus_<pos>/
            └── frame_001.fits
```

Filenames + sidecar JSONs make matching by metadata easy for both ARA's session-matching workflow and external post-processing tools.

### 39.10 Calibration library browsing in WILMA

Settings → Calibration Library:

- Tab per frame type (Darks / Bias / Flats)
- Browseable table: filter / exposure / gain / temp / rotator / focus / count / date
- "Verify integrity" button (read FITS, check for corruption)
- "Match to session" — reverse direction: pick a session, see which calibration frames in the library match it
- "Export" — download a tarball of selected frames to WILMA for use in PixInsight etc.
- "Delete" — remove old/superseded frames
- Storage usage indicator + auto-prune option (e.g., "keep latest 30 days, prune older if >50 GB")

### 39.11 Comparison to ASIAir / NINA / SharpCap

| Capability | ASIAir | NINA | ARA |
|---|---|---|---|
| Light/Dark/Bias/Flat as sequence types | Yes | Yes | Yes |
| Apply calibration at capture | No | No | No (capture-only philosophy) |
| Auto-flats at dusk with flat panel | Yes | Yes (FlatPanelFlats) | Yes (inherited) |
| Dark library auto-generation | Limited | Yes (DarkLibraryInstruction) | Yes (inherited) |
| Session metadata recorded in FITS | Yes | Yes | Yes (richer — full equipment state) |
| **Generate flats matching a past session** | **Yes** | **No** | **Yes (ARA-native feature)** |
| Library browsing UI | Limited | Sequencer view | Rich (Settings → Calibration Library) |

The "matching flats from past session" is genuinely a unique-to-ARA improvement over NINA, inspired by ASIAir's workflow.

---

## 40. Captured-image library workflow

WILMA's Image Library tab is the user's window into everything the Pi has captured. Frames are organized by session (the user's mental unit) and cross-indexed by target (so multi-night, multi-year projects line up perfectly). Available on desktop with full UX, on mobile with view-only UX (per §41).

### 40.1 Frame storage on Pi (recap)

Per §29 + §39: FITS frames live at the configured save path (USB drive recommended) at `<save-path>/captures/<session-id>/<target>/<filter>/<frame>.fits`. Sidecar previews at `<frame>.preview.jpg`. Metadata indexed in Pi-side SQLite `frames` table (see §39.3).

### 40.2 Preview tiers

Server generates two JPEG previews per captured FITS:

| Preview | Resolution | Size | Purpose |
|---|---|---|---|
| `<frame>.thumb.jpg` | Max 480×360 | ~50 KB | List views, dashboard tiles, search results |
| `<frame>.preview.jpg` | Native sensor resolution, quality 90 | ~3-8 MB | Full pinch-to-zoom pixel peep on mobile + desktop |

Both generated server-side at capture time (per §28.5 / §39 — already in the capture pipeline). Stretched per the user's profile-default stretch setting; user can request alternative stretches via API.

### 40.3 API endpoints

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/v1/sessions` | List sessions (id, target, date, frame count, total integration, filters used) |
| `GET` | `/api/v1/sessions/{id}` | Full session metadata |
| `GET` | `/api/v1/sessions/{id}/frames` | List frames in session, filterable by frame type / filter / rating |
| `GET` | `/api/v1/frames/{id}` | Single frame metadata |
| `GET` | `/api/v1/frames/{id}/thumb` | Tiny JPEG (for lists) |
| `GET` | `/api/v1/frames/{id}/preview` | Full-resolution JPEG (for pixel peep) |
| `GET` | `/api/v1/frames/{id}/fits` | Original FITS file (full bytes, large) |
| `PATCH` | `/api/v1/frames/{id}` | Update rating, tags, notes |
| `DELETE` | `/api/v1/frames/{id}` | Delete frame (FITS + previews + DB row) |
| `POST` | `/api/v1/frames/bulk` | Bulk operations (multi-rate, multi-tag, multi-delete) |
| `GET` | `/api/v1/targets` | Roll-up by target: cumulative integration time, sessions count, filter breakdown |
| `GET` | `/api/v1/targets/{name}/sessions` | List all sessions that imaged a given target |
| `POST` | `/api/v1/targets/{name}/resume` | Create a new sequence template seeded from the most-recent session's plate-solve + rotator + filter usage (per §40.6) |

### 40.4 Image Library tab UI (desktop)

```
┌──────────────────────────────────────────────────────────────┐
│  Image Library                                                │
│  [By Session ▼]  [▼ All filters]  [⭐ Any rating]  [🔎 Search] │
│  ────────────────────────────────────────────────────────────  │
│  ▼ 2026-05-18 — M42 Orion Nebula (Backyard Texas)              │
│    4h 12min total · L:48 R:32 G:32 B:32 (144 frames)           │
│    [Capture Matching Flats]  [Resume Target]                   │
│                                                                │
│    ┌──┐┌──┐┌──┐┌──┐┌──┐┌──┐┌──┐┌──┐                            │
│    │  ││  ││  ││  ││  ││  ││  ││  │   ... 144 thumbnails       │
│    └──┘└──┘└──┘└──┘└──┘└──┘└──┘└──┘                            │
│                                                                │
│  ▼ 2026-05-12 — NGC 6188 Fighting Dragons (Backyard Texas)    │
│    2h 30min total · Hα:30 OIII:30 SII:30 (90 frames)          │
│    [Capture Matching Flats]  [Resume Target]                   │
│    ...                                                         │
└──────────────────────────────────────────────────────────────┘
```

- **Group toggle**: [By Session] / [By Target] / [By Date]
- **Filter pills**: filter band, frame type (Light/Dark/Bias/Flat), rating
- **Search**: target name, filter, date range, free-text in notes
- **Per-session row**: [Capture Matching Flats] (§39.5) and **[Resume Target]** (§40.6)
- **Thumbnail strip**: tap any → full frame viewer

### 40.5 Frame viewer (desktop + mobile)

```
┌──────────────────────────────────────────────────┐
│  M42_L_2026-05-18T22:14:32_120s.fits      ⭐⭐⭐⭐  │
│  ──────────────────────────────────────────────  │
│                                                  │
│       [full preview image, pinch/scroll to zoom] │
│                                                  │
│  ──────────────────────────────────────────────  │
│  Exposure: 120s  Gain: 100  Offset: 50           │
│  Filter: L       Bin: 1×1                        │
│  HFR: 1.42       Stars: 487                      │
│  Median ADU: 1284   Background: 1102             │
│  Sensor temp: −10.0°C  Focus: 14820 steps        │
│  Captured: 2026-05-18 22:14:32 UTC               │
│                                                  │
│  Notes: [...]                                    │
│  Tags: [good_seeing]                             │
│                                                  │
│  [Rate]  [Tag]  [Open in App]  [Show in Folder]  │
│  [Download FITS]  [Delete]                       │
└──────────────────────────────────────────────────┘
```

- Pinch-to-zoom + pan on desktop (trackpad gestures) and mobile (touch)
- Full-resolution JPEG preview by default; **[Download FITS]** pulls original 50MB file
- **[Open in App]** invokes OS file-association (system "open with" → PixInsight / Siril / GraXpert / etc. based on user's default FITS handler)
- **[Show in Folder]** opens the file's location in Finder/Explorer (desktop only)
- 0–5 star rating; free-text tags; optional notes
- HFR / star count / median ADU shown inline (read from the session DB, originally computed server-side at capture time)

### 40.6 "Resume Target" workflow — multi-year project alignment

Critical for users building up integration on a target across months or years. The button on the per-session row in the library:

1. User picks a target with prior history (e.g., M42 with 4 sessions over 18 months)
2. WILMA calls `POST /api/v1/targets/M42/resume`
3. Server returns a **new sequence draft** pre-configured to align exactly with the most-recent session:
   - Plate-solve target = recorded center RA/Dec from that session
   - Rotator angle = recorded ROTANG from that session
   - Filter list = filters historically used (sorted by frequency)
   - Exposure / gain / offset defaults = pulled from that session
   - Profile reference = same equipment expected (warn if profile has changed substantially)
4. User reviews + tweaks (add/remove filters, change exposure count) → [Save] / [Run]
5. When the sequence runs, the **§28 recovery flow runs in reverse**: mount slews to target, plate-solves to the *recorded* RA/Dec/rotation (not just "close enough"), refines until within tight tolerance (default 30 arcsec position, 0.5° rotation — half the recovery defaults), then begins capturing
6. New frames written with `OBJECT` matching the target name, so they roll up into the same per-target aggregate

This is what makes "come back in 3 years and add more data" work: the rotator and plate-solve solution are reproducible because we recorded them precisely.

### 40.7 Auto-rating + HFR drift detection (the "clouds, not focus" pattern)

Server analyzes HFR and star count after each frame:

**Auto-rating (per-frame, inherited from NINA logic):**
- HFR > profile threshold (default 2× session-median) → frame rated 1⭐ (auto-reject suggested)
- Star count < profile threshold → rated 1⭐
- Median ADU below floor (severely underexposed) or above ceiling (saturation) → rated 2⭐
- Otherwise → rated 3⭐ by default; user upgrades to 4⭐/5⭐ if they pixel-peep and like it

**Pattern detection (ARA-native — flagged "clouds, not focus"):**
After each autofocus completes, server tracks:
- Frame immediately post-AF: HFR
- N consecutive subsequent frames: HFR
- If pattern emerges (good HFR → degraded HFR → AF retriggers → good HFR → degraded HFR again, within a short window), pattern is `cycling_degradation`
- Queue notification: *"Autofocus completed twice in 12 minutes but HFR degrades immediately after each focus run — likely transient clouds or seeing, not a focus mechanism issue. Check sky conditions."*
- Optional: pause the sequence after N consecutive bad frames (configurable in safety policies §35; default off)
- Bad frames during the cycle are auto-rated 1⭐ for post-processing rejection

### 40.8 Bulk operations

Multi-select frames via Shift+Click (desktop) or long-press + tap (mobile):
- **Rate selection** — set 0-5 stars on all
- **Tag selection** — add/remove tags on all
- **Delete selection** — confirm + remove from disk + DB
- **Download FITS for selection** — zip + download to WILMA
- **Export** — copy to a folder picked by the user (desktop only; on mobile this is "Save to Files" or share sheet)

### 40.9 Storage management

- Per-session row shows total disk used
- Filter view: "Show only frames > 30 days old, < 3⭐ rating" → bulk-prune candidates
- Auto-prune policy (Settings → Storage on the Pi): never / weekly / monthly, with rules ("delete frames < 2⭐ older than X days, never delete frames marked 4⭐+")
- All destructive operations confirm + are logged

---

## 41. Mobile companion mode (iOS / Android)

WILMA on iOS/Android runs in **Companion Mode** — same Flutter codebase as the desktop client, but the UI is tailored for phone/tablet form factors and many "configuration" workflows are intentionally absent (replaced by a "Open ARA on your desktop to do this" prompt). The phone is for monitoring, viewing, and emergencies — not for planning tomorrow's session.

### 41.1 Mobile companion — what it CAN do

| Capability | Notes |
|---|---|
| Connect to Pi (mDNS discovery + token) | Same flow as desktop (§30) |
| **GPS + time push to Pi** | Primary value-add when user has no USB GPS dongle (§31) |
| **Dashboard** | Current sequence, target, last frame thumbnail, time-to-next-frame, equipment connection state, sky safety status |
| **Image library browsing** | Same data as desktop (§40), responsive layout — grouped by session, scrollable thumbnail strips |
| **Frame viewer with pinch-to-zoom** | Full-resolution JPEG preview, native gestures, HFR + star count + temp displayed |
| **Live preview during active session** | Subscribes to WebSocket `frame.complete` events, latest frame appears automatically |
| **Emergency stop button** | Always visible in the persistent bottom bar; same flow as desktop (§35.3) |
| **Safety alarm response** | Receives `safety.unsafe` WebSocket events; full-screen alarm modal with audio + vibration (§35.5); [Emergency Abort] / [Override] |
| **Push notifications** | Sequence complete, safety alerts, HFR drift detection, recovery events |
| **Log tail** | Read-only live log stream |
| **Rate + tag frames** | Touch-friendly star rating + tag chips |
| **Download FITS for off-device processing** | Save to platform Files / Photos / share sheet |
| **Server / token management** | Same Settings → Server panel as desktop |

### 41.2 Mobile companion — what it explicitly does NOT do

| Capability | Why excluded | What user sees |
|---|---|---|
| Sequence editor | Drag-drop instruction tree is bad UX on a 6-inch screen | "Sequence editing requires the ARA desktop app — open WILMA on your Mac, PC, or Linux machine to edit sequences." Quick-share link button to open desktop on the same Wi-Fi. |
| Profile / equipment configuration wizard | 18-screen wizard cramming into phone = pain | Same redirect message |
| Sky Atlas (full Aladin Lite + Tonight's Sky) | Aladin Lite WebView with 21-survey browsing is computationally heavy on phones + 500MB+ tile bundling cost | Same redirect; users who want sky atlas on mobile run Stellarium or SkySafari standalone |
| ASTAP path / autofocus / plate-solve config | Settings | Same redirect |
| Sequence templates / instantiation | Editor-adjacent | Same redirect |

When a mobile user taps something disallowed, they get a polite modal with a "Copy link to send to your desktop" option that puts an ARA-protocol URL on the clipboard (e.g., `araapp://session/123/edit`) that the desktop app can pick up.

### 41.3 Mobile-specific UX considerations

- **Always-on bottom bar**: [Dashboard] [Library] [Logs] [Emergency Stop] — emergency button is permanently visible regardless of which tab is active
- **Push notifications**: Firebase Cloud Messaging on Android, APNs on iOS, **but only between WILMA and the Pi** — no third-party telemetry path; Pi sends webhook to client's notification endpoint. (v0.0.1 may defer push and rely on in-app foreground notifications only — depends on Apple/Google account setup effort)
- **Background mode caveat**: iOS aggressively suspends backgrounded apps; Android less so. App in background may miss WebSocket events; user opens app → fresh state pulled via REST snapshot. Push notifications wake the user for critical events even if app is suspended.
- **Tablet (iPad / Android tablet)**: companion mode renders with more density (split view: dashboard + library side-by-side); pinch-to-zoom on frames goes huge; otherwise same scope as phone. iPad Pro users get a perfectly usable casual-monitor experience.
- **Apple Watch / Wear OS**: out of scope for v0.0.1. Could be a future "notifications only" companion app.

### 41.4 Shared Flutter codebase, conditional shell

```dart
// pseudocode
final isCompanionMode = (Platform.isIOS || Platform.isAndroid);

Widget appShell() => isCompanionMode
    ? CompanionShell(routes: companionRoutes)
    : DesktopShell(routes: desktopRoutes);
```

Shared:
- API client (auto-generated from OpenAPI)
- State management (Riverpod providers)
- WebSocket connection + event handlers
- Authentication + token storage
- Common widgets (frame viewer, dashboard tiles, status indicators)

Different:
- Top-level navigation (tabs vs nav rail)
- Some screens entirely (sequence editor absent on mobile, Aladin tab absent)
- Modal sizing (full-screen on mobile, dialog on desktop)
- Gesture handling (touch-first on mobile, mouse-first on desktop)

### 41.5 Mobile-only entry points

Two flows that exist ONLY on mobile:

- **First-launch GPS push** — if user opens mobile app and the Pi reports no recent time-sync, mobile companion auto-prompts to push device GPS+time without requiring profile-screen entry. Matches the "I just want to give the Pi a clock and go" use case.
- **Wake-from-notification → live frame view** — push notification "Frame 47 captured" → tap → opens directly to that frame in the viewer.

### 41.6 Versioning + acronym

WILMA (Windows / iOS / Linux / Mac / Android) acronym is preserved. Mobile platforms (iOS, Android) explicitly run in Companion Mode by default. Desktop platforms (Windows, macOS, Linux) run the full client.

In practice this means a single `flutter build` per platform, with platform-detection-driven shell selection at runtime.

---

## 42. Hardware fault recovery (per-equipment)

Distinct from §28 (server crash recovery). This section covers per-equipment "something went wrong while the server was running" handling: camera disconnects mid-exposure, mount loses tracking, focuser stalls, EFW jams, dew heaters fail, etc. Most of this logic is preserved from NINA; this section documents what's preserved + the few ARA-native additions (switch value-tolerance, dew detection, hot-reconnect).

### 42.1 Retry-then-action pattern (universal)

Every fault uses the same flow:

```
Fault detected
   ↓
Retry N times with exponential backoff (default: 3 attempts, 5s/15s/30s)
   ↓ all retries failed
   ↓
Execute fault's configured action per profile
   ↓
   Continue   = log, keep going (use for benign / informational faults)
   Notify     = queued WebSocket event, sequence continues
   Pause      = sequence pauses at next safe point, equipment stays connected, user resumes manually
   Abort+park = full §35.3 emergency stop sequence (camera abort, guider stop, mount park, etc.)
```

Per-fault action is configurable in profile safety policies (§35 extension), with the defaults below.

### 42.2 Fault matrix

| Fault | Detection | Default action | Notes |
|---|---|---|---|
| Camera disconnect / capture error mid-exposure | Alpaca connection error or capture timeout | Reconnect → Pause if persistent | In-flight frame lost; next frame retries |
| Camera cooling failure (set temp not reached after timeout) | `CCDTemp` vs `SetCCDTemperature` drift > 5°C for > 5 min | Notify | Don't abort — user may want to image at warmer temp |
| Camera dew heater unexpectedly OFF | Alpaca `DewHeaterPower` queried, expected vs reported | Re-command ON → Notify if still off | Camera-integrated heaters only |
| Mount loses tracking | Alpaca `Tracking = false` unexpectedly during exposure | Re-enable → Pause if rejected | Common cause of trailed stars |
| Mount slew error / refuses command | Alpaca slew returns error or doesn't complete | Retry → Abort + park | May indicate physical obstruction |
| Mount unexpected park / disconnect | Alpaca connection lost or mount auto-parks | Reconnect → Abort + park if persistent | Cable disconnect is most common |
| Focuser stalls | Commanded position not reached within timeout | Retry → recalibrate backlash → Notify | Common in cold weather (lubricant viscosity) |
| **EFW (filter wheel) jam / position not reached** | Commanded slot not reached within timeout | Retry → Notify | User must intervene physically |
| Rotator (CAA) runaway / position drift | Reported angle differs from commanded > tolerance (default 0.5°) | Re-issue → Notify | Mechanical issues |
| Guider (PHD2): loses calibration | PHD2 calibration-failed event | Recalibrate → Pause if persistent | Common after meridian flip |
| Guider (PHD2): loses guide star | PHD2 star-lost event for > 30s | Pause → wait for recovery (clouds passing) | Often transient |
| Guider (PHD2): dither timeout | Dither not settled within 60s | Continue (log warning) | Skip dither, keep imaging |
| Plate solve failure | After §28.2 retries (3 attempts) | Pause + notify | User can re-frame target or skip |
| ASTAP / Astrometry.net executable crash | Process exit code != 0 | Retry once → Notify | Re-invoke; bad images usually cause this |
| **External dew heater (Alpaca Switch) commanded ON but reporting OFF** (boolean switch) | Switch read-back mismatch | Re-command → Notify if still off | Power-port dew straps |
| **External switch value mismatch** (PWM heater, dimmable flat panel, etc. — value-based ISwitch) | Commanded value vs read-back outside tolerance (default ±5%) | Re-command → Notify | Pegasus PowerBox-style devices |
| **Dew formation suspected** | Pattern: humidity near 100% AND ambient at dew point AND HFR rising gradually (halos forming) | Notify | Advisory only — no auto-abort. User intervention required (wipe optics, enable heaters) |

### 42.3 Hot-reconnect on disconnect

When any device disconnects mid-session, ARA Core attempts automatic reconnect with backoff before pausing the sequence:

```
Disconnect detected
   ↓
Attempt 1: reconnect immediately
   ↓ fail
Attempt 2: wait 5s, reconnect
   ↓ fail
Attempt 3: wait 15s, reconnect
   ↓ fail
Attempt 4: wait 30s, reconnect
   ↓ fail
Attempt 5: wait 60s, reconnect
   ↓ fail
Pause sequence + queue notification to WILMA
```

Most disconnects are transient (USB hub hiccup, AlpacaBridge restart, WiFi blip) and recover by attempt 2 or 3.

### 42.4 Switch value tolerance (Alpaca `ISwitch`)

ARA treats Alpaca Switch devices using the full `ISwitch` interface:

- **Boolean switches** (port on/off): fault if commanded state ≠ read-back state
- **Value-based switches** (PWM, dimmable): fault if `|commanded − readBack| > tolerance × range`
  - Default tolerance: 5% of the switch's min/max range
  - Tolerance configurable per switch in profile (some devices have 10%+ inherent noise)
  - Useful for: dew heater PWM, flat panel brightness, focuser temperature setpoint on heated focusers

### 42.5 Fault logging

Every detected fault is logged to the Pi session database:

```sql
CREATE TABLE faults (
  id TEXT PRIMARY KEY,
  session_id TEXT REFERENCES sessions(id),
  detected_at TIMESTAMP,
  equipment_type TEXT,       -- "camera", "mount", "focuser", "efw", "dewheater", etc.
  equipment_id TEXT,         -- Alpaca device id
  fault_type TEXT,           -- "disconnect", "tracking_lost", "value_mismatch", etc.
  details JSON,              -- fault-specific payload
  action_taken TEXT,         -- "retry", "reconnect", "pause", "abort"
  resolved_at TIMESTAMP,     -- null if unresolved
  affected_frames JSON       -- list of frame IDs that may have been impacted
);
```

### 42.6 Fault visibility in WILMA

- **Live**: dashboard equipment chip turns yellow/red on fault detection; tap → fault details modal with retry attempt count, last error message
- **Session library**: per-session fault count badge; tap session → "Faults" tab shows timeline with each fault, action taken, frame impact
- **Image library** (§40): individual frames captured during a fault window are marked with a fault icon overlay (e.g., "captured while mount tracking was lost — likely trailed")
- **Per-fault recommendation**: WILMA shows brief advice ("Mount lost tracking — check cable, weight balance, slew limits")

### 42.7 What's preserved verbatim from NINA

- Backlash compensation algorithm + per-direction step counts
- Focuser temperature compensation curves
- Autofocus retry logic + step pattern
- PHD2 calibration retry semantics
- Plate-solve retry strategy
- Per-instruction retry counts in the sequencer

### 42.8 What's ARA-native

- Switch value-tolerance for PWM/dimmable devices
- Dew formation detection from weather + HFR pattern (§40.7 + this section)
- Hot-reconnect with explicit backoff schedule (NINA had partial; ARA formalizes)
- Per-frame fault flagging in the image library
- Unified retry-then-action pattern across all fault types (NINA varies by subsystem)

---

## 43. Backup + restore

ARA's backup model is **"the USB drive IS the backup unit"** because §29 makes USB storage mandatory and ALL persistent state (profiles, sequences, session DB, calibration library, FITS frames, logs) lives there. The Pi's SD card is disposable.

### 43.1 The portability story

Because everything lives on USB, ARA Core gets a powerful invariant for free: **pull the USB drive out, plug it into a different Pi, and ARA picks up exactly where you left off.** Same profiles, same sessions, same calibration library, same in-flight sequence state.

This makes:
- **Pi replacement** trivial — SD card died? Buy a new one, flash Trixie, `apt install openastroara-server`, plug your USB in, you're back.
- **Field-to-home migration** seamless — image at a dark site on Pi A, drive home, plug the USB into Pi B (your home observatory Pi) for processing without moving files.
- **Hardware testing** safe — try a different Pi or Orange Pi or RockChip, swap USB between them, no data risk.

### 43.2 What backups protect against

The USB-IS-the-data model is fast and portable, but it has one failure mode: **the USB drive itself dies or is lost/stolen**. Backups address only this.

Four backup layers, in priority order:

| Layer | What it protects | Scope |
|---|---|---|
| **1. Drive-to-drive clone** (recommended primary) | USB drive failure, loss, theft | Everything: FITS + DB + profiles + sequences + calibration |
| **2. Real-time backup stream to desktop WILMA** (see §44) | USB drive failure mid-session — frames already streamed are safe on the PC | FITS files trickled to desktop in real time during imaging |
| **3. Server-generated backup ZIP** (downloadable via WILMA) | Profile/sequence/setting loss; quick portability | Profiles + sequences + DB + calibration metadata. **NOT FITS files** (too large). |
| **4. Auto-snapshots on the same USB** (low-value but cheap) | "I accidentally deleted my profile" / catastrophic bug in ARA writes bad DB | Profiles + sequences + DB snapshot. Stored under `.araback/` on the same drive. |

### 43.3 Drive-to-drive clone (primary backup mechanism)

The recommended user workflow: every few weeks, clone the working USB drive to a second drive kept in a separate location.

DEPLOY.md documents the procedure:

```bash
# Identify both drives:
lsblk -f
# Working drive = /dev/sda, backup drive = /dev/sdb (example)

# Clone, drive-to-drive (full block-level copy):
sudo dd if=/dev/sda of=/dev/sdb bs=4M status=progress

# Or, file-level mirror (faster, only changed files):
sudo rsync -aAXxv --delete /media/openastroara/ /mnt/backup-drive/openastroara/
```

ARA does not automate this — the user manages their backup drive(s). v0.1.0 may add a "backup wizard" that guides the user through this with checks (drive size, free space, integrity verify) but stays out of the user's hardware.

### 43.4 Server-generated backup ZIP

For lightweight portability + WILMA-driven backup convenience:

`POST /api/v1/server/backup/create`
- Server zips: `profiles/`, `sequences/library/`, `templates/`, `db/openastroara.db` (snapshot), `calibration/` metadata sidecar JSONs only (not the FITS frames)
- Writes to `/media/openastroara/.araback/openastroara-backup-YYYY-MM-DDTHH-MM-SS.zip`
- Returns the zip's metadata + download URL

`GET /api/v1/server/backup/{filename}` → downloads the zip to WILMA.

`POST /api/v1/server/backup/restore` (multipart upload of a zip)
- Server validates zip integrity
- Pre-flight: lists what's in the zip + version compatibility
- User confirms via WILMA
- Server: stops sequence if running, backs up current state to a "pre-restore" snapshot, then unzips the backup zip over the current data, restarts to pick up new state
- WILMA reconnects

Typical zip size: 5-50 MB depending on history (vs the full USB which is GB to TB).

### 43.5 Auto-snapshots on the USB drive

Lowest-priority but cheap insurance against accidental corruption:

- Configurable in Settings → Backup:
  - "Auto-snapshot: Off / Daily at 2 AM / After every sequence"
- Server creates a backup zip (per §43.4) into `/media/openastroara/.araback/` with the daily/sequence-end timestamp
- Auto-prune: keeps last 14 snapshots, oldest auto-deleted

This protects against "my profile got corrupted by a bug" but does NOT protect against drive failure (snapshots are on the same drive).

### 43.6 Restore flow on a fresh Pi

User scenarios:

**Scenario A: Same USB drive, new Pi (most common)**
1. Flash Trixie on the new Pi's SD card, configure Wi-Fi, install openastroara-server per §34.1
2. Plug in the USB drive
3. First-run wizard detects existing ARA data on the drive, prompts: *"Found existing ARA data: 12 profiles, 47 sessions, 3.2 GB calibration library. Use this data?"* → [Use Existing Data] / [Initialize Fresh]
4. Server pins the UUID, registers existing structure, starts in `ready` mode with full history
5. WILMA prompts for the new token (regenerated per-Pi), reconnects

**Scenario B: Fresh USB drive + backup zip**
1. Same as above, but the USB is fresh
2. Plug in USB, run first-run wizard → server initializes empty structure
3. In WILMA: Settings → Backup → Restore from ZIP → upload `openastroara-backup-*.zip`
4. Server restores profiles + sequences + DB metadata
5. Note: calibration FITS files NOT in the backup zip; only the metadata. User must separately recover those from a drive clone if needed.

**Scenario C: Fresh USB drive + no backup (rebuilding from scratch)**
1. Plug in fresh USB, first-run wizard initializes
2. User goes through profile-setup wizard (§37) fresh
3. No historical data; user starts from zero

### 43.7 Backup metadata

Every backup zip has a top-level `manifest.json`:

```json
{
  "schemaVersion": "openastroara-backup-v1",
  "createdAt": "2026-05-19T03:14:15Z",
  "createdBy": "ara-pi-observatory",
  "serverVersion": "0.0.1-ara.1",
  "contents": {
    "profiles": 12,
    "sequences": 47,
    "templates": 4,
    "frames_metadata_rows": 18420,
    "calibration_metadata_files": 153
  },
  "totalSizeBytes": 24123456
}
```

Restore endpoint validates `schemaVersion` against current ARA Core's known versions before proceeding.

### 43.8 What's NOT backed up

These are deliberately excluded:

- **FITS frames** (`captures/` and `calibration/` FITS files) — too large for a portable ZIP. User backs these up via drive clone (§43.3). The DB has all metadata pointing at the frame paths, so restoring a backup with no FITS files leaves the DB "pointing at missing files" — WILMA shows these frames with a missing-file indicator and a "scan for files" button to relocate (e.g., user manually copied FITS to a different USB).
- **HiPS tile downloads on WILMA** — re-downloadable from CDS (§36)
- **WILMA bundled catalogs** — re-installable from app build (§36.1)
- **System logs older than 14 days** — pruned

### 43.9 Settings → Backup panel (WILMA)

```
┌──────────────────────────────────────────────────────┐
│  Backup                                              │
│  ──────────────────────────────────────────────────  │
│  Primary backup: clone the USB drive periodically    │
│  → [Open DEPLOY.md instructions]                     │
│                                                       │
│  Server backup ZIP:                                   │
│    Last created: 2 days ago (24 MB)                   │
│    [Create Backup Now]  [Download Last Backup]        │
│                                                       │
│  Auto-snapshot to USB:                                │
│    [ Off ▼ ]                                          │
│    Options: Off / Daily at 2 AM / After every session │
│                                                       │
│  Restore:                                             │
│    [Restore from ZIP] (select a .zip from this device)│
└──────────────────────────────────────────────────────┘
```

### 43.10 Best-practice recommendation in README

> **Back up your USB drive.** ARA stores everything on your USB drive — profiles, sequences, sessions, calibration, frames. It's portable and durable, but USB drives can still fail. We strongly recommend cloning your working drive to a second drive every few weeks, and keeping the backup drive in a separate location (or at least a separate room). The drive-to-drive clone takes a few minutes and protects against the one failure mode the "everything on USB" model has: the drive itself dying. **For users with a desktop running WILMA on the same LAN, enable real-time backup streaming (§44) to get a continuously-mirrored copy of new frames on your PC, so even an unexpected USB failure mid-session loses at most the last in-flight frame.**

---

## 44. Real-time backup stream to desktop WILMA

Layer 2 of the backup strategy from §43.2. Optional, opt-in feature that pulls each newly-captured FITS file from the Pi to a desktop WILMA in real time during imaging. Result: the PC has a continuously-updating mirror of the Pi's frames. If the USB drive dies overnight, the user wakes up to find every captured frame already on their desktop.

### 44.1 What it does

- After each FITS file is finalized on the Pi (post-capture, post-preview-generation), the Pi enqueues the file for backup streaming
- A desktop WILMA, if connected with backup-stream enabled, polls the queue and pulls each file via HTTP
- WILMA writes to a user-configured local directory (default `~/Documents/OpenAstroAra/Backups/<pi-hostname>/<session>/<filter>/`)
- Verifies SHA-256 after download; re-requests on mismatch
- Acknowledges Pi when stored locally so the Pi can mark "synced to this WILMA"

### 44.2 Why pull-based, not push

WILMA could theoretically expose an HTTP endpoint for the Pi to POST to, but:
- WILMA on macOS/Windows behind home NAT has no stable inbound port
- WILMA mobile may be on cellular with no inbound connectivity
- Browser-style firewalls block inbound by default

WILMA pulling FROM the Pi (Pi is the LAN-stable listener) just works. No port forwarding, no firewall exceptions on the desktop.

### 44.3 Restrictions

- **Desktop WILMA only** (Windows, macOS, Linux desktop). Mobile companion mode (§41) does NOT participate — phones don't have terabytes of storage and would burn cellular data.
- **Same LAN recommended** but not required (can work over VPN, just slower)
- **Single active stream target per Pi for v0.0.1** — only one WILMA at a time is the "backup target." If two desktops both enable backup stream, Pi designates whichever connects first as the active one and tells the other "another WILMA is already streaming."
- v0.1.0 may add multi-target (mirror to two PCs simultaneously)

### 44.4 Bandwidth throttling

Streaming runs in the background and must not interfere with primary operations (live preview, WebSocket status, current sequence capture). Two control mechanisms:

**Token bucket bandwidth limit** (default 50% of measured uplink):
- WILMA measures effective bandwidth on first connection (one-time HTTP throughput test)
- Bucket refill rate: configurable in Settings → Backup → "Stream bandwidth limit"
- User can override to a fixed value (e.g., "10 Mbps") or set to "Use all available bandwidth"

**Capture-aware backoff**:
- During an active exposure: streaming throttles to ~10% of normal rate (avoid USB I/O competition on the Pi)
- Between exposures (filter changes, dithering, idle): streaming runs at full configured rate
- During dawn/dusk idle: full rate
- WILMA + Pi coordinate via the existing WebSocket events (`exposure.started`, `exposure.complete`)

### 44.5 Endpoints

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/v1/server/backup-stream/status` | Returns `{ "enabled": bool, "active_target": "wilma-hostname", "pending_count": N, "synced_count": M, "queue_size_bytes": N }` |
| `POST` | `/api/v1/server/backup-stream/claim` | WILMA claims the stream slot for this Pi (returns 200 + target token, or 409 if another WILMA is active) |
| `POST` | `/api/v1/server/backup-stream/release` | WILMA voluntarily releases the slot (e.g., disk full, user opted out) |
| `GET` | `/api/v1/server/backup-stream/queue?limit=N` | Returns list of pending frames: `[ { "id", "sha256", "size_bytes", "captured_at", "session_id" } ]`, ordered oldest first |
| `GET` | `/api/v1/frames/{id}/fits` | (existing per §40.3) — Pulls the FITS bytes |
| `POST` | `/api/v1/server/backup-stream/ack` | WILMA acknowledges successful storage. Body: `{ "frame_id", "sha256_verified" }`. Pi marks the frame `synced_to_target: <wilma>`. |

### 44.6 State on the Pi

Sessions DB gains per-frame columns:
```sql
ALTER TABLE frames ADD COLUMN sync_target TEXT;       -- e.g., "wilma-mac-joey"
ALTER TABLE frames ADD COLUMN synced_at TIMESTAMP;    -- null if not yet synced
ALTER TABLE frames ADD COLUMN sha256 TEXT;            -- computed at capture time
```

Queue is implicit: `SELECT * FROM frames WHERE sync_target = '<active>' AND synced_at IS NULL ORDER BY captured_at`.

### 44.7 State on WILMA

WILMA's local DB (SQLite at `<wilma-data>/backup-stream.db`):
```sql
CREATE TABLE stream_state (
  pi_hostname TEXT PRIMARY KEY,
  local_root TEXT,           -- where to write files
  last_synced_frame_id TEXT,
  total_bytes_received INT,
  enabled BOOLEAN
);

CREATE TABLE local_frames (
  frame_id TEXT PRIMARY KEY,
  local_path TEXT,
  sha256 TEXT,
  pulled_at TIMESTAMP
);
```

### 44.8 User flow

1. User opens WILMA on desktop, navigates to Settings → Backup → Stream from Pi
2. Toggle: "Stream new frames to this device": [Off / On]
3. If turned On: WILMA calls `POST /api/v1/server/backup-stream/claim`. If another WILMA is already claimed: error "Another desktop is already streaming from this Pi (<hostname>). Disconnect it first."
4. User picks a local storage path (default `~/Documents/OpenAstroAra/Backups/<pi-hostname>/`)
5. WILMA shows storage estimate: "Pi has 47 GB of frames not yet on this device. Estimated download time at current bandwidth: 2h 14m. Free space on chosen drive: 412 GB."
6. Streaming begins; status visible in:
   - Persistent footer indicator: *"Backup stream: 12 of 144 frames synced (8.4 GB)"*
   - Dashboard tile: progress bar + estimated time remaining
   - Per-frame icon in Image Library: 🟢 (synced to this PC) / ⚪ (on Pi only)

### 44.9 Failure modes

| Failure | Behavior |
|---|---|
| WILMA closed mid-stream | Pi keeps frames; queue waits; resumes when WILMA reconnects |
| Network drops | Both sides retry with backoff; queue persists on Pi |
| SHA-256 mismatch on download | WILMA re-requests the file; logs an integrity error |
| WILMA disk fills | WILMA stops pulling, releases the slot, surfaces a clear notification: *"Backup stream paused — only 1.2 GB free on backup drive. Free space and re-enable."* |
| Pi USB unmounts mid-stream | §29.1.2 handles; stream pauses; resumes on remount |
| User unplugs Pi entirely | Frames already streamed are safe on WILMA. Frames not yet streamed are gone if the USB was the only copy. |

### 44.10 What this protects against (the headline benefit)

Compared to drive-clone backups (which happen weekly) and ZIP backups (which happen on user demand), the real-time stream protects against **mid-session USB drive failure**. The worst case becomes: lose the in-flight FITS frame (the one being captured at the instant of failure). All previously-captured frames are safe on the PC.

Combined with §29's mandatory-USB design, this makes ARA's reliability model significantly stronger than NINA's (where the only protection against drive failure is the user remembering to copy files off).

### 44.11 NOT in v0.0.1 scope

Deferred to v0.1.0:
- **Multi-target streaming** (mirror to two desktops simultaneously)
- **Cloud streaming** (rclone-based push to S3, Google Drive, etc.) — same protocol model but pull from a third-party endpoint
- **Selective stream** (only stream frames matching certain filters / rated 3⭐+) — initial version streams everything
- **WAN-friendly stream** (compressed transfer, delta-encoding, etc.) — initial version uses raw FITS over plain HTTP

---

## 45. Polar alignment — iPolar-style continuous loop

### 45.1 Why not three-point polar alignment (TPPA)

NINA's TPPA plugin slews to 3 widely-separated points, plate-solves each, and computes alignment from the geometric inconsistencies. It works, but:

- **Fragile** — tiny mount adjustments cause wild reported-error swings because each plate-solve carries solver noise, and that noise propagates into the alignment vector
- **Slow over Alpaca/HTTP** — main camera at full resolution = 50+ MB FITS per point × 3 points × multiple iterations = lots of waiting on transfers
- **Bad UX** — adjust the knob, wait 30s for the next solve, see the error jumped instead of decreased, adjust again, repeat

ARA drops TPPA entirely. The user's tip-of-the-spear hardware (iPolar) shows the better path: a tight feedback loop with small images and a visual aim point.

### 45.2 iPolar's approach + ARA's adaptation

iPolar uses a **dedicated small camera on the RA axis with a ~13° FOV pointed at the pole**. It plate-solves locally, shows a simple bullseye that turns green when aligned. Continuous loop, snappy, accurate because it measures the RA axis directly.

ARA does the same workflow but **with the user's main imaging camera** (no extra hardware required), using optimizations to overcome the size/speed problem:

1. **Autofocus first** — sharp stars solve faster and more reliably
2. **One dark-frame capture for noise subtraction** — clean signal at short exposures
3. **Bin frames aggressively** (2×2, 3×3, or 4×4 depending on camera capability) — drastically smaller FITS, fast transfer, plate-solve still works because the few stars needed for PA are bright
4. **Loop at ~500 ms** like iPolar — fast feedback, smoothed errors
5. **Zooming bullseye UI** — magnifies as the user converges toward the pole

This gives ARA users iPolar-quality alignment WITHOUT requiring a dedicated PA camera. v0.1.0 will add native support for an actual iPolar / PoleMaster / dedicated PA camera (see §45.10).

### 45.3 Workflow

```
1. User taps "Polar Align" in WILMA
2. Server prompts: "Roughly point your mount at the celestial pole"
   (Polaris in N hemisphere, Sigma Octantis area in S hemisphere)
3. User confirms rough alignment
4. Server: runs autofocus on main camera (fast — uses bundled exposure
   defaults from profile)
5. Server: captures 1 dark frame at the PA exposure/bin/temp (~3 sec)
6. Server: enters continuous loop:
     a. Capture frame at PA exposure (typically 0.5-1s), apply binning
     b. Dark-subtract using the cached dark from step 5
     c. Plate solve (ASTAP, small downsampled FITS)
     d. Compute mount RA axis vs celestial pole offset
     e. Push WebSocket event with offset vector + small JPEG preview
7. WILMA renders zooming bullseye:
     - Red zone when error > 1°
     - Yellow when 10' to 1°
     - Green when < 10'
     - Arrow indicates which way to adjust altitude/azimuth knobs
     - Numerical readout: "Az: -23' Alt: +14'"
     - Live frame preview in corner (small)
8. User adjusts mount alt/az knobs while watching bullseye live
9. When error is within user's target tolerance (default 1 arcmin),
   user taps [Done] — server logs the achieved error to session DB
   and the polar-alignment workflow ends
10. If user aborts or backs out: server kills the loop, mount stays
    where it was (no slewing back to "home" or anything)
```

### 45.4 Binning per camera

Server queries the camera's `MaxBinX` / `MaxBinY` (Alpaca) to pick a sensible PA binning:

| Camera sensor size | Recommended bin | Resulting frame | Transfer time (USB 3.0) |
|---|---|---|---|
| Small (e.g., ASI120MM, 1.3 MP) | 1×1 (already small) | ~2.5 MB FITS | ~50 ms |
| Mid (e.g., ASI294MM, ~12 MP) | 3×3 | ~3 MB FITS | ~60 ms |
| Large (e.g., ASI2600MM, ~26 MP) | 4×4 | ~3 MB FITS | ~60 ms |
| Very large (e.g., QHY600M, ~62 MP) | 4×4 (cap) | ~8 MB FITS | ~150 ms |

User can override in Settings → Polar Align if their camera misbehaves at higher bin. Default works for 95% of cameras.

### 45.5 Dark frame caching

- One dark captured at the start of each PA session (~3 seconds with the chosen exposure/bin)
- Cached in-memory on the Pi for the duration of the PA session
- Discarded when PA workflow ends (don't pollute the regular calibration library)
- Optionally save to calibration library if user toggles "Save PA dark to library" — useful for users who do PA at consistent settings

### 45.6 Plate-solve tuning for PA

ASTAP can solve the binned, dark-subtracted, small-FOV PA frames much faster than full sequence frames:

- Search radius: tight (start at last-known pole RA/Dec)
- Downsample factor: 1 (already small)
- Star detection: aggressive (we don't need precise centroids; we need rough plate solution)
- Solve timeout: 5 seconds (if it can't solve in 5s with these stars + small FOV, something's wrong)

### 45.7 Latency budget per loop iteration

| Stage | Time |
|---|---|
| Exposure (binned, short) | 200-500 ms |
| Sensor download from camera over USB | 50-100 ms |
| Dark subtraction | <10 ms |
| Plate solve (ASTAP, RPi 5 CPU) | 100-300 ms |
| Error computation | <5 ms |
| WebSocket push to WILMA | ~10 ms (LAN) |
| WILMA bullseye update | instant |
| **Total per iteration** | **~400-1000 ms** |

The 500 ms aspiration is achievable on Pi 5 with most cameras; older Pi 4 + heavier sensors may land at 700-900 ms. Either way, an order of magnitude better than TPPA's "wait 30s per measurement" loop.

### 45.8 Math — error vector from plate solve

Given:
- `P_pole` = celestial pole position (NCP: RA=0, Dec=+90 for J2000; SCP: RA=0, Dec=−90)
- `P_solved` = where the mount's optical axis currently points (RA/Dec from plate solve)
- `P_mount_axis` = direction the RA axis is pointing (≠ P_solved if mount isn't perfectly polar-aligned)

To find `P_mount_axis`, server captures two frames with the mount rotated in RA by Δ (e.g., 30°). The two solved positions form a great-circle arc; the center of that arc is `P_mount_axis`. Difference between `P_mount_axis` and `P_pole` is the alignment error, decomposed into altitude + azimuth components per the user's site latitude.

Once `P_mount_axis` is known (after the initial 2-frame seed), subsequent single frames update the estimate via plate-solve + reverse-projection (no need to re-rotate the mount continuously).

This is the same math iPolar uses internally; we're just running it on the main camera at lower resolution instead of a dedicated PA cam.

### 45.9 API endpoints + WebSocket events

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/v1/polar-align/start` | Initiate PA workflow. Body: `{ "exposure_seconds": 0.5, "bin": 4, "target_tolerance_arcmin": 1.0 }`. Defaults from profile. |
| `POST` | `/api/v1/polar-align/refocus` | Re-run autofocus (e.g., user adjusted focus knob) |
| `POST` | `/api/v1/polar-align/recapture-dark` | Capture a fresh dark (e.g., temp drifted) |
| `POST` | `/api/v1/polar-align/complete` | User marks done; server logs achieved error |
| `POST` | `/api/v1/polar-align/abort` | Cancel; mount stays in place |

WebSocket events broadcast continuously while PA loop is active:

```json
{
  "type": "polar_align.progress",
  "ts": "2026-05-19T03:14:15.234Z",
  "payload": {
    "iteration": 47,
    "azimuth_error_arcmin": -23.4,
    "altitude_error_arcmin": 14.2,
    "total_error_arcmin": 27.3,
    "direction_arrow_deg": 247,
    "zone": "yellow",       // red | yellow | green
    "preview_jpeg_url": "/api/v1/polar-align/preview/47",
    "last_solve_seconds": 0.13,
    "stars_detected": 32
  }
}
```

Failure events:

```json
{
  "type": "polar_align.error",
  "payload": { "reason": "solve_failed", "iteration": 47, "stars_detected": 4 }
}
```

### 45.10 WILMA UI — zooming bullseye

```
┌──────────────────────────────────────────┐
│  Polar Alignment                          │
│  ────────────────────────────────────────  │
│                                            │
│        ╭──────────────────╮                │
│        │   ╭──────────╮   │                │
│        │   │  ╭────╮  │   │                │
│        │   │  │  ↗ │  │   │   ← bullseye   │
│        │   │  │ ●  │  │   │     (zooms in  │
│        │   │  ╰────╯  │   │      as error  │
│        │   │   3.7'   │   │      shrinks)  │
│        │   ╰──────────╯   │                │
│        ╰──────────────────╯                │
│                                            │
│  Az: -23'   Alt: +14'   Total: 27'         │
│  ●●●○○  (red — adjust mount knobs)         │
│                                            │
│  ┌─────┐  ┌─────────┐  ┌────────────┐     │
│  │frame│  │Recapture│  │  Refocus   │     │
│  └─────┘  │  Dark   │  └────────────┘     │
│           └─────────┘                       │
│  [Done — mount is aligned]  [Abort]        │
└──────────────────────────────────────────┘
```

- Bullseye **dynamically zooms** based on current error magnitude: outer ring covers ~5° at start, shrinks to 30' when error < 1°, shrinks to 1' when error < 5'. User always sees their dot near the center with the right scale.
- Arrow inside the dot points the direction the user should move the alt/az knobs
- Color zones: red (>1°), yellow (1° → 10'), green (<10' — within tolerance)
- [Done] button enabled only when in the green zone (configurable)
- [Recapture Dark] for when ambient temp drifts mid-session
- [Refocus] if user re-bumped the focuser
- Live frame preview in the bottom-left corner (small, just confirms "we have stars")

### 45.11 Failure modes

| Failure | Handling |
|---|---|
| Plate solve fails (clouds, no stars) | Loop pauses; WILMA shows "No solve — check sky and try again" with retry counter. After 5 consecutive failures, suggest user check focus / point closer to pole |
| Mount can't rotate in RA for initial 30° seed | Cap to whatever rotation is possible; user shown warning that estimate may be less precise |
| Camera reports cooler instability mid-PA | Notify, recapture dark, continue |
| User's site latitude too far from pole (extreme polar latitudes) | Workflow allows; bullseye math still works; just narrower workable window |
| Southern hemisphere | Same workflow, just plate-solving against SCP region instead of NCP — no code difference; the math uses lat/long from profile |

### 45.12 Profile settings

Polar Alignment section of profile (default values shown):

- Exposure time: 0.5 sec
- Binning: auto (per §45.4)
- Target tolerance: 1 arcmin (controls when [Done] enables)
- Initial RA rotation for seed: 30°
- Loop cadence (target): 500 ms
- Save PA dark to library: off

Configurable in Settings → Polar Align AND in the profile wizard (could go as a brief screen, or as part of the mount config screen — TBD per wizard design).

### 45.13 Session log

After PA completes (or aborts), one row added to a `polar_alignments` table on the Pi:

```sql
CREATE TABLE polar_alignments (
  id TEXT PRIMARY KEY,
  session_id TEXT REFERENCES sessions(id),
  started_at TIMESTAMP,
  ended_at TIMESTAMP,
  final_error_arcmin REAL,
  iterations INT,
  outcome TEXT,             -- "complete" | "aborted" | "failed"
  notes TEXT
);
```

WILMA's Image Library + Dashboard can display: *"Polar alignment quality: 0.7' (last performed 2 nights ago)"* as a small status chip. Gives users awareness of when re-alignment might help.

### 45.14 v0.1.0 — dedicated PA camera support

When v0.1.0 adds support for an iPolar / PoleMaster / dedicated PA camera attached via Alpaca:

- Polar Align workflow auto-detects an Alpaca camera tagged as "PolarAlignCamera" (separate from main imaging camera)
- Uses that camera's FOV / pixel scale for the math instead of binning the main camera
- Same UI, same loop, same math — just smaller frames and faster (no need for main camera autofocus or large transfer)
- User toggles in Settings: "Use dedicated PA camera if available"

### 45.15 What we're explicitly NOT doing

- Three-point polar alignment (TPPA) — dropped per §45.1
- Drift-alignment method (the historical hour-of-RA-drift technique) — too slow + obsolete given plate-solve approach
- Pre-canned "well-known star" alignments (Polaris reticle patterns) — manual workflows, replaced by automated continuous loop

---

## 46. Notifications system

In-app notifications only — no push, no email, no webhooks in v0.0.1 (field users often have no internet). Every meaningful server event becomes a notification. Per-event opt-in/out, quiet hours, four severity levels with distinct UX treatments.

### 46.1 Delivery model

- Server emits events via existing WebSocket connection (the `/api/v1/stream` channel from §9.4)
- WILMA caches events locally; the **Notification Feed** is the persistent in-app view
- If WILMA is disconnected when an event fires: event is queued in Pi's SQLite `notifications` table; delivered on reconnect (oldest first)
- No third-party services (no FCM, no APNs, no SendGrid). Everything is LAN-local.

### 46.2 Severity levels and UX treatment

| Severity | Toast in WILMA | Audio | Vibration (mobile) | Feed entry | Badge | Acknowledgment |
|---|---|---|---|---|---|---|
| **info** | none (feed only) | — | — | yes | — | passive — auto-marked-read on view |
| **warning** | auto-dismiss toast (5 s) | — | — | yes | +1 | passive |
| **critical** | sticky toast until tapped | one chime | short pulse | yes | +1 | tap to acknowledge |
| **urgent** | full-screen modal | looping alarm (per §35.5) | continuous | yes | +1 | explicit user action required (e.g., [Emergency Abort] or [Acknowledge]) |

Quiet hours suppress info + warning (queue silently). Critical + urgent ALWAYS deliver regardless (safety + equipment failure can't wait).

### 46.3 Event catalog

The complete list of server events that produce notifications, with default severity. All severities are user-overridable per-event (§46.6).

**Sequence lifecycle:**
| Event kind | Default severity |
|---|---|
| `sequence.started` | info |
| `sequence.complete` | info |
| `sequence.paused` | warning |
| `sequence.resumed` | info |
| `sequence.aborted_manual` | warning |
| `sequence.aborted_safety` | critical |
| `target.switched` | info |

**Equipment:**
| Event kind | Default severity |
|---|---|
| `equipment.connected` | info |
| `equipment.disconnected` | warning |
| `equipment.reconnected` | info |
| `equipment.fault` | critical |

**Safety (also handled by §35 alarm system):**
| Event kind | Default severity |
|---|---|
| `safety.unsafe` | urgent |
| `safety.alarm` | urgent |

**Autofocus / plate solve:**
| Event kind | Default severity |
|---|---|
| `autofocus.complete` | info |
| `autofocus.failed` | warning |
| `platesolve.complete` | info |
| `platesolve.failed` | warning |

**Frames + quality:**
| Event kind | Default severity |
|---|---|
| `frame.captured` | **suppressed by default** (would be noisy — opt-in only) |
| `frame.quality_drift` | warning (per §40.7 HFR drift detection) |

**Cooler + guider:**
| Event kind | Default severity |
|---|---|
| `cooler.target_reached` | info |
| `cooler.target_failed` | warning |
| `guider.started` | info |
| `guider.lost_star` | warning |
| `guider.recovered` | info |
| `guider.dither_complete` | info |

**Meridian flip:**
| Event kind | Default severity |
|---|---|
| `meridian_flip.imminent` | info (with user-configurable advance warning, default 15 min) |
| `meridian_flip.starting` | info |
| `meridian_flip.complete` | info |
| `meridian_flip.failed` | critical |

**Polar align:**
| Event kind | Default severity |
|---|---|
| `polar_align.complete` | info |
| `polar_align.failed` | warning |

**Recovery (post-crash, per §28):**
| Event kind | Default severity |
|---|---|
| `recovery.started` | warning |
| `recovery.complete` | info |
| `recovery.failed` | critical |

**Storage (per §29 + §43):**
| Event kind | Default severity |
|---|---|
| `storage.low_space` | warning (configurable threshold; default <5% free) |
| `storage.unmounted` | urgent |
| `storage.remounted` | info |
| `backup.complete` | info |
| `backup.failed` | warning |
| `backup_stream.paused` | warning (§44 — disk full on WILMA, etc.) |

**Time / location:**
| Event kind | Default severity |
|---|---|
| `time_sync.required` | warning |
| `time_sync.drift_detected` | warning (drift > 30 s mid-session per §31) |

**Server lifecycle:**
| Event kind | Default severity |
|---|---|
| `server.starting` | info |
| `server.shutdown_imminent` | warning |
| `update.available` | info (per §33) |
| `update.applied` | info |
| `update.failed` | warning |

**Environmental:**
| Event kind | Default severity |
|---|---|
| `dew_detected` | warning (per §42) |
| `network.weak_signal` | warning (WILMA-side connection quality) |

### 46.4 WebSocket event shape

```json
{
  "type": "notification",
  "ts": "2026-05-19T03:14:15.234Z",
  "payload": {
    "id": "ntf_8K2x...",
    "event_kind": "frame.quality_drift",
    "severity": "warning",
    "title": "Image quality degrading",
    "body": "Autofocus completed twice in 12 min but HFR keeps rising — possible clouds. Check sky conditions.",
    "related": {
      "session_id": "sess_abc",
      "frame_id": "frm_xyz",
      "equipment_id": null
    },
    "actions": [
      { "label": "Pause Sequence", "endpoint": "/api/v1/sequence/pause" },
      { "label": "Dismiss", "endpoint": null }
    ]
  }
}
```

Some events come with optional action buttons (e.g., a `frame.quality_drift` notification offers [Pause Sequence] right in the toast).

### 46.5 Persistent storage on the Pi

```sql
CREATE TABLE notifications (
  id TEXT PRIMARY KEY,
  ts TIMESTAMP NOT NULL,
  event_kind TEXT NOT NULL,
  severity TEXT NOT NULL,           -- "info"|"warning"|"critical"|"urgent"
  title TEXT,
  body TEXT,
  payload JSON,                     -- full event-specific data
  related_session_id TEXT,
  related_frame_id TEXT,
  related_equipment_id TEXT,
  acknowledged BOOLEAN DEFAULT 0,
  acknowledged_at TIMESTAMP,
  acknowledged_by TEXT              -- which WILMA acknowledged
);
```

Auto-prune: keep last 30 days of info + warning; keep all critical + urgent indefinitely.

### 46.6 User preferences

Settings → Notifications panel in WILMA:

**Per-event opt-in/out** — list of all event kinds (§46.3) with:
- Toggle: notify yes/no
- Severity override: dropdown (info / warning / critical / urgent / suppressed)
- e.g., user can promote `frame.quality_drift` to critical, demote `cooler.target_reached` to suppressed

**Quiet hours:**
- Toggle: enable quiet hours
- Time range: start time → end time (server's local TZ)
- During quiet hours:
  - info: suppressed (still goes to feed; no toast/audio)
  - warning: suppressed (still goes to feed; no toast/audio)
  - critical: delivered with reduced volume audio (50%)
  - urgent: delivered at full volume

**Defaults pre-filled** by the §37 wizard's notification screen (or implicitly with sensible defaults if user skips):

```json
{
  "quiet_hours": { "enabled": false, "start": "23:00", "end": "06:00" },
  "events": {
    "frame.captured": { "enabled": false },           // opt-in
    "target.switched": { "enabled": true, "severity": "info" },
    "sequence.complete": { "enabled": true, "severity": "info" },
    "safety.unsafe": { "enabled": true, "severity": "urgent" },
    "storage.unmounted": { "enabled": true, "severity": "urgent" },
    // ... rest at default per §46.3
  }
}
```

### 46.7 Notification feed UI (WILMA)

```
┌──────────────────────────────────────────────────────┐
│  Notifications                            ⚙  Mark all read  │
│  ──────────────────────────────────────────────────  │
│  🔴  Storage disconnected             3 min ago      │
│      USB drive disconnected. Sequence paused.        │
│      [Open Storage Settings]                          │
│                                                       │
│  🟠  Image quality degrading          12 min ago     │
│      Autofocus ran twice; HFR keeps rising —          │
│      possible clouds. [Pause Sequence]                │
│                                                       │
│  🔵  Target switched: M42 → NGC 6188  47 min ago     │
│                                                       │
│  🔵  Autofocus complete on L          1h 12m ago     │
│      HFR 1.42 → 1.18                                  │
│                                                       │
│  ... (older entries below, virtualized scroll)        │
└──────────────────────────────────────────────────────┘
```

- Severity icons: 🔵 info, 🟡 warning, 🟠 critical, 🔴 urgent
- Tap action button → executes the linked endpoint, marks acknowledged
- Tap row body → opens related session/frame if applicable
- Filter pills at top: [All] [Unread] [Critical+] [Last hour] [Last 24h]
- Persistent badge count in main app shell's notifications icon

### 46.8 API endpoints

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/v1/notifications` | List notifications (paginated, filterable by severity / acknowledged / event_kind / date range) |
| `GET` | `/api/v1/notifications/{id}` | Single notification full payload |
| `POST` | `/api/v1/notifications/{id}/acknowledge` | Mark acknowledged |
| `POST` | `/api/v1/notifications/acknowledge-all` | Bulk acknowledge by filter |
| `DELETE` | `/api/v1/notifications/{id}` | Remove (rare — typically auto-pruned) |
| `GET` | `/api/v1/notifications/preferences` | Get user's notification preferences |
| `PUT` | `/api/v1/notifications/preferences` | Update preferences |

### 46.9 v0.1.0 expansion paths

Out of scope for v0.0.1, queued in GAPS-ARA for future:

- **Push notifications** (FCM / APNs) — requires Firebase + Apple Developer accounts + privacy review
- **Email integration** — outbound SMTP from Pi (requires user to configure their mail server)
- **Discord / Slack webhooks** — POST notification payloads to user-configured webhook URLs
- **Generic webhook** — same shape, user-pasted URL
- **Notification scripting** — user-defined IFTTT-style "when X happens, do Y" rules (e.g., "when sequence.complete fires after 11pm, send IFTTT trigger to turn off observatory lights")

### 46.10 What "in-app only" means for unattended operation

The user said it best: "user may not have internet." In-app-only means:
- All notifications are deferred until WILMA reconnects
- Pi imaging continues regardless — the sequence doesn't pause just because WILMA isn't subscribing
- User wakes up, opens WILMA, sees the full feed of overnight events sorted by severity
- Critical events (USB unmount, safety abort) are still acted on by the Pi at the moment they happen (via safety policies §35); the notification just records "this happened and the policy fired"

---

## 47. Mosaic imaging (multi-panel)

Astrophotographers shoot multi-panel mosaics when a target is too large for one frame at their focal length — Andromeda at 2000mm, the Veil Nebula, Heart-and-Soul region, etc. NINA's Framing Assistant supports this; ARA preserves and modernizes the workflow with Aladin Lite integration + mosaic-aware tracking.

### 47.1 What mosaic mode does

User defines an N×M grid of overlapping panels centered on a target. Each panel becomes a sub-target with a computed RA/Dec offset. The sequencer captures all panels (light + calibration). Stitching happens later in post-processing (PixInsight, Siril, AstroPixelProcessor — ARA does not stitch).

### 47.2 Building a mosaic in WILMA's Framing Assistant

UI flow:

1. User searches for a target in Framing Assistant (Aladin Lite — §25.5)
2. Sets **Mosaic** mode: defines grid cols × rows (e.g., 3 × 2)
3. Sets overlap percentage (default **10%**; configurable 5-25%)
4. Optionally sets rotation angle (defaults to 0 if no rotator; else profile default)
5. Aladin Lite overlay renders the panel grid as colored rectangles on the sky map — user sees exactly which areas each panel covers, can drag the whole mosaic to recenter, can rotate
6. User confirms → mosaic saved as a single logical entity with N×M panels

### 47.3 Panel math (computed server-side)

Given:
- `f` = telescope focal length (mm)
- `w_sensor`, `h_sensor` = sensor dimensions (mm) — derived from camera's pixel size × pixel count
- `overlap` = fractional overlap (0.10 = 10%)
- `cols`, `rows` = grid dimensions
- `center_ra`, `center_dec`, `rotation` = mosaic anchor

Per-panel field of view: `panel_fov_x = atan(w_sensor / f)`, `panel_fov_y = atan(h_sensor / f)` (radians)

Inter-panel center offset: `step_x = panel_fov_x * (1 - overlap)`, `step_y = panel_fov_y * (1 - overlap)`

Panel `(c, r)` center (where `c ∈ [0, cols-1]`, `r ∈ [0, rows-1]`):
```
dx = (c - (cols-1)/2) * step_x
dy = (r - (rows-1)/2) * step_y

# Rotate (dx, dy) by mosaic rotation
dx', dy' = rotate(dx, dy, mosaic_rotation)

# Convert to spherical offset from center
panel_ra  = center_ra  + dx' / cos(center_dec)
panel_dec = center_dec + dy'
```

(Spherical-projection corrections for large mosaics near the poles are needed in practice; ARA uses standard tangent-plane projection per NINA's existing math.)

### 47.4 Panel scheduling — interleaved

ARA's sequencer runs panels in **interleaved order** by default (not sequential):

```
Instead of: panel(0,0) all-filters all-frames → panel(0,1) all-filters → ...
ARA does:   for filter in filters:
              for frame_index in range(per_panel_count):
                for (c,r) in panels:
                  slew to panel(c,r), capture 1 frame, dither
```

Why interleaved:
- All panels see roughly the same airmass, seeing, sky brightness at each stage
- Calibrating + stitching is more consistent
- Mid-session abort = each panel has roughly proportional data, not "panel 1 done, panel 6 zero"

Cost:
- More slews between panels (each transition = slew + plate-solve, ~30 s)
- Plate-solve verification per panel transition ensures position is correct

User can switch to **sequential** mode in the mosaic's settings if they prefer (e.g., very tight schedule, prefers to "finish" panels one at a time). Setting per-mosaic, not global.

### 47.5 Per-panel sub-target handling

Each panel is a target in the existing target system (§40) with:
- `name`: `"M31 Mosaic — panel 0,0"`, `"M31 Mosaic — panel 0,1"`, etc.
- `mosaic_id`: foreign key to the parent mosaic
- `panel_col`, `panel_row`: position within grid
- `center_ra`, `center_dec`, `rotation`: computed coordinates

Plate solving, autofocus, frame loop, dithering — all run per panel exactly as for any other target. The recovery flow (§28) works per panel.

### 47.6 Schema

```sql
CREATE TABLE mosaics (
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,            -- "M31 Mosaic", "Heart and Soul"
  center_ra REAL NOT NULL,
  center_dec REAL NOT NULL,
  rotation REAL DEFAULT 0,
  grid_cols INT NOT NULL,
  grid_rows INT NOT NULL,
  overlap_pct REAL DEFAULT 0.10,
  panel_fov_x_arcmin REAL,        -- computed at creation
  panel_fov_y_arcmin REAL,
  scheduling TEXT DEFAULT 'interleaved',  -- 'interleaved' | 'sequential'
  created_at TIMESTAMP,
  notes TEXT
);

CREATE TABLE mosaic_panels (
  mosaic_id TEXT REFERENCES mosaics(id),
  panel_col INT,
  panel_row INT,
  center_ra REAL,
  center_dec REAL,
  -- target row in `targets` table is created on demand; lookup by mosaic_id + panel_col + panel_row
  PRIMARY KEY (mosaic_id, panel_col, panel_row)
);
```

### 47.7 API endpoints

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/v1/mosaics` | Create a mosaic (body: name, center coords, grid, overlap, rotation) |
| `GET` | `/api/v1/mosaics` | List user's mosaics |
| `GET` | `/api/v1/mosaics/{id}` | Full state — grid, panels, completion % per panel |
| `GET` | `/api/v1/mosaics/{id}/panels` | Per-panel detail with frame counts per filter |
| `PATCH` | `/api/v1/mosaics/{id}` | Update overlap / rotation / scheduling (only if no frames captured yet) |
| `DELETE` | `/api/v1/mosaics/{id}` | Remove (panels remain as standalone targets if user wants) |
| `POST` | `/api/v1/mosaics/{id}/build-sequence` | Generate a sequence for the mosaic with the user's filter list + per-filter frame count |
| `POST` | `/api/v1/mosaics/{id}/resume` | Generate a sequence for **incomplete panels only** (per §47.9) |

### 47.8 Storage layout for mosaic captures

Under the session's captures dir:

```
captures/<session-id>/
└── M31-mosaic/
    ├── panel-0-0/
    │   ├── L/   (all light frames for panel 0,0 with filter L)
    │   ├── R/
    │   ├── G/
    │   └── B/
    ├── panel-0-1/
    ├── panel-1-0/
    └── panel-1-1/
```

FITS headers include `MOSAIC` (mosaic name), `PANEL` (`"0,0"`), `PANELRA`, `PANELDEC`, plus all the standard session metadata from §39.3. Post-processing tools can use `MOSAIC` + `PANEL` to identify and group frames.

### 47.9 Mosaic-aware Resume Target

The §40.6 "Resume Target" workflow extends naturally to mosaics:

1. User picks a mosaic from the Image Library
2. WILMA calls `POST /api/v1/mosaics/{id}/resume`
3. Server analyzes panel completion:
   - Per panel, per filter: count frames vs target frame count from the original sequence
   - "Complete" = at least N frames per filter (configurable per panel/filter via "per-filter target count")
   - "Incomplete" = below threshold
4. Server returns a sequence draft that contains ONLY incomplete panels' filter passes, interleaved
5. New frames write to the same `captures/<session-id>/M31-mosaic/panel-X-Y/<filter>/` structure with `MOSAIC`/`PANEL` headers — they roll up cleanly into the existing mosaic rather than creating a duplicate

This means **a mosaic project across years**: user can shoot 2 panels one night, 2 more the next clear week, 2 more 6 months later, and ARA tracks panel-completion across sessions. WILMA's mosaic detail view shows the grid colored by completion (red = 0 frames, yellow = partial, green = complete).

### 47.10 Image Library — mosaic rollup view

In §40 Image Library, mosaics appear as a top-level grouping (alongside individual targets):

```
▼ M31 Mosaic — 3×2 grid, 10% overlap
   17h 42min total · 6 panels, 5 complete, 1 partial
   [Visualize Grid]  [Resume Mosaic]  [Capture Matching Flats — All Panels]

   ┌────┬────┬────┐
   │ ✅ │ ✅ │ ✅ │   row 1 — fully complete
   ├────┼────┼────┤
   │ ✅ │ ✅ │ 🟡 │   row 0 — panel (2,0) needs 6 more L + 4 R
   └────┴────┴────┘

   [drill-in: per-panel frame lists]
```

The visualization is the actual sky layout (rotated per mosaic rotation) so user sees the spatial relationship of completed vs incomplete panels.

### 47.11 What ARA does NOT do (post-processing concerns)

- **Stitching** — combining the N×M panels into a single image. User does this in PixInsight (mosaic plugin), Siril, ICE, AstroPixelProcessor, etc. ARA's job is capture + metadata; stitching is the user's processing pipeline.
- **Star matching across panel overlap regions** — same as above
- **Color calibration across panels** — same

ARA's contribution: ensure every panel's FITS file has consistent metadata, panels are captured under similar conditions (interleaved scheduling), and the user can return for missing data years later.

### 47.12 Profile defaults (set in §37 wizard or post-hoc Settings)

- Default mosaic overlap: 10%
- Default scheduling: interleaved
- Default per-filter frame count per panel: inherit from user's standard sequence preferences
- Mosaic naming pattern: `<target> Mosaic` (e.g., "M31 Mosaic")

### 47.13 v0.1.0 expansion paths

- Adaptive panel sizing (variable focal length / FOV per panel — for super-wide mosaics combining wide-field and tighter panels)
- ARA-side stitching preview (low-res, just for sanity-check before user processes in PixInsight)
- Drift mosaicking (target drifts through FOV without slewing — for fast wide-field captures)

---

## 48. Auto-flats and dark library (sequence automation)

§39 covers the calibration philosophy + session-metadata-driven matching flats from past sessions. This section covers the *automation* layer — sequence step types that capture calibration during/after the imaging session without user intervention.

### 48.1 The "calibrate now or later" prompt (sequence start)

When a user starts a sequence, WILMA presents a one-time prompt asking whether to capture flats tonight:

```
┌──────────────────────────────────────────────────────┐
│  Capture calibration tonight?                        │
│  ──────────────────────────────────────────────────  │
│  Your sequence will use: L, R, G, B filters          │
│                                                       │
│  ○ Yes — flat panel at end of session                │
│  ○ Yes — sky flats at twilight                       │
│  ● No — capture them later                           │
│                                                       │
│  💡 ARA can recreate your exact equipment state —    │
│  focus per filter, rotator angle, sensor temp,       │
│  gain, offset — anytime from the Image Library.      │
│  Pick a past session → "Capture Matching Flats" and  │
│  the rig replays the geometry. (§39.5)               │
│                                                       │
│  ☑ Don't ask again — remember my preference          │
│                                                       │
│  [Start Sequence]                                     │
└──────────────────────────────────────────────────────┘
```

Three choices for flats:
- **Yes — flat panel at end of session** → server appends a `FlatPanelFlats` instruction to the sequence after the last imaging instruction
- **Yes — sky flats at twilight** → server appends a `SkyFlats` instruction triggered by morning astronomical twilight
- **No — capture them later** → no auto-append; user understands they can run §39.5 anytime to recreate

This prompt surfaces the §39.5 superpower at exactly the right moment — when the user is deciding whether to spend extra rig-time tonight on calibration or defer with confidence.

### 48.2 Preference persistence

User can opt out of the prompt with "Don't ask again — remember my preference":
- Profile gains a `calibration_capture_default` setting: `"ask" | "panel_at_end" | "sky_at_twilight" | "never"`
- Default = `"ask"` (the prompt is shown each sequence)
- Settings → Calibration in WILMA lets user change later

If "never" is set and user wants to capture occasionally, they manually add a `FlatPanelFlats` or `SkyFlats` instruction to the sequence editor.

### 48.3 Auto-flat step — `FlatPanelFlats` (preserved from NINA)

When the sequence reaches the `FlatPanelFlats` instruction (typically appended at end-of-night):

1. Server queries the sequence's prior instructions to detect which filters were used (live, from session DB)
2. For each filter:
   - Slew mount to the flat-friendly position (default: park position, or user-configured flat target)
   - Switch filter wheel to current filter
   - Move focuser to that filter's focus offset (so flats match imaging conditions)
   - Hold rotator at current angle (CAA)
   - Turn on flat panel, set brightness, wait for stabilization
   - **Auto-exposure**: capture short test exposure, measure median ADU, adjust panel brightness OR exposure time until target ADU is reached (default ~30000 for 16-bit cameras = ~45% full scale)
   - Capture N flats (default 30)
3. After all filters: turn off panel, park mount (optionally), log completion notification

Inherits NINA's existing implementation; ARA preserves verbatim.

### 48.4 Sky flats variant — `SkyFlats` (preserved from NINA)

When user picks sky flats:

- Sequencer waits until morning astronomical twilight starts (or evening if running backwards)
- Slews to zenith (or user-configured sky-flat target — typically east in evening, west in morning to avoid the brightening/darkening direction)
- For each filter:
  - Auto-exposure to target ADU (sky brightness changes rapidly during twilight, so exposure time must adapt frame-to-frame)
  - Capture N flats per filter; adjust exposure as sky brightens/darkens
  - Stop if sky becomes too bright (overexposure) or too dim (insufficient stars-to-skybackground)

Inherits NINA's implementation.

### 48.5 Dark library — manual user-initiated, not prompted

Darks are NOT included in the sequence-start prompt for v0.0.1 because:
- Darks don't match a specific session (they match camera + gain + temp + exposure — much more reusable)
- Building a dark library is typically a multi-hour overnight task, NOT a "tack onto tonight's imaging" thing
- Users typically build darks on cloudy/moony/full-moon nights when DSO imaging is impossible

User builds a dark library by:
1. Creating a new sequence in WILMA
2. Adding a `DarkLibraryInstruction` (NINA's existing instruction type, preserved)
3. Defining the matrix: list of (exposure, gain, temp) tuples + frames-per-combination
4. Running the sequence (typically overnight, unattended)

`DarkLibraryInstruction` semantics (preserved from NINA):
- For each (exposure, gain, temp) combination:
  - Set cooler target temp, wait for stabilization
  - Capture N frames with the camera's shutter closed (or in front of a dark cap)
  - Move to next combination
- Total runtime: hours to a full overnight, depending on matrix size

Bundled `dark-library.json` template (§38.7) gives users a starting point: typical CMOS combinations (30s × 5 gains × 3 temps × 50 frames = ~7,500 darks, ~7 hours).

### 48.6 Bias library

Bias frames are short-as-possible exposures with shutter closed — captures readout pattern. NINA's `TakeBiasExposures` instruction preserved. Typically:
- One bias library per camera + gain combination
- 100-500 frames per combo (stacks well, easy to capture in 1-2 minutes)
- Updated when user changes gain settings or replaces camera

User initiates manually; not part of the sequence-start prompt.

### 48.7 Calibration preference in profile schema

Profile JSON gains a `calibration` block (inside the existing safety/preferences area):

```json
{
  "calibration": {
    "capture_default": "ask",           // "ask" | "panel_at_end" | "sky_at_twilight" | "never"
    "flat_panel": {
      "target_adu": 30000,              // half of 16-bit full scale
      "target_adu_tolerance_pct": 5,
      "frames_per_filter": 30,
      "post_flat_park_mount": true
    },
    "sky_flat": {
      "target_adu": 25000,
      "frames_per_filter": 20,
      "sky_target_azimuth": 90,          // east in morning, west in evening
      "sky_target_altitude": 75,
      "stop_at_max_adu": 50000,         // bail if oversaturated
      "stop_at_min_adu": 5000           // bail if too dark
    },
    "dark_library": {
      "default_frames_per_combination": 50,
      "default_temp_tolerance_c": 0.5
    }
  }
}
```

### 48.8 Settings → Calibration panel (WILMA)

Mirrors the schema above with editable fields, plus:
- "What ARA will do at sequence start" preview ("Will ask each time" / "Will capture sky flats automatically" / etc.)
- Link to §39.5 "Capture Matching Flats" workflow in Image Library

### 48.9 v0.1.0 expansion paths

- **Scheduled dark library** — "build dark library every Sunday night if no sequence planned" — runs automatically when imaging is impossible
- **Smart dark management** — server identifies when dark library is stale (camera replaced, gain settings changed since last darks captured) and prompts user
- **Bias automation** — same model as flats prompt
- **Sky flat optimal target tracking** — server picks the best position by computing brightness gradient direction at the current twilight time (eastern sky brightens in dawn, western in dusk)

---

## 49. API documentation serving

ARA Core serves interactive Swagger UI documentation from its OpenAPI spec. Open access (no token) to match ASCOM Alpaca's convention.

### 49.1 Tool choice — Swagger UI

ARA uses **Swagger UI v5.x** for the same reason ASCOM Alpaca does ([ascom-standards.org/api/](https://ascom-standards.org/api/)): it's the de-facto standard for OpenAPI-spec docs, ecosystem-familiar to anyone working with Alpaca APIs, and ASP.NET Core has first-class support via `Swashbuckle.AspNetCore` (already in §8.1's csproj).

Source-of-truth spec lives at `OpenAstroAra.Server/openapi.yaml` (per §9 + §38). Swagger UI renders it interactively.

### 49.2 Endpoints

| Path | Returns |
|---|---|
| `/api/v1/docs` | Swagger UI HTML page (interactive API explorer) |
| `/api/v1/openapi.yaml` | Raw OpenAPI 3.1 spec (YAML) — for tools that consume the spec directly |
| `/api/v1/openapi.json` | Same spec, JSON format — Swagger UI fetches this |

All three are **open** — no token required. The auth protects state-mutating operations, not documentation.

### 49.3 Swagger UI styling

Match ASCOM's customizations for visual consistency:
- Hide the "base URL" input field (it's always the host the user is browsing)
- Hide the Swagger file description text
- Custom CSS pulled in via `Swashbuckle.AspNetCore`'s `InjectStylesheet` option
- Custom title: "OpenAstro Ara API"

```csharp
app.UseSwaggerUI(c => {
    c.SwaggerEndpoint("/api/v1/openapi.json", "OpenAstro Ara v0.0.1");
    c.RoutePrefix = "api/v1/docs";
    c.DocumentTitle = "OpenAstro Ara API";
    c.InjectStylesheet("/swagger-custom.css");
    c.DisplayRequestDuration();
    c.EnableFilter();
});
```

### 49.4 What the spec contains

Per §9 endpoint groups + later additions:

- Server (info, handshake, time-sync, storage, backup-stream, update)
- Equipment (per device type per Alpaca interface)
- Sequence (CRUD + run + import)
- Image (frames + previews + FITS + thumbnails)
- Session (history + matching-flats)
- Polar alignment
- Notifications
- Mosaic
- Calibration library
- Profiles

All request/response shapes typed via OpenAPI components. Authentication scheme declared (`X-OpenAstroAra-Token` header) so Swagger UI's "Authorize" button works for users who want to test authenticated endpoints from the docs page.

### 49.5 "Try It Out" with token from the docs page

Swagger UI's built-in "Authorize" lets users paste their token once; subsequent "Try It Out" requests include it automatically. Useful for:
- Debugging during development
- Power users exploring the API
- Plugin authors testing endpoints before integrating (when plugin SDK ships in v0.1.0)

The token is stored in browser session storage (not persisted across browser restarts) — secure-by-default.

### 49.6 Where Swagger UI is reachable

Same port as the API (default 5400). On a Pi at `pi-observatory.local`:
- `http://pi-observatory.local:5400/api/v1/docs` — interactive docs
- `http://pi-observatory.local:5400/api/v1/openapi.yaml` — spec

WILMA's About panel can link to `<server>/api/v1/docs` so users discover it.

### 49.7 v0.1.0 expansion

- Generated SDK packages from the OpenAPI spec for popular languages (Python, JavaScript, Go) — useful for plugin authors + community integrations
- Versioned doc browser (current v0.0.1, future v0.1.0, etc.) — Swagger UI supports multi-spec selection
