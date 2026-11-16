### Fixed

- **Polar Align no longer turns tracking off behind a sequence** (Polar Align, Sequencer, #1946): when a sequence's Set Tracking or a tracking command from the client writes tracking while a Polar Align run is open, the run leaves tracking as that write set it instead of restoring its start-of-run state, and starting a sequence run now stops an open Polar Align run (state `stopped`) before the first instruction, storing no alignment result for it.
