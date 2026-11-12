### Added (rig link)

- In Simple Mode, a profile device that goes into an error state shows one amber pill naming it, with Reconnect and Devices. The pill goes once the device connects (#1559).
- In Simple Mode, a card over the wallpaper offers a way back while the rig link is down: Reconnect to this session's rig, Connect to another rig found on the network or saved, or Add by address. Offline planning shows the same card to go online. On a window too narrow to hold the card between the rails (a phone, a one-third Split View), it folds into a bar beside Stop with Connect, so the rails stay usable (#1559).

### Changed (rig link)

- The Full UI goes back to the profile screen after the rig has been unreachable for 15 s. A link that comes back inside the 15 s keeps you where you were. Simple Mode and offline planning stay in the shell (#1570).

### Fixed (rig link)

- Fault recovery no longer reconnects a rotator or dome you removed when the rig still remembered it. The other device types get the same once they move onto the shared device lifecycle (#1696) (#1763).
- A Guider start that fails after calibration began now says so: an error pill in Simple Mode and the guiding strip's message in the Full UI, with the Guider's reason. A Stop during calibration is not reported (#1756).
- Check disk on a server whose kernel can't read exFAT says so in words instead of showing `exfat_unsupported` (#1764).
- A rig answer of "not found" now shows the rig's own sentence when it sent one, such as "No guide camera is known." Storage browse and New folder say a folder that is gone in one plain sentence (#1764).
- Erasing a disk through ARA voids any copy that disk held: the erase warning on the disk the frames came from counts them as not copied again. A copy made before this version counts nothing as copied (#1765).
