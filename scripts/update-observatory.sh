#!/usr/bin/env bash
# Rebuild ARA/client locally; build native ARM64 services on the equipment SBC.
set +x
set -Eeuo pipefail
umask 077
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)

fail() { printf 'error: %s\n' "$*" >&2; exit 1; }
need() { command -v "$1" >/dev/null || fail "missing command: $1"; }
log() { printf '%s\n' "$*"; }
quote_command() { printf '%q ' "$@"; }

# Identify artifacts by Debian metadata, never by newest-file/glob order.
package_file() {
    local dir=$1 package=$2 f found=''
    while IFS= read -r -d '' f; do
        [[ $(dpkg-deb -f "$f" Package) == "$package" ]] || continue
        [[ $(dpkg-deb -f "$f" Architecture) == arm64 ]] || fail "$f: wrong architecture"
        [[ -z $found ]] || fail "multiple $package artifacts in $dir"
        found=$f
    done < <(find "$dir" -maxdepth 1 -type f -name '*.deb' -print0)
    [[ -n $found ]] || fail "missing $package artifact in $dir"
    printf '%s\n' "$found"
}

remote_sudo() {
    if [[ -n ${sudo_password:-} ]]; then
        printf '%s\n' "$sudo_password" | sudo -S -p '' -- "$@"
    else
        sudo -n -- "$@"
    fi
}

remote_main() {
    local phase=$1 stage=$2 jobs=$3 offline=${4:-0}
    # Password arrives as data on stdin, never shell syntax or process arguments.
    IFS= read -r sudo_password || true
    [[ $stage == /home/*/.cache/openastro-update/run-* ]] || fail 'unexpected remote stage'
    cd "$stage"
    [[ $(dpkg --print-architecture) == arm64 ]] || fail 'SBC must run arm64 Debian'
    remote_sudo true
    if [[ $phase == build ]]; then
        if [[ $offline == 0 ]]; then
            remote_sudo apt-get update
            remote_sudo apt-get install -y --no-remove build-essential cmake ninja-build ccache \
                dpkg-dev debhelper pkg-config python3 curl rsync \
                libcfitsio-dev libusb-1.0-0-dev libudev-dev libgpiod-dev \
                libhidapi-dev nlohmann-json3-dev zlib1g-dev libsystemd-dev \
                libcurl4-openssl-dev libwxgtk3.2-dev libopencv-dev libv4l-dev \
                libcurl4-gnutls-dev libeigen3-dev libgtest-dev
        else
            log 'Offline SBC build: using installed compiler and dependencies.'
        fi
        # Read Build-Depends from the actual pinned source trees.
        python3 bridge/scripts/changelog_to_deb.py --changelog bridge/CHANGELOG.md \
            --out bridge/debian/changelog --package alpacabridge \
            --version "$(tr -d '[:space:]' < bridge/VERSION)" --maintainer 'OpenAstro <support@openastro.net>'
        python3 guider/scripts/changelog_to_deb.py --changelog guider/CHANGELOG.md \
            --out guider/debian/changelog --package openastro-guider \
            --version "$(awk '/^[[:space:]]*[0-9]+\.[0-9]+\.[0-9]+[^[:space:]]*[[:space:]]*$/ {gsub(/[[:space:]]/, ""); print; exit}' guider/version.md)" \
            --maintainer 'OpenAstro <support@openastro.net>'
        (cd bridge && PARALLEL="$jobs" bash scripts/build_deb.sh -j"$jobs")
        # Invoke the package's underlying build flow without interactive sudo.
        (cd guider
         python3 scripts/changelog_to_deb.py --changelog CHANGELOG.md \
            --out debian/changelog --package openastro-guider \
            --version "$(awk '/^[[:space:]]*[0-9]+\.[0-9]+\.[0-9]+[^[:space:]]*[[:space:]]*$/ {gsub(/[[:space:]]/, ""); print; exit}' version.md)" \
            --maintainer 'OpenAstro <support@openastro.net>'
         dpkg-buildpackage -us -uc -b -j"$jobs")
        # Bridge package build disables tests: run them explicitly before install.
        cmake -S bridge/AlpacaHTTP -B bridge/AlpacaHTTP/build-update-tests \
            -DALPACACORE_ENABLE_ALL_VENDORS=ON -DALPACAHTTP_BUILD_TESTS=ON \
            -DALPACACORE_BUILD_TESTS=ON -DCMAKE_BUILD_TYPE=Release
        cmake --build bridge/AlpacaHTTP/build-update-tests --parallel "$jobs"
        ctest --test-dir bridge/AlpacaHTTP/build-update-tests --output-on-failure
        (cd ara && bash scripts/build-astrometry-natives.sh "$stage/server")
        package_file "$stage" alpacabridge > bridge-package.txt
        package_file "$stage" openastro-guider > guider-package.txt
        file server/OpenAstroAra.Server | grep -q 'ARM aarch64' || fail 'server is not ARM64'
        test -s server/libnovas31.so && test -s server/libsofa.so
        touch BUILD_COMPLETE
        return
    fi
    [[ $phase == install ]] || fail 'unknown remote phase'
    [[ -f BUILD_COMPLETE ]] || fail 'remote build incomplete'
    local executable service_user server_dir backup path
    executable=$(systemctl show openastroara-server -p ExecStart --value)
    if [[ $executable == *'/opt/openastroara/server/OpenAstroAra.Server'* ]]; then
        server_dir=/opt/openastroara/server
    elif [[ $executable == *'/opt/openastroara/OpenAstroAra.Server'* ]]; then
        server_dir=/opt/openastroara
    else
        fail 'unsupported ARA ExecStart; preserve custom install and configure updater first'
    fi
    service_user=$(systemctl show openastroara-server -p User --value)
    [[ -n $service_user && $service_user != root ]] || fail 'ARA service user missing/root'
    # Existing installation only. Preserve custom systemd units and profile locations.
    systemctl cat alpacabridge openastro-guider openastroara-server > services-before.txt
    backup="$stage/backup"
    mkdir -p "$backup"
    remote_sudo cp -a "$server_dir" "$backup/server"
    remote_sudo cp -a /etc/systemd/system "$backup/systemd"
    dpkg-query -W > "$backup/packages.txt"
    # Prevent package maintainer scripts from starting equipment mid-upgrade.
    [[ ! -e /usr/sbin/policy-rc.d ]] || fail 'existing policy-rc.d: unattended install unsupported'
    printf '#!/bin/sh\nexit 101\n' > policy-rc.d
    remote_sudo install -m 755 policy-rc.d /usr/sbin/policy-rc.d
    local stopped=0 switched=0
    remote_cleanup() {
        local status=$?
        trap - EXIT
        remote_sudo rm -f /usr/sbin/policy-rc.d || true
        if (( status != 0 )); then
            log "Update failed. Backup: $backup. Services remain stopped; inspect before restart."
            if (( stopped )); then
                remote_sudo systemctl stop openastroara-server openastro-guider alpacabridge || true
            fi
            if (( switched )); then
                remote_sudo rsync -a --delete "$backup/server/" "$server_dir/" || true
            fi
            # Do not restart old server against a potentially migrated database.
        fi
        exit "$status"
    }
    trap remote_cleanup EXIT
    remote_sudo systemctl stop openastroara-server openastro-guider alpacabridge
    stopped=1
    # Services stopped: SQLite databases/WAL and configuration copied consistently.
    for path in /var/lib/openastroara /var/lib/openastro-guider /var/lib/alpacabridge \
                /etc/openastroara /etc/alpacabridge; do
        if remote_sudo test -d "$path"; then
            remote_sudo mkdir -p "$backup/state$path"
            remote_sudo rsync -a --exclude='/frames/' --exclude='/logs/' "$path/" "$backup/state$path/"
        fi
    done
    remote_sudo apt-get install -y --no-remove -o Dpkg::Options::=--force-confold \
        "$(cat bridge-package.txt)" "$(cat guider-package.txt)"
    # Restore existing site-specific service files. Upstream new units remain
    # available under /usr/lib; local overrides retain board-specific settings.
    remote_sudo cp -a "$backup/systemd/." /etc/systemd/system/
    switched=1
    remote_sudo rsync -a --delete --exclude=/scripts/ --exclude=/seed-data/ --exclude=/server/ --exclude=/update.sh server/ "$server_dir/"
    remote_sudo chown -R "$service_user:$(id -gn "$service_user")" "$server_dir"
    # Install new server authorization policy even on manual server layouts.
    if [[ -d ara/packaging/debian/usr/share/polkit-1/rules.d ]]; then
        remote_sudo mkdir -p /etc/polkit-1/rules.d
        remote_sudo cp ara/packaging/debian/usr/share/polkit-1/rules.d/*.rules /etc/polkit-1/rules.d/
    fi
    # Solver/catalog remain untouched unless supplied explicitly as verified archives.
    if [[ -f astap.tar ]]; then
        remote_sudo mkdir -p /opt/astap
        remote_sudo cp -a /opt/astap "$backup/astap"
        remote_sudo tar --no-same-owner -xf astap.tar -C /opt/astap
    fi
    remote_sudo rm -f /usr/sbin/policy-rc.d
    remote_sudo systemctl daemon-reload
    remote_sudo systemctl start alpacabridge
    curl --fail --retry 20 --retry-all-errors --retry-delay 2 --max-time 5 \
        http://127.0.0.1:6800/management/v1/configureddevices >/dev/null
    remote_sudo systemctl start openastro-guider
    python3 - <<'PY'
import json, socket, time
for attempt in range(30):
    try:
        with socket.create_connection(('127.0.0.1', 4400), 2) as s:
            s.settimeout(3)
            s.sendall(b'{"method":"get_app_state","id":1001}\n')
            with s.makefile('r') as f:
                for line in f:
                    message = json.loads(line)
                    if message.get('id') == 1001:
                        if 'result' not in message: raise RuntimeError(message)
                        raise SystemExit(0)
    except (OSError, ValueError, RuntimeError):
        time.sleep(2)
raise SystemExit('guider RPC readiness failed')
PY
    remote_sudo systemctl start openastroara-server
    curl --fail --retry 30 --retry-all-errors --retry-delay 2 --max-time 5 \
        http://127.0.0.1:5555/healthz
    systemctl is-active alpacabridge openastro-guider openastroara-server
    touch INSTALL_COMPLETE
    log "SBC updated. Recovery backup: $backup"
}

main() {
    local mode=update target_mode=all host=172.24.1.1 user=astro jobs=2 offline=0 local_mode=0 source_root=''
    local ara_url=https://github.com/open-astro/openastro-ara.git ara_ref=HEAD
    local bridge_url=https://github.com/open-astro/AlpacaBridge.git bridge_ref=HEAD
    local guider_url=https://github.com/open-astro/openastro-guider.git guider_ref=HEAD
    local workspace=${OPENASTRO_UPDATE_DIR:-${XDG_CACHE_HOME:-$HOME/.cache}/openastro-update}
    local install_dir=${OPENASTRO_CLIENT_DIR:-$HOME/.local/opt/openastroara}
    local dotnet=${DOTNET:-dotnet} flutter=${FLUTTER:-flutter} astap_dir=''
    while (($#)); do
        case $1 in
            --plan) mode=plan; shift ;;
            --build-only) mode=build; shift ;;
            --client-only)
                [[ $target_mode == all || $target_mode == client ]] || fail 'choose one target: --client-only or --sbc-only'
                target_mode=client; shift ;;
            --sbc-only)
                [[ $target_mode == all || $target_mode == sbc ]] || fail 'choose one target: --client-only or --sbc-only'
                target_mode=sbc; shift ;;
            --offline) offline=1; shift ;;
            --local) local_mode=1; shift ;;
            --source-root)
                (($# >= 2)) || fail 'missing value for --source-root'
                source_root=$2; shift 2 ;;
            --host|--user|--jobs|--ara-url|--ara-ref|--bridge-url|--bridge-ref|--guider-url|--guider-ref|--astap-dir)
                (($# >= 2)) || fail "missing value for $1"
                case $1 in
                    --host) host=$2;; --user) user=$2;; --jobs) jobs=$2;;
                    --ara-url) ara_url=$2;; --ara-ref) ara_ref=$2;;
                    --bridge-url) bridge_url=$2;; --bridge-ref) bridge_ref=$2;;
                    --guider-url) guider_url=$2;; --guider-ref) guider_ref=$2;;
                    --astap-dir) astap_dir=$2;;
                esac
                shift 2 ;;
            --help|-h)
                cat <<'HELP'
Usage: update-observatory.sh [--plan|--build-only] [--client-only|--sbc-only] [options]
Default: build/test all components, update SBC, install local Linux client.
--client-only                 build/test/install local Linux Flutter client only
--sbc-only                    build/push/install ARM64 SBC services only
Run only when equipment is idle; service restarts interrupt imaging/guiding.
--host HOST --user USER       defaults: 172.24.1.1 / astro
--jobs N                      default: 2 (SBC memory budget)
--offline                     use local source/dependency caches; no downloads
--local                       build current local ARA worktree, including dirty files
--source-root DIR             local sibling checkouts for --offline
--ara-url URL --ara-ref REF    default: upstream HEAD; accepts PR refs/commit IDs
--bridge-url URL --bridge-ref REF
--guider-url URL --guider-ref REF
--astap-dir DIR               optional prepared ARM64 astap_cli + star database
Environment: DOTNET, FLUTTER, OPENASTRO_PASSWORD, OPENASTRO_UPDATE_DIR,
             OPENASTRO_CLIENT_DIR. Password omitted: SSH key + passwordless sudo.
--plan performs no network access, builds, or deployment. --offline requires
preinstalled SBC build dependencies and cached local .NET/Flutter packages.
Prerequisites: Linux client build tools, .NET from global.json, pinned Flutter;
SBC: existing ARM64 Debian installation, SSH, sudo, free build space. Online
mode downloads source and SBC packages; offline mode does not.
HELP
                return ;;
            *) fail "unknown option: $1" ;;
        esac
    done
    [[ $host =~ ^[a-zA-Z0-9][a-zA-Z0-9.-]*$ && $user =~ ^[a-z_][a-z0-9_-]*$ ]] || fail 'invalid SSH destination'
    [[ $jobs =~ ^[1-9][0-9]*$ ]] || fail '--jobs must be positive integer'
    [[ $(uname -s) == Linux ]] || fail 'this updater builds the local Linux client'
    [[ $EUID -ne 0 ]] || fail 'do not run with sudo; run as the client user so HOME and Flutter stay correct'
    log "Target: $target_mode; SBC: $user@$host; native jobs: $jobs"
    log "ARA: $ara_url $ara_ref; bridge: $bridge_url $bridge_ref; guider: $guider_url $guider_ref"
    log "Client: $install_dir; mode: $mode; offline: $offline; local: $local_mode; solver/database: ${astap_dir:-preserve existing}"
    [[ $mode != plan ]] || return 0
    need git
    if [[ $target_mode != client ]]; then
        for cmd in ssh scp tar python3 curl file rsync "$dotnet"; do need "$cmd"; done
    fi
    if [[ $target_mode != sbc ]]; then
        for cmd in tar python3 "$flutter"; do need "$cmd"; done
    fi
    if [[ -z ${OPENASTRO_PASSWORD:-} && -t 0 ]]; then
        if [[ $target_mode != client ]]; then
            read -rsp 'SBC SSH/sudo password (blank when SSH key + passwordless sudo): ' OPENASTRO_PASSWORD
            printf '\n'
            export OPENASTRO_PASSWORD
        fi
    fi
    local -a ssh_base=(ssh -o ConnectTimeout=10 -o ServerAliveInterval=15 -o ServerAliveCountMax=4 -o StrictHostKeyChecking=accept-new)
    local -a scp_base=(scp -o ConnectTimeout=10 -o StrictHostKeyChecking=accept-new)
    if [[ $target_mode != client && -n ${OPENASTRO_PASSWORD:-} ]]; then
        need sshpass
        export SSHPASS=$OPENASTRO_PASSWORD
        ssh_base=(sshpass -e "${ssh_base[@]}")
        scp_base=(sshpass -e "${scp_base[@]}")
    elif [[ $target_mode != client ]]; then
        ssh_base+=(-o BatchMode=yes)
        scp_base+=(-o BatchMode=yes)
    fi
    local target="$user@$host" run='' remote_dir remote_free_kib
    if [[ $target_mode != client ]]; then
    remote_free_kib=$("${ssh_base[@]}" "$target" "df -Pk /home/$user | awk 'NR==2 {print \$4}'") || \
        fail "cannot inspect SBC free space; check SSH and sudo access"
    [[ $remote_free_kib =~ ^[0-9]+$ ]] || fail "invalid SBC free-space report: $remote_free_kib"
    if (( remote_free_kib < 4194304 )); then
        local remote_usage
        remote_usage=$("${ssh_base[@]}" "$target" "du -xhd1 /home/$user /var/cache /var/log 2>/dev/null | sort -h | tail -20" || true)
        fail "SBC needs at least 4 GiB free on /home; reported ${remote_free_kib} KiB. Largest directories:
${remote_usage}
Remove only obsolete data, then retry."
    fi
    log "SBC free space: ${remote_free_kib} KiB"
    fi
    mkdir -p "$workspace"
    run=$(mktemp -d "$workspace/run-XXXXXXXX")
    log "Build log/artifacts: $run"
    if [[ $target_mode != client ]]; then
        remote_dir="/home/$user/.cache/openastro-update/${run##*/}"
        "${ssh_base[@]}" "$target" "$(quote_command mkdir -p "$remote_dir")"
    fi
    snapshot() {
        local name=$1 url=$2 ref=$3
        local source=$url
        if (( local_mode )) && [[ $name == ara ]]; then
            if [[ -z $source_root ]]; then
                source_root=${OPENASTRO_SOURCE_ROOT:-}
                if [[ -z $source_root || ! -e $source_root/openastro-ara/.git ]]; then
                    for candidate in "$(pwd)" "$(cd -- "$script_dir/../.." && pwd)"; do
                        if [[ -e $candidate/openastro-ara/.git ]]; then
                            source_root=$candidate
                            break
                        fi
                    done
                fi
            fi
            if [[ -e $url/.git ]]; then
                source=$url
            else
                source="$source_root/openastro-ara"
            fi
            [[ -e $source/.git ]] || fail "local ARA source missing: $source"
            [[ ! -f $source/.gitmodules ]] || fail 'submodule repository requires explicit snapshot support'
            local revision
            revision=$(git -C "$source" rev-parse HEAD)
            mkdir -p "$run/$name"
            # Copy the worktree, not Git's index: this preserves uncommitted,
            # untracked, and local-only commits for a test build. Generated
            # Flutter/.NET output stays out of the immutable updater snapshot.
            tar -C "$source" \
                --exclude='.git' \
                --exclude='client/openastroara_client/build' \
                --exclude='client/openastroara_client/.dart_tool' \
                --exclude='**/bin' \
                --exclude='**/obj' \
                -cf - . | tar -C "$run/$name" -xf -
            printf '%s %s-local-worktree\n' "$name" "$revision" >> "$run/revisions.txt"
            return
        fi
        if (( offline )); then
            if [[ -z $source_root ]]; then
                source_root=${OPENASTRO_SOURCE_ROOT:-$(pwd)}
                [[ -d $source_root/openastro-ara/.git ]] || \
                    [[ ! -d "$(dirname "$source_root")/openastro-ara/.git" ]] || \
                    source_root=$(dirname "$source_root")
            fi
            case $name in
                ara) [[ -e $url/.git ]] || source="$source_root/openastro-ara" ;;
                bridge) [[ -e $url/.git ]] || source="$source_root/AlpacaBridge" ;;
                guider) [[ -e $url/.git ]] || source="$source_root/openastro-guider" ;;
            esac
            [[ -e $source/.git ]] || fail "offline source missing: $source"
            # Worktrees use a .git file, and the cache may be on another mount;
            # avoid hard-link failures while making an immutable snapshot.
            git clone --local --no-hardlinks --no-checkout -- "$source" "$run/$name"
            git -C "$run/$name" checkout --detach "$ref"
        else
            git clone --no-checkout -- "$url" "$run/$name"
            git -C "$run/$name" fetch origin "$ref"
            git -C "$run/$name" checkout --detach FETCH_HEAD
        fi
        [[ ! -f $run/$name/.gitmodules ]] || fail 'submodule repository requires explicit snapshot support'
        printf '%s %s\n' "$name" "$(git -C "$run/$name" rev-parse HEAD)" >> "$run/revisions.txt"
    }
    snapshot ara "$ara_url" "$ara_ref"
    if [[ $target_mode != client ]]; then
        snapshot bridge "$bridge_url" "$bridge_ref"
        snapshot guider "$guider_url" "$guider_ref"
    fi
    local client="$run/ara/client/openastroara_client" pinned actual
    if [[ $target_mode != sbc ]]; then
        pinned=$(tr -d '[:space:]' < "$client/.flutter-version")
        actual=$("$flutter" --version --machine | python3 -c 'import json,sys; print(json.load(sys.stdin)["frameworkVersion"])')
        [[ $actual == "$pinned" ]] || fail "Flutter $pinned required; found $actual. Set FLUTTER to pinned SDK."
    fi
    if [[ $target_mode != client ]]; then
    (cd "$run/ara"
     if (( offline )); then
         "$dotnet" restore OpenAstroAra.sln --ignore-failed-sources -m:1
         dotnet_args=(--no-restore)
     else
         dotnet_args=()
     fi
     "$dotnet" test OpenAstroAra.Test/OpenAstroAra.Test.csproj "${dotnet_args[@]}" --filter \
        'FullyQualifiedName~SystemctlGuiderProcessSupervisorTest|FullyQualifiedName~GuiderActivePollTest|FullyQualifiedName~PHD2Guider' -m:1
     "$dotnet" publish OpenAstroAra.Server/OpenAstroAra.Server.csproj -c Release -r linux-arm64 \
        --self-contained true -p:PublishAot=false -o "$run/server" -m:1 "${dotnet_args[@]}")
    fi
    if [[ $target_mode != sbc ]]; then
    (cd "$client"
     if (( offline )); then
         # Resolve once from the local cache. Every later Flutter command must
         # use --no-pub: Flutter otherwise performs a pub.dev advisory lookup,
         # which breaks the promise that --offline makes no network requests.
         "$flutter" pub get --offline
         "$flutter" analyze --no-pub
         "$flutter" test --no-pub
         "$flutter" build linux --release --no-pub
     else
         "$flutter" pub get
         "$flutter" analyze
         "$flutter" test
         "$flutter" build linux --release
     fi)
    fi
    local arch
    if [[ $target_mode != sbc ]]; then
        case $(uname -m) in x86_64) arch=x64;; aarch64) arch=arm64;; *) fail 'unsupported local CPU';; esac
        local bundle="$client/build/linux/$arch/release/bundle"
        [[ -x $bundle/openastroara ]] || fail 'client bundle missing'
    fi
    if [[ $target_mode != client && -n $astap_dir ]]; then
        [[ -x $astap_dir/astap_cli ]] || fail 'ASTAP executable missing'
        file "$astap_dir/astap_cli" | grep -q 'ARM aarch64' || fail 'ASTAP must be ARM64'
        tar -cf "$run/astap.tar" -C "$astap_dir" .
    fi
    if [[ $target_mode != client ]]; then
    cp "${BASH_SOURCE[0]}" "$run/update-observatory.sh"
    tar --exclude=.git --exclude="ara/*/bin" --exclude="ara/*/obj" --exclude="ara/client/openastroara_client/build" --exclude=.dart_tool -czf "$run/payload.tar.gz" -C "$run" ara bridge guider server revisions.txt update-observatory.sh
    (cd "$run" && sha256sum payload.tar.gz > payload.tar.gz.sha256)
    "${scp_base[@]}" "$run/payload.tar.gz" "$target:$remote_dir/payload.tar.gz"
    "${scp_base[@]}" "$run/payload.tar.gz.sha256" "$target:$remote_dir/payload.tar.gz.sha256"
    "${ssh_base[@]}" "$target" "cd $(printf '%q' "$remote_dir") && sha256sum -c payload.tar.gz.sha256"
    "${ssh_base[@]}" "$target" "$(quote_command tar -xzf "$remote_dir/payload.tar.gz" -C "$remote_dir")"
    if [[ -f $run/astap.tar ]]; then "${scp_base[@]}" "$run/astap.tar" "$target:$remote_dir/astap.tar"; fi
    run_remote() {
        printf '%s\n' "${OPENASTRO_PASSWORD:-}" | "${ssh_base[@]}" "$target" \
        "$(quote_command bash "$remote_dir/update-observatory.sh" --remote "$1" "$remote_dir" "$jobs" "$offline")"
    }
    run_remote build
    [[ $mode != build ]] || { log "Build complete: $run and $target:$remote_dir"; return 0; }
    run_remote install
    fi
    # Retain previous bundle; install only after remote health checks succeed.
    if [[ $target_mode != sbc && $mode != build ]]; then
        mkdir -p "$(dirname "$install_dir")"
        local next="$install_dir.next-${run##*/}"
        mkdir "$next"
        cp -a "$bundle/." "$next/"
        if [[ -e $install_dir ]]; then mv "$install_dir" "$install_dir.previous-${run##*/}"; fi
        mv "$next" "$install_dir"
        mkdir -p "$HOME/.local/bin"
        ln -sfn "$install_dir/openastroara" "$HOME/.local/bin/openastroara"
        if [[ $target_mode == all ]]; then
            log "Updated. Relaunch $HOME/.local/bin/openastroara; connect to $host:5555."
        else
            log "Local ARA client updated. Relaunch $HOME/.local/bin/openastroara."
        fi
    elif [[ $target_mode == client ]]; then
        log "Client build complete: $run"
    elif [[ $target_mode == sbc ]]; then
        log "SBC ARA services updated over SSH/Wi-Fi. Recovery backup is on the SBC."
    else
        log "Updated. Relaunch $HOME/.local/bin/openastroara; connect to $host:5555."
    fi
}

if [[ ${BASH_SOURCE[0]} == "$0" ]]; then
    if [[ ${1:-} == --remote ]]; then shift; remote_main "$@"; else main "$@"; fi
fi
