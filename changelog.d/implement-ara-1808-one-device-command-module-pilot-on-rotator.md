### Changed (internal)

- **Sequencer rotator moves now run through one shared command module** (daemon, #1808): the hard timeout, the settle poll, the stall verdict, the fault publish and the abandoned-call handling live in `DeviceCommand` instead of the rotator mediator, with tests for the timeout and the stall against the fake device adapter. Move behaviour, fault kinds and messages do not change.
