# A headless daemon on the rig and a Flutter client

Status: accepted

## Context

Ara is a port of NINA, a Windows WPF desktop application that drives the equipment from the same process that draws the screens. The rig computer Ara targets is a Raspberry Pi on the mount, often with no display, while the person watches from a laptop, a tablet or a phone indoors. The imaging run has to keep going when that screen sleeps, roams off the Wi-Fi or is closed.

## Decision

We decided during the port ([`design/PORT_PLAYBOOK.md`](../../design/PORT_PLAYBOOK.md) §2) that Ara is two programs: a headless .NET daemon on the rig that owns the equipment, the sequencer and the files, and a Flutter client on every other device that owns every screen. They talk only over HTTP on the LAN: REST operations under `/api/v1` and one WebSocket at `/api/v1/stream` for live events. The committed `OpenAstroAra.Server/openapi.yaml` is the REST contract, and `design/API_CONTRACT.md` holds the reasoning for each wire shape and the WebSocket protocol.

## Rules

1. The daemon draws no user interface: every screen lives in the Flutter client, and the daemon serves only the REST API, its documentation and the one WebSocket at `/api/v1/stream`. Scope: `OpenAstroAra.Server/Program.cs`, `OpenAstroAra.Server/Endpoints/*.cs`, `OpenAstroAra.Server/OpenAstroAra.Server.csproj`. Checked by: review (a UI framework reference, a served page other than the API docs, or a second WebSocket route).
2. `OpenAstroAra.Server/openapi.yaml` matches the routes the daemon maps, and a PR that adds or changes a route commits the regenerated file. Scope: `OpenAstroAra.Server/openapi.yaml`, `OpenAstroAra.Server/Endpoints/*.cs`. Checked by: the OpenAPI contract snapshot test "The_committed_spec_matches_what_the_daemon_generates".
3. A PR that adds or changes a wire shape appends its reasoning to `design/API_CONTRACT.md`. Scope: `OpenAstroAra.Server/Contracts/*.cs`, `OpenAstroAra.Server/Endpoints/*.cs`. Checked by: review (a changed DTO or route with no new `design/API_CONTRACT.md` entry in the same diff).
4. The client reaches the daemon only through the REST API and the WebSocket: no SSH, no shared folder, no direct file or database access on the rig. Scope: `client/openastroara_client/lib/services/*.dart`. Checked by: review (a client service that opens a connection to the rig other than HTTP or the stream).

## Alternatives rejected

- **Keep NINA's desktop app and remote into it** (VNC or RDP to the Pi): no port needed, but the screen is a desktop squeezed onto a phone, the Pi spends its CPU drawing it, and the session dies with the remote link.
- **A web UI served by the daemon**: one codebase and no install, but the browser cannot read a USB GPS, hold the planetarium offline or survive the rig being off, and the Pi would serve every asset.
- **gRPC or a custom socket protocol**: typed and compact, but browsers, scripts and curl cannot speak it, and HTTP plus JSON is what ASCOM Alpaca and the rest of the hobby already use.

## Consequences

- The run lives on the rig and survives the client sleeping, roaming or closing; a client that comes back reads the current state over REST.
- Every feature crosses the wire, so each one costs a route, a DTO, a contract entry and a snapshot refresh.
- The client works with no rig for anything that needs no equipment (planning, the album, the planetarium), see ADR 0004.
- The daemon has no login and no TLS, see ADR 0005.

## How to reverse

Moving screens into the daemon means a UI stack on the Pi and a second copy of every screen; moving equipment control into the client means the run stops when the client does. Either way every route in `openapi.yaml` and every client service changes.

## Links

- [`design/PORT_PLAYBOOK.md`](../../design/PORT_PLAYBOOK.md) §2 (target stack), §60 (API conventions)
- [`design/API_CONTRACT.md`](../../design/API_CONTRACT.md)
- [#1131](https://github.com/open-astro/openastro-ara/issues/1131) (the OpenAPI snapshot test)
