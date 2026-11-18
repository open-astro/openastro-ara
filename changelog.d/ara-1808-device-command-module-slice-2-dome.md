### Changed (equipment)

- **Dome commands share the device-command module** (#1808): shutter open and close, park, find home and slew to azimuth now run through the same command module as the rotator, with the same timeouts and fault kinds. A command that has already finished now returns one poll interval (100 ms) later, because the module waits one poll before it first checks.
