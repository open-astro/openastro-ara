#!/usr/bin/env bash
# Build, test, and install the ARA Linux Flutter client on this computer.
set -Eeuo pipefail
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
exec "$script_dir/update-observatory.sh" --client-only "$@"
