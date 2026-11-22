### Fixed

- **The Disconnect Equipment instruction now disconnects any connected device** (it reported "Failed to disconnect" for every role but the guider before), and Disconnect All Equipment disconnects each role the same way (Sequencer, #1545). A Sequencer disconnect now counts as a user disconnect, so the Reconnect trigger leaves the device alone.
