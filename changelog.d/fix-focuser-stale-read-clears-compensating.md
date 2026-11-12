### Fixed (focuser)
- **A backlash-compensated move keeps reporting `backlash_compensating` until it settles**: a refresh read that started before the move was dispatched, while the focuser was still idle, could end the compensating state as soon as it landed, so the rest of the move showed plain `moving`. Only a read taken after the move call returns can end it now.
