### Added (decision records)

- Four decision records in `docs/adr/` for choices the code already follows: 0017 says the daemon holds no root privilege and root work runs in polkit-scoped helper units, 0018 says a sequence is stored in NINA's JSON format and NINA's engine runs it, 0019 says the planetarium is the Stellarium Web Engine in the platform's native webview, and 0020 says guiding is done by a separate guider service that the daemon drives over the PHD2 protocol.
