#!/usr/bin/env bash
# Builds the bundled ASTAP command-line plate solver (astap_cli) from the git submodule under
# third_party/astap: OpenAstro's fork of han-k59/astap (main; upstream PR han-k59/astap#2), whose command-line
# solver spreads the FITS load, binning, star detection and the spiral database search over
# every core. On a 4-core Pi a hinted solve of a 26 MP frame drops from 1.6 s to 0.7 s and a
# blind solve from 12 s to 4 s against the stock binary; with ASTAP_THREADS=1 (or -threads 1)
# the output is bit-identical to upstream. MPL-2.0, see NOTICE.md.
#
# The daemon runs it as <publish dir>/astap_cli (the .deb installs it at
# /opt/openastroara/astap_cli, the Docker image at /app/astap_cli), passing the profile's star
# database directory with -d. The database itself (D80 and friends) is still the user's download
# (docs/DEPLOY.md step 3).
#
# Usage: scripts/build-astap-cli.sh <output-dir>
# Needs the Free Pascal compiler (Debian/Ubuntu: apt install fpc) for the TARGET architecture:
# fpc has no cross units in the distro packages, so CI builds it inside an arm64 container
# (qemu) rather than cross-compiling from the x64 runner. FPC=<path> overrides the compiler.
set -euo pipefail

OUT="${1:?usage: build-astap-cli.sh <output-dir>}"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"

SRC=third_party/astap/command-line_version
if [ ! -f "$SRC/astap_command_line.lpr" ]; then
    echo "error: $SRC is empty — the solver source is a git submodule; run" >&2
    echo "       git submodule update --init" >&2
    exit 2
fi
FPC="${FPC:-fpc}"
command -v "$FPC" > /dev/null || { echo "error: Free Pascal compiler '$FPC' not found (apt install fpc)" >&2; exit 2; }

# Per-arch tuning mirrors the project's astap_command_line_linux_<arch>.lpi files: -O3 everywhere,
# ARMv8 code generation on aarch64. Everything else is what lazbuild derives from the .lpi
# (Delphi syntax mode, long strings, smart linking, cthreads via the -dUseCThreads define).
ARCH_OPTS=()
case "$("$FPC" -iTP)" in
    aarch64) ARCH_OPTS=(-CpARMV8 -OpARMV8) ;;
esac

# Build in a scratch directory: fpc writes .o/.ppu next to the sources otherwise, and stale
# objects in the submodule break the next build.
BUILD="$(mktemp -d)"
trap 'rm -rf "$BUILD"' EXIT
(
    cd "$SRC"
    "$FPC" -Mdelphi -Sh -Si -O3 "${ARCH_OPTS[@]}" -XX -Xs -dUseCThreads -vewn \
        -FU"$BUILD" -FE"$BUILD" -B astap_command_line.lpr -oastap_cli
)
install -m 0755 "$BUILD/astap_cli" "$OUT/astap_cli"
echo "built: $OUT/astap_cli ($(du -h "$OUT/astap_cli" | cut -f1))"
