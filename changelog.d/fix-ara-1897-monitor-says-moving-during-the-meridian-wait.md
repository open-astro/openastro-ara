### Fixed (Simple Mode)

- **Monitor says "Waiting for flip" during the meridian wait, not "Moving…"** (#1897): while the pass-meridian countdown runs, the top line, the Now tile and the Flip tile show "Waiting for flip" with the countdown. Before, the daemon's `flipping` state won and the Flip tile said "Moving…" while the mount stood still. When the countdown ends, "Flipping" and "Moving…" show as before.
