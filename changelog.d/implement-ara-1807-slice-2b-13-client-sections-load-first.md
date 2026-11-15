### Fixed

- **Notifications, file names and diagnostics mode no longer show or save made-up defaults** (Settings, #1807): the three panels say "Not loaded" with a Retry button when the rig does not answer, and Save sends the copy it loaded, so another device's newer save is not overwritten (the panel shows a Reload banner instead). A diagnostics mode choice that the rig fails to save goes back to the mode the rig holds.
- **Screen readers hear the diagnostics modes as one choice** (Settings, #1807): the three modes are one radio group with the selected mode checked; each was read as a plain button.
