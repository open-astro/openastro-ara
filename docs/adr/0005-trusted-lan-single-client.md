# A trusted LAN with one controlling client

Status: accepted

## Context

Imagers run their rigs in the backyard, at a remote observatory they own, or on a shared star-party field. In every case the others on the network are imaging peers, and ASCOM Alpaca, ASIAir and NINA-over-Alpaca all serve an open API there. Two clients driving the same rig at once is the risk people actually hit: a laptop and a phone both sending slews or both starting a run.

## Decision

We decided during the port ([`design/PORT_PLAYBOOK.md`](../../design/PORT_PLAYBOOK.md) §27 and §67) that the daemon serves an open API on the LAN with no authentication and no TLS, the same posture as Alpaca and ASIAir, and that one client controls the rig at a time. A second client asks the current one to hand over, and a client that was taken over can no longer change anything. On 2026-11-06 ([#1163](https://github.com/open-astro/openastro-ara/issues/1163)) we decided that a remote-access mode with TLS and tokens is not planned: a VPN is the supported way to reach a rig from outside its network.

## Rules

1. The daemon's API has no authentication, login or token check, and listens on plain HTTP. Scope: `OpenAstroAra.Server/Program.cs`, `OpenAstroAra.Server/Endpoints/*.cs`. Checked by: review (an auth middleware, a credential check on a route, a login route, or a TLS listener).
2. One client holds the control session; a second client's connect goes through the holder's allow or reject. Scope: `OpenAstroAra.Server/Services/ClientSessionService.cs`, `OpenAstroAra.Server/Endpoints/ConnectionEndpoints.cs`. Checked by: the client session tests "Takeover_allow_grants_new_client_and_closes_old_socket_with_4004" and "Takeover_reject_returns_rejected_with_holder_hostname_and_keeps_holder".
3. A mutating request that presents a session id other than the holder's is refused with 409; reads, connect, disconnect and the emergency stop are never refused. Scope: `OpenAstroAra.Server/Endpoints/SessionGuard.cs`. Checked by: the API hardening tests "A_mutating_request_with_a_stale_session_is_refused", "Reads_are_never_refused" and "Connect_and_disconnect_are_exempt_and_so_is_everything_outside_the_api".

## Alternatives rejected

- **Token authentication on the LAN**: stops a casual stranger, but every client and script needs pairing, a lost token locks the user out of their rig at night, and no comparable imaging tool asks for it.
- **A remote-access mode with TLS and tokens** (designed in §67.4, shelved by #1163): reaching a rig over the internet, but a second security surface to maintain where a VPN already does the job.
- **Many clients at once**: watch from the phone while the laptop drives, but conflicting commands need arbitration on every route; a read-only spectator mode stays possible later.

## Consequences

- Anyone on the LAN can call the API; the session policy is about who drives, not who is allowed.
- A request with no session header stays on the advisory path so scripts and older clients keep working ([#1194](https://github.com/open-astro/openastro-ara/issues/1194)).
- Destructive actions rely on confirmation in the UI (typing the drive label to reformat, a hold on Stop) rather than on identity.
- Reaching a rig from outside the LAN is the user's VPN.

## How to reverse

Adding authentication means pairing in every client and the wizard, a credential store on the rig, a recovery path for a lost credential, and an auth check on every route in `openapi.yaml`. Allowing several controlling clients means arbitration in every mutating route.

## Links

- [`design/PORT_PLAYBOOK.md`](../../design/PORT_PLAYBOOK.md) §27 (connection policy), §67 (security model)
- [`.out-of-scope/remote-access-mode.md`](../../.out-of-scope/remote-access-mode.md)
- [#1163](https://github.com/open-astro/openastro-ara/issues/1163), [#1194](https://github.com/open-astro/openastro-ara/issues/1194)
