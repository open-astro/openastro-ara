# The Linux desktop client is x86-64 and Wayland only

Status: accepted

## Context

The Linux client embeds the planetarium as a native overlay inside the Flutter window. Making it draw in the right order, resize without dead strips, and show the window icon took separate code paths on X11 and on Wayland, and each path broke in its own way on each desktop. The mainstream distributions now default to Wayland sessions.

## Decision

We decided (2026-09-29, epic [#1204](https://github.com/open-astro/openastro-ara/issues/1204); the runner landed 2026-10-05 in [#1275](https://github.com/open-astro/openastro-ara/issues/1275) and [#1201](https://github.com/open-astro/openastro-ara/issues/1201)) that the Linux desktop client supports x86-64 on Wayland only. It is tested on Ubuntu and Kubuntu 24.04 LTS, the current Fedora KDE and Arch, one distribution per family, with a glibc floor of 2.39. The client refuses an X11 session at startup with a plain message. The daemon stays an arm64 Debian `.deb` and is not affected.

## Rules

1. The Linux runner refuses to start when `WAYLAND_DISPLAY` is unset and restricts GDK to the Wayland backend. Scope: `client/openastroara_client/linux/runner/main.cc`. Checked by: review (the `WAYLAND_DISPLAY` check or `gdk_set_allowed_backends("wayland")` removed or weakened).
2. Linux client code carries no X11 path: no Xlib or XWayland-specific branches. Scope: `client/openastroara_client/linux/runner/*.cc`, `client/openastroara_client/linux/runner/*.h`. Checked by: review (an `X11` include, a GDK X11 call, or a branch on the display backend).

## Alternatives rejected

- **X11 and Wayland both**: covers older and NVIDIA-legacy setups, but doubles the overlay, window chrome and icon code, and each path broke on a different desktop.
- **X11 only, or Wayland through XWayland**: one path, but the path the distributions are leaving, and the overlay relies on GtkOverlay draw order on a Wayland surface.
- **arm64 Linux desktop**: would let the client run on the Pi itself, but no runner tests it and the rig is meant to be driven from another device.

## Consequences

- Users on an X11 session log into a Wayland one; the error says so.
- The supported matrix is three distribution families on x86-64, which CI and manual testing can cover.

## How to reverse

Supporting X11 again means restoring the X11 window chrome, native subwindow and icon paths in the runner and the planetarium overlay, and adding X11 sessions to the test matrix.

## Links

- [#1204](https://github.com/open-astro/openastro-ara/issues/1204), [#1201](https://github.com/open-astro/openastro-ara/issues/1201), [#1275](https://github.com/open-astro/openastro-ara/issues/1275)
- [`client/openastroara_client/linux/runner/main.cc`](../../client/openastroara_client/linux/runner/main.cc)
