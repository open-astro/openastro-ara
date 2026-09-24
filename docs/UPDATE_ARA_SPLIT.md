# ARA split updater

Location: `ara-unified-update/scripts`.

Problem: the old `update-observatory.sh` built the local client and stopped/restarted the SBC in one run. The SBC hotspot has no upstream internet, and a full RK3568 image flash cannot run over Wi-Fi.

Fix: use one command per device. Both commands use the existing local Git checkouts and transfer the SBC payload by SSH/SCP. No SBC internet is needed in `--offline` mode.

## Local PC client

Run as the normal desktop user. Never use `sudo`; it changes `HOME` and hides Flutter.

```bash
cd /home/sam/openastro/ara-unified-update
export FLUTTER=/path/to/pinned/flutter/bin/flutter
bash scripts/update-ara-client.sh --offline --source-root /home/sam/openastro --plan
bash scripts/update-ara-client.sh --offline --source-root /home/sam/openastro
```

The command snapshots ARA, runs Flutter analyze/tests, builds the Linux release bundle, then atomically installs it at `~/.local/opt/openastroara` and links `~/.local/bin/openastroara`. It does not contact the SBC.

To build the current local ARA worktree, including uncommitted files and local
commits not pushed upstream, add `--local`:

```bash
bash scripts/update-ara-client.sh --local --offline \
  --source-root /home/sam/openastro
```

`--local` snapshots only the ARA worktree. Generated Flutter/.NET output is
excluded. Other repositories keep their normal source selection.

To test the planning fix worktree:

```bash
bash scripts/update-ara-client.sh --offline --ara-url /home/sam/openastro/ara-planning-fixes \
  --ara-ref fix/planning-interaction
```

Offline client builds require the pinned Flutter SDK and all Dart packages in the local pub cache. The updater resolves packages once with `flutter pub get --offline`, then passes `--no-pub` to analyze, test, and build so Flutter does not contact pub.dev for advisories. Set `OPENASTRO_UPDATE_DIR=/tmp/openastro-update` if the normal cache filesystem is not writable.

After installing the planning branch, seed DSS2 photographs before going to the
SBC hotspot. With normal Internet access, launch the client, open Planning,
enable DSS2, select each target, and zoom until its photograph appears. The
client stores requested tiles in the local application-support
`stellarium-dss2` cache. Close and relaunch after joining the SBC hotspot; the
cached tiles and framing overlays work without Internet. Never-viewed tiles
still need an online seed.

## SBC services over the OpenAstro hotspot

Connect the PC to the SBC Wi-Fi, then run as the normal desktop user:

```bash
cd /home/sam/openastro/ara-unified-update
export OPENASTRO_PASSWORD=astro
bash scripts/update-ara-sbc.sh --offline --source-root /home/sam/openastro --plan
bash scripts/update-ara-sbc.sh --offline --source-root /home/sam/openastro
unset OPENASTRO_PASSWORD
```

Defaults: `astro@172.24.1.1`, two native build jobs. The command builds the self-contained ARM64 ARA server on the PC, snapshots AlpacaBridge and OpenAstro Guider, creates one compressed payload, verifies its SHA-256, copies it over Wi-Fi, builds native Debian packages on the SBC, preserves service/state backups, installs, and checks Bridge port 6800, Guider RPC port 4400, and ARA `/healthz` port 5555. It does not build Flutter.

Use draft fixes together:

```bash
bash scripts/update-ara-sbc.sh --offline --source-root /home/sam/openastro \
  --ara-url /home/sam/openastro/ara-planning-fixes --ara-ref fix/planning-interaction \
  --guider-url /home/sam/openastro/ara-guider-recovery --guider-ref fix/guider-service-recovery
```

The SBC must already have the compiler, CMake, Debian packaging tools, and Bridge/Guider development libraries. Offline mode skips APT and all downloads; missing packages cause a build error. Install them once while the SBC has internet, use a second interface/USB tether, or pre-stage Debian packages. The script refuses to start when `/home` has less than 4 GiB free.

The payload and recovery backup remain under `/home/astro/.cache/openastro-update/run-*`. Remove only failed or obsolete runs after checking backups. If the SBC reports zero free space, first inspect:

```bash
ssh astro@172.24.1.1 'df -h /home; du -xhd1 /home/astro/.cache /home/astro 2>/dev/null | sort -h | tail -20'
```

## RK-flashtool boundary

`rk-flashtool/scripts/flash-rootfs` and `flash-all` write RK3568 eMMC through USB Loader/Maskrom. They are recovery tools; they do not flash raw partitions through the SBC hotspot. `flash-rootfs` can use an explicit cached `.img`/`.img.gz` without internet, but needs root, a USB connection, and several GiB of temporary space. Use it only for a full rootfs recovery. Use `update-ara-sbc.sh` for normal ARA, AlpacaBridge, and Guider updates over Wi-Fi.

## Checks

```bash
bash -n scripts/update-observatory.sh scripts/update-ara-client.sh scripts/update-ara-sbc.sh
python3 -m unittest discover -s scripts/tests -p 'test_update_observatory.py'
```
