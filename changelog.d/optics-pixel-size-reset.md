### Fixed (Sky Album)

- **Pixel scale without a profile pixel size.** Daemon: when the profile's pixel size is 0, a captured light now takes the connected camera's pixel size for its XPIXSZ/YPIXSZ headers and its Sky Album pixel scale, so the frame still fills a Card by position and not only by its target name.

### Changed (Daemon logging)

- **Optics changes name the client.** Daemon: a `PUT /api/v1/profile/optics` that changes the optics logs the client address, its user agent and the values before and after, so an unexpected optics change can be traced to the device that sent it.

### Fixed (Synced preferences)

- **A change no longer reverts on disk.** Client: a rig reconcile that was already queued when you changed a synced preference (for example the planning noise tolerance) could write the rig's older value over the local file after your change had saved. On the next start that older value went to the rig. The reconcile now leaves the file to the change.
