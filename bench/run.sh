#!/usr/bin/env bash
# bench/run.sh — run the §42.2 virtual-observatory bench on the Linux/arm64 lane
# (bench-5). Builds the hermetic bench image and runs the hardware-free bench
# suite (AlpacaFaultProxy + FakeGuider + guider fault scenarios) inside a
# linux/arm64 .NET SDK container, isolated from the host build tree.
#
# Requires a running arm64 Docker engine (e.g. colima on Apple Silicon:
# `colima start --arch aarch64`). Exits with the test pass/fail code.
#
# `--build` is passed so a source edit is always picked up. When you're only
# re-running the same suite (no source change), drop it for a cached image:
#   docker compose -f bench/docker-compose.yml run --rm bench
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
compose="${repo_root}/bench/docker-compose.yml"

# Prefer the modern `docker compose` plugin; fall back to the standalone
# `docker-compose` binary (what some colima/Homebrew setups ship).
if docker compose version >/dev/null 2>&1; then
    exec docker compose -f "${compose}" run --rm --build bench
elif command -v docker-compose >/dev/null 2>&1; then
    exec docker-compose -f "${compose}" run --rm --build bench
else
    echo "error: need either the 'docker compose' plugin or the 'docker-compose' binary" >&2
    exit 1
fi
