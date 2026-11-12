### Changed (client modules)

- The Full UI's filter picker asks before it changes the filter while a plan runs, as Simple Mode's Filter wheel window does. A filter change that the wheel accepts but never makes now says so in both presentations, and a wheel seen turning gets 45 s to land before it counts as failed (#1698).
- Simple Mode's polar alignment loads its frames one at a time, as the Full UI does: a frame that finishes while one is loading is fetched once after it (#1698).
- First-run setup pins the guide camera by its Alpaca UniqueID, as Settings › Equipment › Guider does, so a new pick no longer keeps the UniqueID of the camera it replaced (#1698, #1308).
