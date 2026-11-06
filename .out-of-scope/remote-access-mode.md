# Remote-access mode (TLS, token auth, rate limiting)

Ara has no remote-access mode. The daemon serves its API without
authentication or TLS, on the LAN only. To reach a rig from outside its
network, use a VPN.

## Why this is out of scope

The security model (playbook §67) matches ASCOM Alpaca and ZWO ASIAir: Ara
runs on a trusted LAN, the API has no authentication, and the people on that
network are imaging peers. Every client and every endpoint is built on that
assumption.

A remote mode would not be a bolt-on. It needs TLS termination and
certificate handling, token issue and storage in every client, rate limiting,
and a second listening interface so LAN access stays unauthenticated. That
touches every endpoint and every client connection path, and all of it
becomes attack surface on the internet.

A VPN (WireGuard, Tailscale, or the router's own) already gives encrypted,
authenticated access to the whole LAN, and Ara works over it unchanged. That
covers the remote-observatory case without Ara carrying an internet-facing
security stack.

Encrypted database backups were tied to this decision (#1175). They stay
unbuilt for the same reason: on a trusted LAN the backup zip doesn't need its
own encryption, and SQLCipher would not cover the zip anyway.

## Prior requests

- #1163: "TLS / remote-internet access mode, and the API auth decision it forces (§67.4)"
