### Fixed (Server)

- **Restarting the daemon from the client now works on the packaged rig** (#1858): `POST /api/v1/server/restart` used to run `systemctl restart`, which the `openastroara` user may not do, so nothing happened. After the 2 s notice the daemon now stops itself with exit code 75 and the unit's `Restart=on-failure` starts it again. A Docker container run without a restart policy (`docker run --rm`) now stops on Restart, since there is no supervisor to start it again; run it with `--restart on-failure` to keep the restart button working.
