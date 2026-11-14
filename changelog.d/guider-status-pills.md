### Fixed (guiding)

- **Simple Mode sees a guider the rig starts** (#1875): Simple Mode re-read the guider only while its last read said it was busy, so a guider started by a plan's Start guiding stayed hidden (no guiding bar, pill or figures) until Advanced Mode was opened. It now re-reads an idle guider every 10 s and a busy one every 2 s.
- **One Focus pill during a plan's autofocus** (#1875): the Plan pill steps aside while autofocus runs and returns when it ends.

### Changed (guiding)

- **A plan's calibration says where it is** (#1875): the Plan pill reads "Start guiding · Guider calibrating · West step 3 · 11.9 px", with no second Guider pill, and the Guider panel draws the calibration steps in place of RMS and Exposure while the guider calibrates.
- **The Guiding Assistant has its own pill** (#1875): time left on a progress bar while it measures; its Done pill stays 10 s.
- **The Guider panel takes the height of the tab it shows** (#1875): Tools no longer scrolls inside the Guide tab's height; the panel grows up to the room above the bottom row and scrolls only past it.
- **Preview › Guide loops plain frames** (#1875): its shutter starts the guider's own looping frames instead of the guide-camera focus loop. Focusing the guide camera stays in Focus › Guide.
