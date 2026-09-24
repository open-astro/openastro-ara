#!/usr/bin/env bash
# ara-sbc-vm — a virtual arm64 Debian 13 SBC under QEMU (Apple hypervisor on
# Apple Silicon) that stands in for the observatory Pi: same OS, same .deb,
# same systemd hardening. Everything lives under $VM_DIR; the repo is untouched.
#
#   vm.sh setup                 install qemu, fetch + resize the nocloud image, first-boot provision
#   vm.sh start                 boot in the background (ssh :2222, daemon :5555, bridge :6800, guider :4400/:8080)
#   vm.sh stop                  clean shutdown (falls back to kill after 60 s)
#   vm.sh status                running? ports answering? daemon healthy?
#   vm.sh ssh [cmd...]          shell / run a command as astro
#   vm.sh scp <src> <dest>      copy to/from the VM (dest like vm:/tmp/x)
#   vm.sh deploy [<file.deb> | --run <ci-run-id>]   install a .deb (default: latest green master CI artifact)
#   vm.sh build-pr <repo-or-worktree> [<version>]   publish the daemon for arm64 on the Mac, build natives + .deb inside the VM, deploy it
#   vm.sh reset                 delete the disk (keeps the pristine download) so setup starts clean
set -euo pipefail

VM_DIR="${ARA_VM_DIR:-$HOME/.local/share/ara-sbc-vm}"
IMG_URL="https://cloud.debian.org/images/cloud/trixie/latest/debian-13-nocloud-arm64.qcow2"
PRISTINE="$VM_DIR/debian-13-nocloud-arm64.pristine.qcow2"
DISK="$VM_DIR/ara-vm.qcow2"
DISK_SIZE="${ARA_VM_DISK:-20G}"
MEM="${ARA_VM_MEM:-4G}"
CPUS="${ARA_VM_CPUS:-4}"
PIDFILE="$VM_DIR/qemu.pid"
PROVISIONED="$VM_DIR/.provisioned"
SERIAL="$VM_DIR/serial.log"
MONITOR="$VM_DIR/monitor.sock"
SSH_PORT=2222
PUBKEY_FILE="${ARA_VM_PUBKEY:-$HOME/.ssh/id_ed25519.pub}"
FWD="hostfwd=tcp::${SSH_PORT}-:22,hostfwd=tcp::5555-:5555,hostfwd=tcp::6800-:6800,hostfwd=tcp::4400-:4400,hostfwd=tcp::8080-:8080"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

log()  { printf '\033[1m[ara-vm]\033[0m %s\n' "$*" >&2; }
fail() { printf 'error: %s\n' "$*" >&2; exit 1; }

firmware() {
    local f
    for f in /opt/homebrew/share/qemu/edk2-aarch64-code.fd /usr/local/share/qemu/edk2-aarch64-code.fd /usr/share/qemu-efi-aarch64/QEMU_EFI.fd /usr/share/AAVMF/AAVMF_CODE.fd; do
        [ -f "$f" ] && { echo "$f"; return; }
    done
    fail "no aarch64 UEFI firmware found; is qemu installed? (vm.sh setup)"
}

accel_args() {
    # hvf = Apple's hypervisor: near-native arm64 on Apple Silicon. Elsewhere
    # fall back to TCG (software emulation; works, ~10x slower).
    if [ "$(uname -s)" = Darwin ] && [ "$(uname -m)" = arm64 ]; then
        echo "-accel hvf -cpu host"
    elif [ "$(uname -s)" = Linux ] && [ "$(uname -m)" = aarch64 ] && [ -w /dev/kvm ]; then
        echo "-accel kvm -cpu host"
    else
        echo "-accel tcg -cpu cortex-a76"
    fi
}

qemu_base() {
    # shellcheck disable=SC2046
    echo qemu-system-aarch64 -M virt $(accel_args) -smp "$CPUS" -m "$MEM" \
        -bios "$(firmware)" \
        -drive "file=$DISK,if=virtio,format=qcow2" \
        -netdev "user,id=n0,$FWD" -device virtio-net-pci,netdev=n0 \
        -device virtio-rng-pci
}

running() { [ -f "$PIDFILE" ] && kill -0 "$(cat "$PIDFILE")" 2>/dev/null; }

check_ports() {
    local p
    for p in $SSH_PORT 5555 6800 4400 8080; do
        if lsof -nP -iTCP:"$p" -sTCP:LISTEN >/dev/null 2>&1; then
            fail "port $p is already in use on this Mac (a local daemon? 'lsof -nP -iTCP:$p'); free it first"
        fi
    done
    return 0
}

SSH_OPTS=(-p "$SSH_PORT" -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR -o ConnectTimeout=5)
vssh() { ssh "${SSH_OPTS[@]}" astro@localhost "$@"; }
vscp() { scp -P "$SSH_PORT" -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR "$@"; }

wait_ssh() {
    local i
    for i in $(seq 1 90); do
        vssh true 2>/dev/null && return 0
        sleep 2
    done
    fail "VM did not answer on ssh :$SSH_PORT within 3 min; see $SERIAL"
}

cmd_setup() {
    mkdir -p "$VM_DIR"
    if ! command -v qemu-system-aarch64 >/dev/null; then
        command -v brew >/dev/null || fail "qemu missing and no Homebrew; install qemu by hand"
        log "installing qemu via Homebrew (a few minutes)"
        brew install qemu
    fi
    [ -f "$PUBKEY_FILE" ] || fail "no SSH public key at $PUBKEY_FILE (set ARA_VM_PUBKEY)"
    if [ ! -f "$PRISTINE" ]; then
        log "downloading Debian 13 nocloud arm64 image (~400 MB)"
        # Debian's mirrors drop long transfers now and then; resume until whole.
        local try
        for try in 1 2 3 4 5 6; do
            curl -fL -sS -C - --retry 3 -o "$PRISTINE.part" "$IMG_URL" && break
            log "download interrupted (attempt $try), resuming"
            sleep 3
        done
        # A resumed file that is still short means every attempt failed.
        [ "$(stat -f %z "$PRISTINE.part" 2>/dev/null || stat -c %s "$PRISTINE.part")" -gt 300000000 ] || fail "image download incomplete; rerun setup"
        qemu-img info "$PRISTINE.part" >/dev/null || fail "downloaded image is not a valid qcow2"
        mv "$PRISTINE.part" "$PRISTINE"
    fi
    if [ -f "$DISK" ] && [ -f "$PROVISIONED" ]; then
        log "disk already provisioned at $DISK; use 'vm.sh reset' to start over"
    else
        running && fail "a VM is already running; stop it first"
        check_ports
        # A disk without the stamp is a half-finished setup: start from the pristine image again.
        cp "$PRISTINE" "$DISK"
        qemu-img resize "$DISK" "$DISK_SIZE"
        log "first boot: provisioning over the serial console (astro/astro, sshd, grow root)"
        # shellcheck disable=SC2046
        PUBKEY="$(cat "$PUBKEY_FILE")" expect -f "$SCRIPT_DIR/first-boot.expect" $(qemu_base) -nographic \
            | tee "$VM_DIR/first-boot.log" | grep -E "PROVISION_OK|error|E:|Filesystem|/dev/" || true
        grep -q PROVISION_OK "$VM_DIR/first-boot.log" || fail "provisioning did not finish; read $VM_DIR/first-boot.log"
        touch "$PROVISIONED"
    fi
    log "setup complete. Next: vm.sh start && vm.sh deploy"
}

cmd_start() {
    [ -f "$DISK" ] || fail "no disk; run 'vm.sh setup' first"
    running && { log "already running (pid $(cat "$PIDFILE"))"; return; }
    check_ports
    : > "$SERIAL"
    # shellcheck disable=SC2046
    $(qemu_base) -display none -daemonize -pidfile "$PIDFILE" \
        -serial "file:$SERIAL" -monitor "unix:$MONITOR,server,nowait"
    log "booting (pid $(cat "$PIDFILE")); waiting for ssh"
    wait_ssh
    log "up: ssh -p $SSH_PORT astro@localhost   daemon → http://localhost:5555"
}

cmd_stop() {
    running || { log "not running"; rm -f "$PIDFILE"; return; }
    local pid; pid=$(cat "$PIDFILE")
    vssh sudo poweroff 2>/dev/null || echo system_powerdown | nc -U "$MONITOR" >/dev/null 2>&1 || true
    local i
    for i in $(seq 1 30); do kill -0 "$pid" 2>/dev/null || break; sleep 2; done
    kill -0 "$pid" 2>/dev/null && { log "still up after 60 s; killing"; kill "$pid"; }
    rm -f "$PIDFILE"
    log "stopped"
}

cmd_status() {
    if running; then echo "vm:      running (pid $(cat "$PIDFILE"), $CPUS cpu, $MEM)"; else echo "vm:      stopped"; return; fi
    printf 'ssh:     '; vssh 'echo "$(hostname) $(uname -m) $(. /etc/os-release; echo $PRETTY_NAME)"' 2>/dev/null || echo "not answering"
    printf 'daemon:  '; curl -s -m 3 http://localhost:5555/healthz 2>/dev/null || echo "not answering on :5555"; echo
    printf 'package: '; vssh "dpkg-query -W -f='\${Version} (\${Status})' openastroara-server 2>/dev/null || echo 'not installed'"; echo
    printf 'service: '; vssh "systemctl is-active openastroara-server 2>/dev/null || true"
    printf 'profile: '; curl -s -m 3 http://localhost:5555/api/v1/profiles 2>/dev/null | python3 -c 'import json,sys; d=json.load(sys.stdin); print(", ".join(p["name"]+(" (active)" if p["id"]==d.get("active_id") else "") for p in d["profiles"]) or "none")' 2>/dev/null || echo "?"
}

latest_master_deb() {
    local run="$1" dir="$VM_DIR/artifacts"
    if [ -z "$run" ]; then
        # --status success on gh matches stale runs here; filter on conclusion instead.
        run=$(gh run list --branch master --workflow CI -L 20 --json databaseId,conclusion,status -q '[.[] | select(.status=="completed" and .conclusion=="success")][0].databaseId')
        [ -n "$run" ] || fail "no green master CI run found"
    fi
    mkdir -p "$dir"; rm -rf "$dir/run-$run"
    log "downloading arm64 .deb from CI run $run"
    gh run download "$run" -p '*arm64-deb' -D "$dir/run-$run" >/dev/null
    find "$dir/run-$run" -name '*.deb' | head -1
}

cmd_deploy() {
    running || fail "VM not running; vm.sh start"
    local deb="" run=""
    case "${1:-}" in
        --run) run="${2:?run id}";;
        "") ;;
        *) deb="$1";;
    esac
    [ -n "$deb" ] || deb=$(latest_master_deb "$run")
    [ -f "$deb" ] || fail "no .deb at $deb"
    log "installing $(basename "$deb")"
    vscp "$deb" astro@localhost:/tmp/ara.deb
    # Same steps as docs/DEPLOY.md. The .deb's postinst creates the service
    # user, sets caps, and starts the unit; libcfitsio + astap come from apt.
    # DEPLOY.md's "storage setup" mounts a drive at /media/openastroara; the
    # unit's ReadWritePaths= hard-requires the path to exist (no "-" prefix),
    # so without it the service fails with 226/NAMESPACE. The VM has no drive:
    # a plain directory stands in for the mount (found 2026-09-24).
    vssh 'sudo mkdir -p /media/openastroara && sudo apt-get update -qq && sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq /tmp/ara.deb 2>&1 | tail -3 && sudo chown openastroara:openastroara /media/openastroara && sudo systemctl restart openastroara-server'
    local i
    for i in $(seq 1 30); do curl -sf -m 3 http://localhost:5555/healthz >/dev/null 2>&1 && break; sleep 2; done
    curl -sf -m 3 http://localhost:5555/healthz >/dev/null || { vssh 'sudo journalctl -u openastroara-server -n 30 --no-pager'; fail "daemon not healthy after install"; }
    log "daemon healthy on http://localhost:5555 (connect the client with host localhost, port 5555)"
}

cmd_build_pr() {
    # Turns any checkout (a worktree on a PR head, say) into a .deb the same
    # way CI does, except the arm64 natives compile natively inside the VM
    # instead of via a cross compiler on the Mac.
    running || fail "VM not running; vm.sh start"
    local src="${1:?repo or worktree path}" version="${2:-}"
    [ -f "$src/OpenAstroAra.sln" ] || fail "$src is not an openastro-ara checkout"
    [ -n "$version" ] || version="0.0.0-dev-$(git -C "$src" rev-parse --short HEAD)"
    local out="$VM_DIR/build/$version"; rm -rf "$out"; mkdir -p "$out"
    log "dotnet publish linux-arm64 (self-contained) from $src"
    (cd "$src" && dotnet publish OpenAstroAra.Server/OpenAstroAra.Server.csproj -c Release -r linux-arm64 --self-contained -p:PublishAot=false -p:TreatWarningsAsErrors=false -o "$out/publish" > "$out/publish.log" 2>&1) || { tail -20 "$out/publish.log"; fail "publish failed"; }
    log "shipping publish dir + vendored C sources + packaging to the VM"
    vssh "rm -rf ~/build && mkdir -p ~/build"
    (cd "$src" && tar -cf - scripts/build-astrometry-natives.sh SOFA NOVAS31 packaging) | vssh "tar -xf - -C ~/build"
    (cd "$out" && tar -cf - publish) | vssh "tar -xf - -C ~/build"
    log "building natives + .deb inside the VM"
    vssh "set -e; cd ~/build; sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq build-essential dpkg-dev curl >/dev/null; \
          bash scripts/build-astrometry-natives.sh publish >/dev/null; \
          bash packaging/build-deb.sh publish '$version' dist 2>&1 | tail -3"
    vscp "astro@localhost:~/build/dist/openastroara-server_${version}_arm64.deb" "$out/"
    cmd_deploy "$out/openastroara-server_${version}_arm64.deb"
}

cmd_reset() {
    running && cmd_stop
    rm -f "$DISK" "$PIDFILE" "$SERIAL" "$MONITOR" "$PROVISIONED"
    log "disk removed; 'vm.sh setup' will provision a fresh one from the cached image"
}

case "${1:-}" in
    setup)    cmd_setup ;;
    start)    cmd_start ;;
    stop)     cmd_stop ;;
    status)   cmd_status ;;
    ssh)      shift; vssh "$@" ;;
    scp)      shift; vscp "${@/#vm:/astro@localhost:}" ;;
    deploy)   shift; cmd_deploy "$@" ;;
    build-pr) shift; cmd_build_pr "$@" ;;
    reset)    cmd_reset ;;
    *) sed -n 2,14p "$0"; exit 2 ;;
esac
