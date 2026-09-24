# Rebuild and update the observatory

Split commands are now available: [`update-ara-client.sh`](../scripts/update-ara-client.sh) updates only the local Linux client; [`update-ara-sbc.sh`](../scripts/update-ara-sbc.sh) updates only the ARM64 SBC over SSH/Wi-Fi. Use [`UPDATE_ARA_SPLIT.md`](UPDATE_ARA_SPLIT.md) for the normal workflow. The combined command below remains for compatibility.

Run `scripts/update-observatory.sh` on the Linux computer displaying ARA. It builds/tests the local Linux Flutter client and publishes the self-contained ARM64 server locally, then builds AlpacaBridge and OpenAstro Guider natively on the SBC. No .NET runtime is required on the SBC. Default SSH destination: `astro@172.24.1.1`.

This updates an **existing** installation. It recognizes both manual `/opt/openastroara/server` and packaged `/opt/openastroara` server layouts. Existing service users, local systemd configuration, profile databases, captures, and solver catalog are preserved. Server binaries are deployed directly; this does not change the installed ARA Debian package version. A later APT upgrade can replace them.

## Run

Install the local build prerequisites from [RUNNING.md](RUNNING.md), including the .NET SDK selected by `global.json` and Flutter version in `client/openastroara_client/.flutter-version`. Point `DOTNET` and `FLUTTER` at those executables if not on PATH. SSH, SCP, rsync, Python 3, tar, curl, and file are also required. Password authentication additionally needs `sshpass`.

```bash
export DOTNET=/path/to/dotnet
export FLUTTER=/path/to/flutter/bin/flutter
bash scripts/update-observatory.sh --plan
read -rsp 'SBC SSH/sudo password: ' OPENASTRO_PASSWORD
printf '\n'
export OPENASTRO_PASSWORD
bash scripts/update-observatory.sh --build-only
# When equipment is idle, build/test and install:
bash scripts/update-observatory.sh
unset OPENASTRO_PASSWORD
```

Password is never stored in source or passed in command arguments. SSH key plus passwordless sudo also works without the variable. Host keys use `accept-new`: new hosts are enrolled; changed keys fail. Existing local SSH configuration still applies.

Run as the normal client user. Do not invoke this script with `sudo`: it changes `HOME`, can hide the required Flutter/.NET tools, and installs the client under `/root`. Interactive runs prompt for `OPENASTRO_PASSWORD` when no SSH key path is configured; press Enter at that prompt only when key authentication and passwordless SBC sudo are ready.

Online mode needs SBC internet access, ARM64 Debian with package build dependencies available (Trixie for libgpiod 2), sudo permission, and enough disk/memory for three source trees, packages, and backups. The updater refuses to stage when `/home` has less than 4 GiB free. Default compilation parallelism is 2; change with `--jobs`. Dependency installation can update build/runtime libraries even under `--build-only`; no application deployment or intentional service stop occurs in that mode.

### SBC Wi-Fi has no upstream internet

The default run downloads Git sources on the client and APT packages on the SBC. It fails when the SBC hotspot has no upstream route. Use offline mode after staging all three repositories and build caches on the client, and installing the SBC build dependencies once while online:

```bash
bash scripts/update-observatory.sh --offline --source-root /home/sam/openastro --plan
bash scripts/update-observatory.sh --offline --source-root /home/sam/openastro --build-only
bash scripts/update-observatory.sh --offline --source-root /home/sam/openastro
```

To stage the draft fixes before merge, use their local worktrees:

```bash
bash scripts/update-observatory.sh --offline --source-root /home/sam/openastro \
  --ara-url /home/sam/openastro/ara-planning-fixes --ara-ref fix/planning-interaction \
  --guider-url /home/sam/openastro/ara-guider-recovery --guider-ref fix/guider-service-recovery
```

`--offline` uses local Git checkouts, `flutter pub get --offline`, cached .NET packages, and installed SBC libraries. The client build then passes `--no-pub` to Flutter analyze/test/build, preventing pub.dev advisory lookups. It makes no Git, APT, NuGet, or pub downloads. Missing cache or dependency causes a clear build failure. `--plan` always needs no network. `--source-root` expects `openastro-ara`, `AlpacaBridge`, and `openastro-guider` below that directory; a local path passed with `--ara-url`, `--bridge-url`, or `--guider-url` overrides that checkout. For a fresh SBC, use a second network interface, USB tether, or pre-stage Debian packages and SDK caches first.

Use `--local` to snapshot the current ARA worktree instead of cloning its
committed Git state. This includes uncommitted files and local commits not yet
pushed. It is intended for local client testing; generated build/cache output
is excluded. Other repositories keep their normal source selection.

Do not run install during an observing session. The script stops ARA, guider, and bridge; starting the guider can reconnect equipment. It does not slew or request exposures itself. It does not claim an atomic transaction across hosts or packages.

## Source selection

Each run uses fresh private checkouts and records exact commits in `revisions.txt`. Existing developer checkouts are untouched. Upstream HEAD is default; it includes fixes only after merge. Test a specific PR with, for example:

```bash
bash scripts/update-observatory.sh --ara-ref refs/pull/NUMBER/head
```

`--ara-url`, `--bridge-url`, `--guider-url` and corresponding `--*-ref` options accept forks and commit IDs. An ARA PR contains only its branch's fixes; combine independent fixes in a reviewed integration branch before deployment. Repositories with submodules fail explicitly until recursive snapshot support is added.

## Solver and catalog

Existing `/opt/astap` is preserved. ASTAP and its star database are independently versioned third-party artifacts; this script does not silently download an unpinned latest binary or replace gigabytes of catalog data.

To update them in the same run, prepare a directory containing an executable ARM64 `astap_cli` plus the chosen database beside it, verify their provenance/checksums, then pass `--astap-dir /path/to/prepared-directory`. The script checks executable architecture, backs up `/opt/astap`, and overlays the directory. Existing profile solver path remains unchanged. See [DEPLOY.md](DEPLOY.md) and the local ARA setup runbook for solver configuration.

## Recovery and results

Local run artifacts: `${OPENASTRO_UPDATE_DIR:-$HOME/.cache/openastro-update}/run-*`. SBC artifacts/backups: `/home/USER/.cache/openastro-update/run-*`. Directories are private. Keep sufficient free space; the script does not delete previous runs. If preflight reports no space, inspect first:

```bash
ssh astro@172.24.1.1 'df -h /home; du -xhd1 /home/astro/.cache /home/astro 2>/dev/null | sort -h | tail -20'
```

Delete only failed or obsolete update runs after checking backup contents. When safe, clear package and old journal data with `sudo apt-get clean` and `sudo journalctl --vacuum-time=7d` on the SBC.

All application builds/tests must finish before service stops. Package maintainer starts are suppressed during installation. Failure after service stops leaves services stopped; if server binaries were replaced, the old binaries are restored. Package changes and database migrations are **not** automatically rolled back. Inspect retained service definitions, package inventory, state backup, and journal before recovery. Restore matching databases and binaries together; never start an old server against an incompatible migrated database. The state backup excludes `/frames/` and `/logs/`, preserving captures in place.

Success requires bridge management HTTP, guider `get_app_state` RPC, ARA `/healthz`, and all three services active. Local client installation occurs only after those pass. Previous local bundle remains as `openastroara.previous-run-*`; launch `~/.local/bin/openastroara`. Relaunch an already-running client yourself. Health checks do not prove mount/camera connectivity or a plate solve; validate those in Setup after update.

Existing `/usr/sbin/policy-rc.d` causes installation to stop before touching services. Unsupported/custom server executable paths also fail rather than overwrite an unknown layout. A failed SSH connection leaves the remote deployment unverified; retained artifacts support diagnosis.

## Automated script checks

```bash
bash -n scripts/update-observatory.sh
python3 -m unittest discover -s scripts/tests -p 'test_update_observatory.py'
```
