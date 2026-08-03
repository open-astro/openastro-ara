#!/bin/sh
# §77.1 capture tuning — the entire privilege surface for usbfs sizing.
# Invoked by the daemon as: sudo /opt/openastroara/scripts/set-usbfs-memory.sh <MB>
# Validates the argument, applies it live, and persists it for boot via
# modprobe.d (§34.3). Installed 0750 root:openastroara by the .deb.
set -eu

MB="${1:-}"
case "$MB" in
    ''|*[!0-9]*) echo "usage: $0 <MB in [16,1000]>" >&2; exit 2 ;;
esac
if [ "$MB" -lt 16 ] || [ "$MB" -gt 1000 ]; then
    echo "value out of range [16,1000]: $MB" >&2
    exit 2
fi

SYSFS=/sys/module/usbcore/parameters/usbfs_memory_mb
if [ -w "$SYSFS" ]; then
    printf '%s\n' "$MB" > "$SYSFS"
fi

CONF=/etc/modprobe.d/openastroara-usbfs.conf
printf 'options usbcore usbfs_memory_mb=%s\n' "$MB" > "$CONF"
chmod 0644 "$CONF"
