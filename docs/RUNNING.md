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
| Flutter | **3.44.x** (stable) | Pinned in `client/openastroara_client/.flutter-version`; the client's `pubspec.yaml` requires `>=3.44.0 <3.45.0` — a newer Flutter fails `flutter pub get` |
| CFITSIO | any recent | Native library the daemon loads at runtime to write FITS files — per-OS install below |

### Installing Flutter at the exact pinned version

The simplest way to get a version-exact Flutter on any OS is a git clone of the
tagged release (the official installers track "latest stable", which will drift
past the pin):

```bash
# Linux / macOS
git clone https://github.com/flutter/flutter.git -b 3.44.0 ~/development/flutter
echo 'export PATH="$HOME/development/flutter/bin:$PATH"' >> ~/.bashrc   # or ~/.zshrc
```

```powershell
# Windows — use a short path WITHOUT spaces (not under Program Files)
git clone https://github.com/flutter/flutter.git -b 3.44.0 C:\development\flutter
# Then add C:\development\flutter\bin to your user Path:
# Start → "environment variables" → Environment Variables… → Path → Edit → New
```

**Open a new terminal after editing PATH** — it only refreshes in new sessions
(`flutter: command not found` almost always means PATH wasn't set or the terminal
wasn't reopened). The first `flutter --version` downloads the bundled Dart SDK;
give it a minute. It must report `Flutter 3.44.0 … channel stable`. Then run
`flutter doctor` and fix anything red for your platform's desktop toolchain.

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
- **macOS (Apple Silicon):** `brew install cfitsio`. The `CopyLibCfitsioMacOS`
  post-build target in `OpenAstroAra.Server.csproj` copies the dylib into the app's
  native runtime dir on every macOS build — needed because the .NET loader doesn't
  search `/opt/homebrew/lib` and SIP strips `DYLD_*` vars. Just build after
  brew-installing and it works.
- **macOS (Intel):** the auto-copy target is Arm64-gated, so copy the dylib by hand
  after `brew install cfitsio` (Homebrew lives at `/usr/local` on Intel, and the
  loader probes the `osx-x64` runtime dir):

  ```bash
  BIN=OpenAstroAra.Server/bin/Debug/net10.0/runtimes/osx-x64/native   # or bin/Release/…
  mkdir -p "$BIN"
  cp -L /usr/local/lib/libcfitsio.dylib "$BIN/libcfitsio.dylib"
  ```

  Re-copy after a clean build (a clean wipes `bin/`).
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
sudo apt-get install -y git curl unzip xz-utils \
  clang cmake ninja-build pkg-config \
  libgtk-3-dev libwebkit2gtk-4.1-dev \
  libsecret-1-dev libjsoncpp-dev
flutter config --enable-linux-desktop
flutter run -d linux            # dev
flutter build linux --release   # ships from build/linux/x64/release/bundle/
```

- `libwebkit2gtk-4.1-dev` is the **WebKitGTK engine the §36 planetarium renders in**
  (the client uses each platform's native webview — there is no bundled Chromium/CEF).
- `libsecret-1-dev` + `libjsoncpp-dev` are required by the `flutter_secure_storage_linux`
  plugin — without them the build fails at CMake configure.
- **Wayland sessions:** Flutter's Linux GL path wants X11. If you hit
  `Failed to create platform view rendering surface` (or a blank window), you're on a
  Wayland session — log into an X11 session or prefix runs with
  `GDK_BACKEND=x11 flutter run -d linux`.
- After launching, open the Planning tab and check the planetarium actually draws
  stars/atmosphere — a blank/black sky means a WebGL2 gap in your WebKitGTK build.

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
(the standard Flutter Windows requirement — `flutter doctor` verifies it; the lighter
"Build Tools for Visual Studio 2022" with the same workload also works). Then:

```powershell
flutter config --enable-windows-desktop
flutter run -d windows            # dev
flutter build windows --release   # ships from build/windows/x64/runner/Release/
```

The planetarium renders through the **Edge WebView2** runtime, preinstalled on
Windows 11. If a run complains it's missing (some Windows 10 / stripped images),
install the "Evergreen Standalone Installer" from
<https://developer.microsoft.com/microsoft-edge/webview2/>.

---

## 3. Connect the client to the daemon

On Linux and Windows the client should auto-discover a daemon on the same LAN via
mDNS (`_openastroara._tcp.local`). Anywhere discovery doesn't fire (macOS debug
builds, VPNs, multicast-hostile networks), enter the host and port manually on the
first-run screen.

### Same machine

Manual entry: host `localhost`, port `5555`.

### Daemon on a different machine (e.g. daemon on a Mac, client on Linux/Windows)

Both machines must be on the same LAN. Use **manual entry with the daemon machine's
IP address** — prefer it over tapping a discovered row, because discovery connects
by the daemon host's `.local` mDNS name, which Linux typically can't resolve without
extra setup (`Temporary failure in name resolution`).

1. Find the daemon machine's LAN IP (macOS: `ipconfig getifaddr en0`; Linux:
   `hostname -I`; Windows: `ipconfig`).
2. Sanity-check reachability from the *client* machine first:

   ```bash
   curl http://<daemon-ip>:5555/api/v1/server/info   # must return JSON
   ```

   No JSON = a network/firewall issue (or the daemon IP changed), not a client problem.
3. Enter that IP + port `5555` on the first-run screen and save.

Equipment chips should turn green within a few seconds of connecting.

No astro hardware on hand? The daemon speaks **ASCOM Alpaca only**, so point it at
the [ASCOM Alpaca OmniSim](https://github.com/ASCOMInitiative/ASCOM.Alpaca.Simulators)
simulators — the same devices the integration tests use
(`scripts/get-alpaca-simulators.sh` fetches the pinned build CI runs against).

---

## 4. Troubleshooting (client builds)

- **`flutter pub get` fails with a version error** → wrong Flutter. It must be 3.44.x
  (`flutter --version`); reinstall via the version-exact git clone above.
- **`flutter: command not found` / `not recognized`** → Flutter's `bin` isn't on PATH,
  or the terminal wasn't reopened after editing PATH.
- **Linux build fails copying to `/usr/local/…` with "Permission denied"** → a
  *previous* build aborted mid-configure (usually a missing `-dev` dependency),
  leaving a stale CMake cache that pinned the install path. Fix: `flutter clean`,
  then rebuild. **Never reach for `sudo`** — it only entrenches the bad cache.
- **CMake "required packages were not found"** names a **pkg-config module** (e.g.
  `libsecret-1>=0.18.4`). On Debian/Ubuntu the satisfying `.pc` file ships in the
  matching **`-dev`** package: `sudo apt install <name>-dev` (here `libsecret-1-dev`).
- **A run dies on a missing `lib*.so`** →
  `sudo apt install apt-file && sudo apt-file update && apt-file search libNAME.so`
  tells you which package provides it.
- **GL context / "Failed to create platform view rendering surface" (Linux)** →
  Wayland session; use X11 or `GDK_BACKEND=x11` (see the Linux section above).
- **Planetarium shows a blank/black sky** → the platform webview lacks WebGL2
  (old WebKitGTK, or missing WebView2 runtime on Windows). Stars + atmosphere
  drawing = the webview path is healthy.
