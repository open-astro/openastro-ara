# Guiding is done by a separate guider service that the daemon drives over the PHD2 protocol

Status: accepted

## Context

A long exposure needs a guide loop: read the Guide Camera, find the star, correct the mount, several times a second for hours. NINA does not guide. It ships clients for outside guiding programs (PHD2, MetaGuide, SkyGuard) and a simple built-in pulse guider. Ara's daemon is headless, so whatever guides has to run on the rig with no screen.

openastro-guider is OpenAstro's fork of PHD2. It runs headless as its own systemd service, reaches its camera and mount through Alpaca, and answers PHD2's JSON-RPC protocol on a local port (4400 by default).

## Decision

We decided during the port ([`design/PORT_PLAYBOOK.md`](../../design/PORT_PLAYBOOK.md) §0 rule 2 and §63) that the daemon does not guide. A separate guider service does, and the daemon is its client. The daemon connects to it with NINA's PHD2 client (`PHD2Guider`), over PHD2's JSON-RPC protocol, at the host and port in the profile. Everything Ara does with guiding goes through that connection: start, stop and dither, calibration, the Guiding Assistant, the dark library, profile pushes and the Polar Align Routine.

The guider is a sibling package and unit, not part of the daemon. The daemon's `.deb` lists `openastro-guider` under `Recommends` and ships no guider unit. The daemon may start and restart `openastro-guider.service` for crash recovery and may not stop it ([0017](0017-the-daemon-holds-no-root-privilege.md)).

The client never talks to the guider. Every guider action and every guide frame reaches the client through the daemon's REST and WebSocket API.

The guider may be stock PHD2 or openastro-guider. The daemon works out which one at connect, from the fork key of `get_version`, then the `Version` event, then the legacy version markers.

## Rules

1. The daemon holds no guide loop: no star detection for guiding and no computed mount correction in the daemon's own code. Scope: `OpenAstroAra.Server/Services/Guider*.cs`, `OpenAstroAra.Equipment/Equipment/MyGuider/PHD2/*.cs`. Checked by: review (a change that computes a guide correction from guide frames).
2. The daemon reaches the guider only through `PHD2Guider` and its JSON-RPC methods. Scope: `OpenAstroAra.Server/Services/GuiderService.cs`, `OpenAstroAra.Equipment/Equipment/MyGuider/PHD2/*.cs`. Checked by: `OpenAstroAra.Test/Phd2MethodSerializationTest.cs` and `OpenAstroAra.Test/FakeGuiderTest.cs`, and review (a second guider client, or a call to the guider outside the adapter).
3. The client never opens a connection to the guider; guider state, actions and frames come from the daemon. Scope: `client/openastroara_client/lib/**/*.dart`. Checked by: review (a socket or HTTP call from the client to the guider's host or port).
4. The daemon's package ships no guider unit and names the guider under `Recommends`; the daemon asks systemd only to start or restart `openastro-guider.service`. Scope: `packaging/debian/DEBIAN/control.template`, `OpenAstroAra.Server/Services/GuiderProcessSupervisor.cs`, `packaging/debian/usr/share/polkit-1/rules.d/50-openastroara-guider.rules`. Checked by: `OpenAstroAra.Test/SystemctlGuiderProcessSupervisorTest.cs`, and review (the verbs in the polkit rule).
5. The guider's identity is resolved at connect from the `get_version` fork key, then the `Version` event, then the legacy version markers, in that order. Scope: `OpenAstroAra.Equipment/Equipment/MyGuider/PHD2/PHD2Guider.Fork.cs`. Checked by: `OpenAstroAra.Test/PHD2GuiderForkTest.cs`.

## Alternatives rejected

- **A guide loop inside the daemon**: one process and one camera owner, and no protocol between them. It means writing and tuning star detection, guide algorithms and calibration that PHD2 already has. The playbook's rule for the port is to take NINA's PHD2 client as it is and not to improve working code.
- **NINA's other guider clients (MetaGuide, SkyGuard, the direct pulse guider)**: their source files came across with the port and nothing in the daemon registers them. No document says why.
- **Let the client talk to the guider**: fewer hops for the live view. `design/API_CONTRACT.md` states the rule ("never client → guider") and gives no reason; the daemon's default guider address is `localhost`.
- **Ship the guider inside the daemon's package**: one install. The guider has its own repository and releases, and a rig may run stock PHD2 instead.

## Consequences

- The rig runs a second service with its own package, unit, user and logs, and the bug report bundle has to collect from both.
- Two programs want the Guide Camera, so the daemon needs one rule for who holds it ([#1809](https://github.com/open-astro/openastro-ara/issues/1809)).
- What Ara can do with guiding is bounded by the guider's RPC surface. A feature that needs a new method lands in openastro-guider first; the Guiding Assistant did ([#1364](https://github.com/open-astro/openastro-ara/issues/1364)).
- The guider is a `Recommends`, so the daemon has to run on a rig that has none.
- NINA's unused guider clients remain in `OpenAstroAra.Equipment/Equipment/MyGuider/`.

## How to reverse

Moving guiding into the daemon means a guide loop, calibration, the guide algorithms and a dark library in the daemon, a new owner for the Guide Camera, and a rewrite of every caller of `PHD2Guider`: the sequencer's guider mediator, the Polar Align Routine, the Guiding Assistant and the guider live view. Supporting a second outside guider is smaller: a second adapter behind `IGuider` and a choice in the profile.

## Links

- [`design/PORT_PLAYBOOK.md`](../../design/PORT_PLAYBOOK.md) §63 (guider lifecycle, profile and dark library push)
- [Equipment is controlled only through ASCOM Alpaca](0003-alpaca-only-equipment.md), [The daemon holds no root privilege](0017-the-daemon-holds-no-root-privilege.md)
- [#1364](https://github.com/open-astro/openastro-ara/issues/1364) (guider features driven from Ara), [#1809](https://github.com/open-astro/openastro-ara/issues/1809) (Guide Camera custody)
