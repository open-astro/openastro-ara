---
name: ara-sbc-vm
description: Stand up, boot, and deploy to a virtual observatory SBC — an arm64 Debian 13 VM under QEMU on this Mac that runs the real openastroara-server .deb with the real systemd hardening, as a stand-in for the Raspberry Pi. Use it whenever the user wants a test server, a "fake Pi", a VM to point the client at, to install or try a build/PR of the daemon somewhere safe, or when the Pi at openastro.lan is unreachable and a PR investigation needs a live daemon with a real profile. Subcommands: setup, start, stop, status, ssh, scp, deploy, build-pr, reset.
---

# ara-sbc-vm

The observatory daemon is meant to run unattended on a Raspberry Pi as a
Debian package with a hardened systemd unit. A dev `dotnet run` on the Mac
proves the code but not the deployment: no service user, no
`ProtectSystem=strict`, no `NoNewPrivileges`, no `.deb` postinst. This VM
gives you the real thing on your desk: Debian 13 arm64 (the Pi's OS),
Apple's hypervisor so it runs at native speed, and the same `.deb` CI ships.

Everything is driven by one script:

```bash
.claude/skills/ara-sbc-vm/scripts/vm.sh <subcommand>
```

State lives in `~/.local/share/ara-sbc-vm/` (disk image, logs, downloaded
artifacts). Nothing in the repo changes.

## What you get

| From the Mac | Inside the VM |
|---|---|
| `ssh -p 2222 astro@localhost` (key + password `astro`, passwordless sudo, like the Pi) | user `astro`, hostname `ara-vm` |
| `http://localhost:5555` | the daemon (`openastroara-server.service`) |
| `:6800`, `:4400`, `:8080` | forwarded for AlpacaBridge / guider if you install them later (skipped with a warning when the Mac already has something on that port) |

All forwards bind to 127.0.0.1 only: the guest has a published password and
passwordless sudo, so it must never be reachable from the Mac's LAN.
mDNS does not cross QEMU's user-mode network, so the client will not
discover it. On the client's connect screen enter host `localhost`, port
`5555` by hand.

## Workflow

**First time** (qemu via Homebrew, ~400 MB image download, one automated
serial-console boot that creates the user, enables sshd, and grows the disk):

```bash
vm.sh setup
```

**Every session:**

```bash
vm.sh start                 # boots in the background, returns when ssh answers
vm.sh deploy                # installs the newest green master CI .deb
vm.sh status                # daemon health, package version, profiles
```

**Try a PR's daemon** — point it at a worktree on the PR head (the
`ara-pr-investigation` skill makes one) and it publishes for arm64 on the
Mac, compiles the astrometry natives and assembles the `.deb` inside the
VM exactly as `packaging/build-deb.sh` does in CI, then installs it:

```bash
vm.sh build-pr /path/to/worktree            # version defaults to 0.0.0-dev-<sha>
vm.sh deploy ~/Downloads/some.deb           # or any .deb you already have
vm.sh deploy --run 36028793716              # or a specific CI run's artifact
```

`apt-get install ./x.deb` upgrades in place, so going back to master is just
`vm.sh deploy` again.

**Profiles.** A fresh VM has no profile and no site, so Tonight's Sky and
anything altitude-based show empty states. Either run the client's profile
wizard against it once, or import an exported profile through
Options → Profiles, or hit the API:

```bash
curl -X POST 'http://localhost:5555/api/v1/profiles?activate=true' -H 'content-type: application/json' -d '{"name":"RedCat test"}'
curl -X PUT  http://localhost:5555/api/v1/profile/site -H 'content-type: application/json' -d '{"site_name":"Home","latitude_deg":34.05,"longitude_deg":-118.25,"elevation_m":100,"time_zone":"America/Los_Angeles","use_custom_horizon":false,"default_horizon_altitude_deg":20,"bortle_class":6,"typical_seeing_arcsec":2.5,"twilight_definition":"astronomical","max_sequence_runtime_min":0,"soft_warning_altitude_deg":30,"observer_name":"","sqm_mag_per_arcsec2":0}'
```

The `.deb` bundles the sky catalogs as seed data, so unlike a bare dev
daemon nothing needs downloading for the planetarium to have objects.
Profile state persists in the VM's `/var/lib/openastroara` across restarts
and across `deploy`, so set it up once.

**Done for the day:** `vm.sh stop`. `vm.sh reset` throws the disk away and
`setup` rebuilds it from the cached image in a couple of minutes.

## What it cannot do

- No USB, so no real cameras, mounts, or focusers. For equipment, install
  the Alpaca simulators (`scripts/get-alpaca-simulators.sh`) inside the VM
  or on the Mac and point the daemon at them.
- It is not the exact Pi image: no Raspberry Pi OS kernel, no GPIO, no
  AlpacaBridge or guider unless you install them. Hardening interactions
  (the `NoNewPrivileges` sudo trap, `RestrictAddressFamilies` and netlink)
  reproduce faithfully because they are the unit's, not the board's.
- `build-pr` needs the .NET SDK on the Mac (it is already there for the repo).

## Things that go wrong

- **Port 5555 already in use** when starting: a local `dotnet run` daemon or
  a previous investigation is still up. `lsof -nP -iTCP:5555` finds it. The
  script refuses to start rather than silently forwarding to nothing.
- **Setup stalls at the serial console:** read
  `~/.local/share/ara-sbc-vm/first-boot.log`. The expect script waits for a
  `login:` prompt and a `# ` shell prompt; a changed Debian image banner is
  the usual cause.
- **ssh host key warnings** are suppressed on purpose (the VM is rebuilt
  often); never copy that setting to real hosts.
- **Slow:** if `vm.sh status` shows it running under `tcg`, the hypervisor
  was not available (Intel Mac or Linux without KVM). Everything still works,
  budget ten times longer for builds.
- **Pi memory still applies** for what runs where and which ports the guider
  and bridge use; see the `pi-rig-openastro-lan` memory.
