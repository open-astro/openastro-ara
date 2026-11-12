### Added (storage)

- Before you erase a disk, the disk chooser says how many frames the disk holds and the date of the newest one, and warns when some were never copied to another disk by ARA. A disk that isn't connected says ARA can't see what is on it. Erase stays off until this is read (#1579).

### Fixed (storage)

- On a server whose kernel can't mount exFAT (an ASIAIR Plus on an rk-flashtool image), Erase as exFAT is refused before the drive is touched, so the drive keeps its data. Use Disk and disk-to-disk copy onto an exFAT drive say the server can't read exFAT and that erasing as ext4 lets ARA use it. Before, the erase wiped the drive and then failed with "could not be connected" (#1753).
