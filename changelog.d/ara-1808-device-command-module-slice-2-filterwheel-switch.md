### Changed (equipment)

- **Filter wheel changes and switch writes share the device-command module** (#1808): the Sequencer's filter changes and switch writes now share the module's timeout and fault handling with the rotator, the dome and the focuser. The timeouts and fault kinds are unchanged. A filter change that fails or is not confirmed now reports the module's message ("filter change to position 2 was not confirmed settled within the 60s bound"), and a switch write that outlasts its timeout now logs the abandoned call.
