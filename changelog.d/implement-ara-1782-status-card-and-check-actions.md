### Changed (status card)
- Simple Mode shows what the rig is doing and what it needs from you in one card at the top left: the status lines first, then the messages, each behind a hairline. The card is as wide as its widest line up to 420 px, 420 px while a question holds actions, and the whole row on a phone. Every status line shows (no "+N" fold for them); "+N more" folds messages only. The camera status no longer sits top right (#1782).
- Check says what to do when it can't run: a missing camera still opens Camera settings, a busy camera says to Check again once it has read out, and a stuck capture gate says to restart the daemon. `POST /api/v1/platesolve/verify-position` answers 409 with a `reason` (`no-camera`, `camera-busy`, `gate-stuck`) (#1786).

### Fixed (status card)
- Simple Mode's chips are 44 px tall, ink included, on macOS, Windows, Linux and iOS (#1791).
