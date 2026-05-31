# Deploying OpenAstro Ara

This guide covers running `OpenAstroAra.Server` on a Raspberry Pi. Reference platform is **Raspberry Pi 4 (4 GB+) or Pi 5** on **64-bit Debian Trixie** or newer. Other ARM64 SBCs (Orange Pi 5, Rock Pi, etc.) running Trixie arm64 work best-effort.

The Flutter client (`OpenAstroAra.Client`) runs on macOS, iOS, Android, Windows, and Linux desktops — pull the appropriate build from [Releases](https://github.com/open-astro/openastro-ara/releases).

---

## Quick start (`.deb` install)

```bash
# 1. Download the .deb for your release
wget https://github.com/open-astro/openastro-ara/releases/download/v0.0.1-ara.1/openastroara-server_0.0.1-ara.1_arm64.deb

# 2. Install (apt resolves libcfitsio10 transitively)
sudo apt install ./openastroara-server_0.0.1-ara.1_arm64.deb

# 3. The systemd unit auto-starts on first install
sudo systemctl status openastroara-server

# 4. Verify the daemon is responding
curl http://$(hostname -s).local:5555/healthz   # expect "ok"
```

That's it. The Flutter client auto-discovers the daemon via mDNS (`_openastroara._tcp.local`) on the same LAN.

---

## What gets installed

| Path | Purpose | Owner |
|---|---|---|
| `/opt/openastroara/` | Self-contained .NET runtime + `OpenAstroAra.Server` binary | `openastroara:openastroara` |
| `/etc/openastroara/server.env` | Environment overrides (`OPENASTROARA_PORT`, etc.) | `root:openastroara`, 640 |
| `/var/lib/openastroara/` | Profile + SQLite catalog (`profile.json`, `openastroara.db`) | `openastroara:openastroara` |
| `/var/log/openastroara/` | Rotated log files (Serilog file sink) | `openastroara:openastroara` |
| `/media/openastroara/` | Captures save path (mount your USB SSD here — see below) | `openastroara:openastroara` |
| `/etc/systemd/system/openastroara-server.service` | systemd unit | root |

The daemon runs as the dedicated `openastroara` system user; it never runs as root.

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

The daemon does NOT auto-update. New releases come from GitHub Releases.

```bash
sudo systemctl stop openastroara-server
sudo apt install ./openastroara-server_<new-version>_arm64.deb
# systemd unit + DB schema migrations apply automatically on next start
sudo systemctl start openastroara-server
```

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

See [`design/PORT_PLAYBOOK.md`](design/PORT_PLAYBOOK.md) §13 + §29 for deeper detail.
