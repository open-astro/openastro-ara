### Added

- **Add your devices on a fresh rig** (setup wizard, Simple Mode, #1580): the Connect screen and Simple Mode's empty Devices section list what AlpacaBridge sees attached (cameras today) as ticked rows under "Found on this rig". "Add selected (n)" adds them one at a time; each row shows Waiting, Adding…, Added or Failed, a failed row has Retry and Edit, and a row that needs a value opens its form first. "Add a device by hand" lists the bridge's own driver catalog and builds the form from it. One add runs at a time: while one is under way, Add selected, Add a device by hand and Retry detection are off. A camera the bridge already offers is left out (matched by serial, by name only when a serial is missing). The list reads `GET /equipment/alpacabridge/catalog` and `/cameras` and sends `POST /equipment/alpacabridge/devices`; the daemon dials the bridge.

### Changed

- **The device-names route also answers `serials`** (daemon, #1580): `GET /equipment/guider/alpacadevicenames` adds `serials` (`"type/N"` to the device's serial, additive, empty ones left out) so two cameras of one model can be told apart.
