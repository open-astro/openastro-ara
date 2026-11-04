#!/usr/bin/env bash
# Builds the bundled ASTAP command-line plate solver (astap_cli) from the git submodule under
# third_party/astap: branch `ara` of open-astro/astap, which is Han Kleijn's han-k59/astap `main`
# (multi-core spiral search since v2026.11.01) plus an OpenAstro patch that also spreads the
# FITS load, binning and star detection over every core (OPENASTRO.md in the submodule; merged
# with Han's updates, not sent upstream). On the 4-core CM4 with a 26 MP frame a hinted solve
# takes 0.63 s against Han's 1.60 s and a blind solve 2.7 s against 3.7 s, with output
# identical to Han's build at any -threads value. MPL-2.0, see NOTICE.md.
#
# The daemon runs it as /opt/openastroara/astap_cli (the .deb and the Docker image both put
# the daemon directory there; scripts/tests/test_astap_cli_bundle_guards.py pins it), passing the profile's star
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
# Debian's fpc is unpinned: record which compiler produced the binary so a codegen change after a
# distro bump can be read off the build log.
echo "fpc $("$FPC" -iV) for $("$FPC" -iTP)-$("$FPC" -iTO)"

# Per-arch tuning mirrors the project's astap_command_line_linux_<arch>.lpi files: -O3 everywhere,
# ARMv8 code generation on aarch64. Everything else is what lazbuild derives from the .lpi
# (Delphi syntax mode, long strings, smart linking). The program links cthreads on Unix itself.
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
    "$FPC" -Mdelphi -Sh -Si -O3 "${ARCH_OPTS[@]}" -XX -Xs -vewn \
        -FU"$BUILD" -FE"$BUILD" -B astap_command_line.lpr -oastap_cli
)
install -m 0755 "$BUILD/astap_cli" "$OUT/astap_cli"
echo "built: $OUT/astap_cli ($(du -h "$OUT/astap_cli" | cut -f1))"
