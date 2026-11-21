### Fixed

- **The Disconnect Equipment instruction now disconnects a Safety Monitor or Rotator** it reported "Failed to disconnect" on before, and Disconnect All Equipment disconnects every connected device the same way (Sequencer, #1545). A Sequencer disconnect now counts as a user disconnect, so the Reconnect trigger leaves the device alone.
