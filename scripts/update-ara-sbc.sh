#!/usr/bin/env bash
# Build and update ARA, AlpacaBridge, and OpenAstro Guider on the SBC over SSH/Wi-Fi.
set -Eeuo pipefail
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
exec "$script_dir/update-observatory.sh" --sbc-only "$@"
