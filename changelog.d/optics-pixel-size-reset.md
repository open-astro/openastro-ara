### Fixed (Sky Album)

- **Pixel scale without a profile pixel size.** Daemon: when the profile's pixel size is 0, a captured light now takes the connected camera's pixel size for its XPIXSZ/YPIXSZ headers and its Sky Album pixel scale, so the frame still fills a Card by position and not only by its target name.

### Changed (Daemon logging)

- **Optics changes name the client.** Daemon: a `PUT /api/v1/profile/optics` that changes the optics logs the client address, its user agent and the values before and after, so an unexpected optics change can be traced to the device that sent it.
