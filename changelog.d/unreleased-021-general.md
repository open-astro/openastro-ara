### Removed

- The daemon's USB-GPS serial probe (#1305): it opened the mount's serial port and knocked a serial mount offline. A GPS dongle now goes on the computer running the client (Settings → Site → GPS on this computer); `internal_gps_available` is gone from `GET /server/time-sync`.
