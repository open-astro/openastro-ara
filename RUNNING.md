# Running OpenAstro Ara from source

This guide covers building and running the two pieces of ARA — the headless daemon
(`OpenAstroAra.Server`) and the Flutter desktop client (`client/openastroara_client`) —
from a source checkout on **Linux, macOS, and Windows**, for development and testing.

For installing a released build on a Raspberry Pi (the production deployment target),
see [`DEPLOY.md`](DEPLOY.md) instead.

---

## Prerequisites (all platforms)

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | **10.0.100+** | Pinned in `global.json` (`rollForward: latestFeature`) |
| Flutter | **3.44.x** (stable) | Pinned in `client/openastroara_client/.flutter-version`; the client's `pubspec.yaml` requires `>=3.44.0 <3.45.0` |
| CFITSIO | any recent | Native library the daemon loads at runtime to write FITS files — per-OS install below |

---

## 1. Run the daemon

The daemon is the same on every OS:

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project OpenAstroAra.Server
curl http://localhost:5555/healthz   # → "ok"
```

- It is a **headless JSON API** — browsing `http://localhost:5555` shows nothing;
  that's correct. A Scalar API browser is served at `/api/reference`.
- Default port **5555** (override with `OPENASTROARA_PORT`).
- Profile/state directory resolution: `OPENASTROARA_PROFILE_DIR` env var →
  `/var/lib/openastroara` (if it exists — the systemd install) →
  `~/.local/share/openastroara`. For a throwaway dev profile:
  `OPENASTROARA_PROFILE_DIR=/tmp/ara dotnet run --project OpenAstroAra.Server`.
- No auth — the daemon assumes a trusted LAN (playbook §67).
- The solution builds with `TreatWarningsAsErrors=true` + `AnalysisMode=All`. If your
  local SDK surfaces analyzer warnings that block `dotnet run`, append
  `-p:TreatWarningsAsErrors=false` — a run-time-only relaxation that touches no files.

### CFITSIO per OS

The build doesn't need CFITSIO, but the capture path resolves it at runtime
(`DllNotFoundException: 'cfitsio'` means it's missing).

- **Linux:** `sudo apt-get install libcfitsio-dev` (Debian/Ubuntu; pulls the runtime lib).
- **macOS:** `brew install cfitsio`. The `CopyLibCfitsioMacOS` post-build target in
  `OpenAstroAra.Server.csproj` copies the dylib into the app's native runtime dir on
  every macOS build — needed because the .NET loader doesn't search `/opt/homebrew/lib`
  and SIP strips `DYLD_*` vars. Just build after brew-installing and it works.
- **Windows:** untested — the deployment target is ARM64 Linux and daemon development
  happens on Linux/macOS. The client runs fine on Windows (below); if you want the
  daemon on the same machine, run it under WSL2 using the Linux steps, or put
  `cfitsio.dll` (e.g. `vcpkg install cfitsio`) next to the built server binary and
  report how it goes.

---

## 2. Run the client

Common first step on every OS:

```bash
cd client/openastroara_client
flutter pub get
```

### Linux

Install the desktop toolchain + the libraries the runner links against (same list CI uses):

```bash
sudo apt-get install -y clang cmake ninja-build pkg-config \
  libgtk-3-dev libwebkit2gtk-4.1-dev \
  libsecret-1-dev libjsoncpp-dev
flutter run -d linux            # dev
flutter build linux --release   # ships from build/linux/x64/release/bundle/
```

### macOS

Full Xcode (not just Command Line Tools) + CocoaPods (`brew install cocoapods`).
First-time Xcode setup needs both:

```bash
sudo xcodebuild -license          # interactive; type "agree"
sudo xcodebuild -runFirstLaunch   # installs CoreSimulator.framework
```

Then:

```bash
flutter run -d macos   # foreground only — a backgrounded `flutter run` exits and kills the app
```

For a session that outlives the terminal, build once and launch the `.app` detached:

```bash
flutter build macos --debug
open build/macos/Build/Products/Debug/openastroara.app
```

**macOS debug-build gotcha:** mDNS auto-discovery fails in debug builds (no multicast
entitlement — `No route to host` on `0.0.0.0:5353`), so the discovered-servers list
stays empty. Use the first-run screen's manual entry: host `localhost`, port `5555`.

### Windows

Install Visual Studio 2022 with the **Desktop development with C++** workload
(the standard Flutter Windows requirement — `flutter doctor` verifies it). Then:

```bash
flutter run -d windows            # dev
flutter build windows --release   # ships from build/windows/x64/runner/Release/
```

---

## 3. Connect the client to the daemon

On Linux and Windows the client should auto-discover a daemon on the same LAN via
mDNS (`_openastroara._tcp.local`). Anywhere discovery doesn't fire (macOS debug
builds, VPNs, multicast-hostile networks), enter the host and port manually on the
first-run screen.

No astro hardware on hand? The daemon speaks **ASCOM Alpaca only**, so point it at
the [ASCOM Alpaca OmniSim](https://github.com/ASCOMInitiative/ASCOM.Alpaca.Simulators)
simulators — the same devices the integration tests use
(`scripts/get-alpaca-simulators.sh` fetches the pinned build CI runs against).
