# Deploying OpenAstro Ara

This guide covers running `OpenAstroAra.Server` on a Raspberry Pi. Reference platform is **Raspberry Pi 4 (4 GB+) or Pi 5** on **64-bit Debian Trixie** or newer. Other ARM64 SBCs (Orange Pi 5, Rock Pi, etc.) running Trixie arm64 work best-effort.

The Flutter client (`OpenAstroAra.Client`) runs on macOS, iOS, Android, Windows, and Linux desktops — pull the appropriate build from [Releases](https://github.com/open-astro/openastro-ara/releases).

---

## Quick start (APT install)

The daemon is released through the OpenAstro APT repository at **apt.openastro.net** (playbook §34).

```bash
# 1. Add the OpenAstro APT repo (one-time) — matches the live instructions at
#    https://apt.openastro.net (suite `trixie`, pre-dearmored keyring)
sudo curl -fsSL https://apt.openastro.net/repo/openastro-archive-keyring.gpg \
  -o /usr/share/keyrings/openastro-archive-keyring.gpg
echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/openastro-archive-keyring.gpg] https://apt.openastro.net trixie main" \
  | sudo tee /etc/apt/sources.list.d/openastro.list
sudo apt update

# 2. Install (apt resolves libcfitsio10 and astap-cli transitively)
sudo apt install openastroara-server

# 3. Star database for the plate solver (one-time, ~1.7 GB). ASTAP's D80 covers the
#    usual range of fields (roughly 0.25° to 30°); very wide fields want W08/G05 and
#    very narrow ones H17/H18 (playbook §18.I). The package creates /var/lib/astap
#    owned by the service user; the daemon passes it to astap_cli with -d
#    (Options → Plate solving → index path).
#    D80 is published only as a Debian package (its payload is the d80_*.1476 files);
#    extract it into the daemon's directory rather than installing it, so the files
#    land where the profile's index path points.
#    Not via /tmp: on Raspberry Pi OS it is a ~2 GB tmpfs and this package is 1.2 GB.
curl -L -o ~/d80.deb https://sourceforge.net/projects/astap-program/files/star_databases/d80_star_database.deb/download
dpkg-deb --fsys-tarfile ~/d80.deb \
  | sudo tar -x -C /var/lib/astap --strip-components=3 --wildcards './opt/astap/d80_*'
sudo chown -R openastroara:openastroara /var/lib/astap && rm ~/d80.deb

# 4. The systemd unit auto-starts on first install
sudo systemctl status openastroara-server

# 5. Verify the daemon is responding
curl http://$(hostname -s).local:5555/healthz   # expect "ok"
```

That's it. The Flutter client auto-discovers the daemon via mDNS (`_openastroara._tcp.local`) on the same LAN.

The install also pulls in the sibling packages the full experience expects — the AlpacaBridge
equipment hub and the guider daemon — via apt Recommends; pass `--no-install-recommends` for a
daemon-only install.

**Offline / one-off install:** every release's `.deb` also lives in the repo pool
(`https://apt.openastro.net/pool/main/`) — download it on another machine
and `sudo apt install ./openastroara-server_<version>_arm64.deb` on the Pi.

---

## What gets installed

| Path | Purpose | Owner |
|---|---|---|
| `/opt/openastroara/` | Self-contained .NET runtime + `OpenAstroAra.Server` binary + the `libsofa.so` / `libnovas31.so` astrometry natives | `openastroara:openastroara` |
| `/var/lib/astap/` | ASTAP star database (you download it, step 3 above; the solver binary itself is the `astap-cli` package) | `openastroara:openastroara` |
| `/etc/openastroara/server.env` | Environment overrides (`OPENASTROARA_PORT`, etc.) | `root:openastroara`, 640 |
| `/var/lib/openastroara/` | Profile + SQLite catalog (`profile.json`, `openastroara.db`) | `openastroara:openastroara` |
| `/var/log/openastroara/` | Rotated log files (Serilog file sink) | `openastroara:openastroara` |
| `/media/openastroara/` | Captures save path (mount your USB SSD here — see below) | `openastroara:openastroara` |
| `/etc/systemd/system/openastroara-server.service` | systemd unit | root |

The daemon runs as the dedicated `openastroara` system user; it never runs as root.

The first lines of the log say `Astrometry natives loaded (SOFA + NOVAS31)`. A
`Astrometry natives incomplete` warning there means the package is broken (or a manual
install skipped `scripts/build-astrometry-natives.sh`): slews still work, but altitude,
sun and moon conditions, the meridian-flip projection and polar-align solving fail until
the two `.so` files are next to the binary.

---

## Storage setup (required)

The captures volume must be **ext4** + writable by the `openastroara` user. The daemon refuses to start if the configured save path isn't ext4 per §28.9.

```bash
# 1. Identify the drive
lsblk

# 2. Reformat as ext4 if needed (DESTRUCTIVE; uses the entire device)
sudo mkfs.ext4 /dev/sda1

# 3. Get the UUID
sudo blkid /dev/sda1

# 4. Add to /etc/fstab — replace <uuid> with the value from blkid
echo "UUID=<uuid>  /media/openastroara  ext4  defaults,data=ordered,noatime,errors=remount-ro  0  2" | sudo tee -a /etc/fstab

# 5. Mount
sudo mkdir -p /media/openastroara
sudo mount /media/openastroara
sudo chown -R openastroara:openastroara /media/openastroara
```

The `errors=remount-ro` flag means a corrupted filesystem will remount read-only mid-session rather than allowing further damage. The daemon detects this and emits `storage.error` to the client.

A USB SSD is **strongly recommended** over USB stick — flash sticks don't sustain the §28.7 atomic-write durability budget on multi-hour sessions. Quality SSDs (Samsung T7, SanDisk Extreme, etc.) keep fsync latency well under the §28.7 ≤ 200 ms ceiling.

---

## UPS recommendation (advisory)

A USB-attached UPS keeps the Pi alive long enough on power loss to finish the in-flight FITS atomic-write, checkpoint the SQLite WAL, pause the sequence, and park the mount per §35's safety policy. ARA works fine without one — this is a "for night-long unattended runs, strongly consider" recommendation, not a requirement.

Recommended UPS HATs:
- **Geekworm X728** — clean 12V → 5V conversion, GPIO shutdown signal
- **PiJuice** — popular community choice, smaller capacity
- Generic 12V UPS HATs from Waveshare / Aliexpress — verify community reports

---

## Logs + diagnostics

```bash
# systemd journal
journalctl -u openastroara-server -f

# Serilog file sink (rotated daily, 14-day retention)
sudo ls -la /var/log/openastroara/
sudo tail -f /var/log/openastroara/openastroara-*.log

# Health probe
curl http://localhost:5555/healthz

# Full server-state snapshot (§60.4)
curl http://localhost:5555/api/v1/server/state | jq
```

---

## Updating

The daemon does NOT auto-update. New releases land on apt.openastro.net and install like any
other package:

```bash
sudo apt update
sudo apt install --only-upgrade openastroara-server
# systemd unit + DB schema migrations apply automatically on restart
```

Don't upgrade while a sequence is running — finish or stop the run first (the client warns via
the §34.7 imminent-restart event when a restart is pending).

Settings persist via `profile.json` + the SQLite catalog at `/var/lib/openastroara/` — neither is touched by upgrade.

---

## Uninstall

```bash
sudo apt remove openastroara-server         # removes binaries + unit
sudo apt purge  openastroara-server         # also removes /etc/openastroara/

# /var/lib/openastroara/ + /media/openastroara/ are NOT touched by purge —
# delete manually if you want to nuke captures + settings:
sudo rm -rf /var/lib/openastroara
# /media/openastroara is your captures drive; don't delete unless you mean it
```

---

## Manual install (no .deb)

If you're building from source or testing a development branch:

```bash
# 1. Run the per-Pi setup
sudo useradd -r -s /usr/sbin/nologin openastroara
sudo mkdir -p /opt/openastroara /etc/openastroara /var/lib/openastroara /var/log/openastroara
sudo chown -R openastroara:openastroara /opt/openastroara /var/lib/openastroara /var/log/openastroara
sudo chown root:openastroara /etc/openastroara
sudo chmod 750 /etc/openastroara

# 2. Install runtime dependency
sudo apt install libcfitsio10

# 3. Copy your linux-arm64 publish output into /opt/openastroara/
# (built via `dotnet publish OpenAstroAra.Server -c Release -r linux-arm64 --self-contained -p:PublishAot=false -o ./publish/arm64`)
# The SOFA/NOVAS31 astrometry natives are NOT produced by `dotnet publish`; build them into the
# same directory first (on the Pi itself: `sudo apt install build-essential`; cross-compiling
# from x86-64: `sudo apt install gcc-aarch64-linux-gnu` and prefix with `CC=aarch64-linux-gnu-gcc`).
scripts/build-astrometry-natives.sh ./publish/arm64
ls publish/arm64/libsofa.so publish/arm64/libnovas31.so   # both must exist
sudo cp -r publish/arm64/* /opt/openastroara/
sudo chown -R openastroara:openastroara /opt/openastroara

# 4. Drop the systemd unit (see playbook §13.3 for the full file)
sudo systemctl daemon-reload
sudo systemctl enable --now openastroara-server
```

---

## Troubleshooting

**Daemon won't start, `journalctl` shows "Storage drive is formatted as ..."**
The captures volume isn't ext4. See [Storage setup](#storage-setup-required).

**Daemon won't start, journalctl shows libcfitsio errors**
`sudo apt install libcfitsio10` — the .deb dependency should pull this in automatically, but if you're on a sparse distro you may need it explicitly.

**Client can't discover the daemon**
- mDNS announces require LAN multicast — verify the Pi's network supports it (most home networks do; some enterprise networks block multicast)
- Check the firewall: `sudo ufw status` — port 5555/tcp must be open
- Direct connection: enter the Pi's IP + port in the Flutter client's manual-connect dialog

**Captures fail with "storage.unavailable"**
USB drive unmounted or read-only. `mount | grep /media/openastroara` to verify. If `errors=remount-ro` triggered, run `sudo fsck -y /dev/sdaN` and `sudo mount -o remount,rw /media/openastroara`.

See [`design/PORT_PLAYBOOK.md`](../design/PORT_PLAYBOOK.md) §13 + §29 for deeper detail.
