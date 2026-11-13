### Changed (Simple Mode)

- **Luminance glass is a spectrum.** In the Filter wheel window the L cell (L, Lum, Luminance or Clear) shows the visible spectrum instead of pale clear glass, so it reads as all colours without being the brightest thing on screen at night.

- **Frames is a typed count.** In Plan, Frames is a field you type any count into, with − and + beside it, instead of a wheel of presets.
- **Exposures and Filter pick on a wheel.** Exposure in Preview, Plan, Guide and Polar align, and Preview's Filter, open a wheel in place instead of a list: the iOS wheel on iPhone, iPad and desktop, and Android's own number picker on Android.
- **Dec mode is segments.** The Guider panel's Dec mode shows Auto, North, South and Off on one row instead of a list.

- **Focuser zero and travel calibration.** Settings › Focuser has a Position section: Set zero makes where the focuser is now 0, and Calibrate travel runs a focuser that can find its own stops to both ends. Each shows only when the focuser's driver offers it. The daemon adds `POST /api/v1/equipment/focuser/setzero`, `/calibrate` and `/calibrate/abort`, and the focuser's capabilities report `can_set_zero` and `can_calibrate`.

### Fixed (Simple Mode)

- **Full filter names get their glass colour.** A wheel that names its filters Red, Green, Blue or Clear showed those cells as plain grey glass; they now take the same colour as R, G, B and L.
- **Versions last in Settings › Rig.** The Versions table now closes the Rig section, after Storage and Logs, as it closes each device's window in Settings.
- **Stats tiles and badges line up.** Tiles in a row of the Stats page are now the same height, and an earned badge keeps its bar (full, green) with "Earned" under it, so it is as tall as a badge still in progress.
