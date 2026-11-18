### Changed (guider faults)

- **Guider faults show up as equipment faults.** A guider link drop, a guide camera drop and a guide star lost for more than 30 s now publish `equipment.fault` with `device_type` `guider` (details `link_down`, `equipment_disconnected`, `star_lost`), and your "on guider lost" policy runs through the same fault reaction as the other devices: the runs pause, skip or abort, and a pause ends when the guider recovers, which resumes exactly the runs it paused (#1588).
- **New `skipped` fault action.** With "skip target" as the policy, `equipment.fault_action_taken` reports `skipped` with `skipped_runs` (#1588).
- **A guide camera that drops and returns again starts a new fault each time.** Before, the policy ran once per guider connection; now each drop after a recovery pauses, skips or aborts again (#1588).

### Removed (guider faults)

- **The `guider.fault_action_taken` WebSocket event is gone.** Nothing in the app read it; guider faults use `equipment.fault` and `equipment.fault_action_taken` (#1588).
