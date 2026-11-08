# A GPS receiver goes on the client, never the rig

Status: accepted

## Context

The daemon used to probe for a USB GPS by opening every `/dev/ttyUSB*` and `/dev/ttyACM*` at 9600 baud every few minutes. That included the port AlpacaBridge held for a serial mount: the mount link faulted and the tty was left at the wrong speed. The Pi's USB ports are also the scarce resource on a rig.

## Decision

We decided (2026-10-11, [#1305](https://github.com/open-astro/openastro-ara/issues/1305)) that the daemon never reads a GPS receiver. A USB GPS dongle goes on the computer running the client, which reads the NMEA itself and pushes the fix as `gps-client`. The daemon also accepts `gps-mobile`, which no client sends yet. A user who wants the Pi's clock disciplined with no client attached runs gpsd and chrony. Ara stays out of serial ports it does not own.

## Rules

1. The daemon opens no serial port to look for a GPS receiver; a fix reaches it only as a pushed time with source `gps-client` or `gps-mobile`. Scope: `OpenAstroAra.Server/Services/TimeSyncService.cs`, `OpenAstroAra.Server/**/*.cs`. Checked by: the time sync tests "Gps_client_is_an_external_gps_at_high_trust" and "Gps_mobile_maps_to_the_gps_external_state_source", and review (a serial port or tty opened by the daemon for anything but a device it was told to use).
2. The client reads the receiver on its own computer and pushes the fix as `gps-client`. Scope: `client/openastroara_client/lib/state/client_gps_state.dart`, `client/openastroara_client/lib/services/serial_gps_source.dart`. Checked by: the client GPS state test "enabled with a port: pushes the receiver time and fix as gps-client".

## Alternatives rejected

- **Probe for GPS on the rig, skipping ports in use**: keeps the rig self-sufficient, but the daemon cannot know which tty a bridge is about to open, and a probe at the wrong moment still breaks the mount link.
- **A configured GPS port on the rig**: no probing, but it spends a scarce USB port and duplicates what gpsd already does well.

## Consequences

- The client must be connected for a GPS fix to reach the rig; with no client the rig's clock comes from gpsd and chrony or the last push.
- The time-sync state has no `gps-internal` source and `GET /server/time-sync` has no `internal_gps_available`.

## How to reverse

Reading GPS on the rig again means a serial worker in the daemon that coordinates port ownership with AlpacaBridge, a `gps-internal` source in the time-sync state and wire contract, and client logic that skips its relay when the rig has its own fix.

## Links

- [`design/API_CONTRACT.md`](../../design/API_CONTRACT.md), entry "2026-10-11 — The daemon never reads a GPS receiver; the dongle lives on the client (#1305)"
- [`design/PORT_PLAYBOOK.md`](../../design/PORT_PLAYBOOK.md) §31 (time and location sync)
- [#1305](https://github.com/open-astro/openastro-ara/issues/1305)
