### Fixed (Time sync)

- **A time push from the client can set the rig clock** (#1944): the packaged service unit's syscall filter left out `clock_settime`, so every push ended `clockSet=False` even with `CAP_SYS_TIME`. The unit now allows `@clock`, and the warning names the syscall filter as a cause as well as the missing capability.
