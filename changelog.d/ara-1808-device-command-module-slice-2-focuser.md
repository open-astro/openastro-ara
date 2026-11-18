### Changed (equipment)

- **Focuser moves share the device-command module** (#1808): absolute, relative and temperature moves now share the module's timeout and fault handling with the rotator and the dome. The timeouts, fault kinds and failure messages are unchanged.
