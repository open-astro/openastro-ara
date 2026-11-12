# The daemon holds no root privilege, and root work runs in polkit-scoped helper units

Status: accepted

## Context

The daemon's API is open to the LAN ([0005](0005-trusted-lan-single-client.md)), and some of what it offers needs root: formatting, checking and mounting the storage drive, installing a `.deb` the client pushed, restarting the guider, and setting the system clock. The unit `openastroara-server.service` runs as the `openastroara` user with `NoNewPrivileges=true`. Under that flag `sudo` and every other setuid binary fail ("no new privileges flag is set", seen on a Pi), so a sudoers entry cannot work from the packaged daemon.

## Decision

We decided during the port ([`design/PORT_PLAYBOOK.md`](../../design/PORT_PLAYBOOK.md) §13, §29.1.4, §33, §63.3; update flow in [#1122](https://github.com/open-astro/openastro-ara/issues/1122)) that the daemon never holds root and never gains it. Root work has one shape. The daemon writes a request file under `/run/openastroara/`, then asks systemd to start a root-owned oneshot unit (`openastroara-storage@<id>.service`, `openastroara-update@<id>.service`). A polkit rule allows the `openastroara` user that unit template and the `start` verb, and nothing else. The helper reads the request, does the work, and leaves a result file for the daemon. The guider is a sibling service, so its rule names one unit, `openastro-guider.service`, and the verbs `start` and `restart`.

The one privilege the daemon itself holds is `CAP_SYS_TIME`, to set the clock from the client's time push. The unit's capability bounding set is that one capability.

A sudoers line for `configure-storage.sh` still ships. The packaged daemon cannot reach it; a dev rig with no packaged unit falls back to it.

## Rules

1. The daemon's unit runs as the `openastroara` user with `NoNewPrivileges=true`, and its `CapabilityBoundingSet` lists the same capabilities as its `AmbientCapabilities`. Scope: `packaging/debian/etc/systemd/system/openastroara-server.service`. Checked by: review (the `User=`, `NoNewPrivileges=`, `CapabilityBoundingSet=` and `AmbientCapabilities=` lines of the unit).
2. Work that needs root runs in a root-owned oneshot unit that the daemon asks systemd to start, never in the daemon and never through `sudo`, `pkexec` or a setuid binary. Scope: `OpenAstroAra.Server/Services/StorageDeviceService.cs`, `OpenAstroAra.Server/Services/ServerUpdateService.cs`, `OpenAstroAra.Server/Services/GuiderProcessSupervisor.cs`, `packaging/debian/etc/systemd/system/openastroara-*@.service`. Checked by: review (a new privileged action adds a helper unit and a polkit rule, and no `sudo` call on the packaged path).
3. A polkit rule answers `YES` only for the `openastroara` user, the `org.freedesktop.systemd1.manage-units` action, one named unit or unit template, and the verbs that unit needs; every other case returns `NOT_HANDLED`. Scope: `packaging/debian/usr/share/polkit-1/rules.d/*.rules`. Checked by: review (the four tests in each rule, and no `stop`, `mask` or `edit` verb).
4. A helper takes its parameters from the request file named by its instance id, and refuses a bad id, a symlink and an oversized request before it runs anything. Scope: `packaging/debian/opt/openastroara/scripts/storage-request.sh`, `packaging/debian/opt/openastroara/scripts/update-request.sh`. Checked by: `scripts/tests/test_update_request.py` (`test_bad_ids_symlinks_and_oversized_requests_are_refused_without_running_the_helper`, `test_a_planted_symlink_at_the_result_name_is_replaced_not_followed`) and `OpenAstroAra.Test/StorageExchangeRoundTripTest.cs`.
5. The sudoers file grants the `openastroara` user one command, `configure-storage.sh`, with `env_reset` kept. Scope: `packaging/debian/etc/sudoers.d/openastroara`. Checked by: review (one `NOPASSWD` line, no `SETENV` and no `env_keep`).

## Alternatives rejected

- **Run the daemon as root**: no helper units and no polkit rules, but every route of an API with no authentication would run with full privilege.
- **A sudoers entry per privileged command**: the first storage flow was built this way. It cannot work under `NoNewPrivileges=true`, and dropping the flag would drop it for everything the daemon runs.
- **Pass the parameters on the helper's command line**: simpler than a request file, but the daemon would be building a root command line from request data. The unit's `ExecStart` is fixed and takes only the instance id.

## Consequences

- A new privileged action costs a helper script, a unit, a polkit rule and tests, where one sudoers line would have done.
- The package depends on `polkitd`; storage setup, the pushed update and guider restart all go through it.
- A helper's outcome travels through a result file, so the daemon has to handle a helper that died without writing one.
- The storage and update rules match a unit template by prefix and suffix, so any instance id passes polkit; the helper's own check of the id is what refuses a bad one.
- Rules 1, 2, 3 and 5 are checked by review. A script that reads the unit, the rules files and the sudoers file would turn them into a failing check.

## How to reverse

To let the daemon use `sudo`: remove `NoNewPrivileges=true` from the unit and add a sudoers line per command. The three polkit rules, the two helper units and the request and result file handling in `StorageDeviceService` and `ServerUpdateService` then go. To run the daemon as root, drop the `User=` line as well; the hardening block of the unit stops meaning much at that point.

## Links

- [`design/PORT_PLAYBOOK.md`](../../design/PORT_PLAYBOOK.md) §13 (unit hardening), §29.1.4 (storage helper), §33 (pushed update), §63.3 (guider restart)
- [#1122](https://github.com/open-astro/openastro-ara/issues/1122)
- [A trusted LAN with one controlling client](0005-trusted-lan-single-client.md)
