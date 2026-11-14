### Added (Simple Mode)

- **Warming pill.** After the Camera window's cooler switch turns the cooler off, the status card shows "Warming" with the sensor's temperature until the sensor stops rising, so you can tell when it is safe to cap the camera and cut its power (#1870).

### Changed (Simple Mode)

- **Luminance glass is a spectrum.** In the Filter wheel window the L cell (L, Lum, Luminance or Clear) shows the visible spectrum instead of pale clear glass, so it reads as all colours without being the brightest thing on screen at night.

- **Frames is a typed count.** In Plan, Frames is a field you type any count into, with − and + beside it, instead of a wheel of presets.
- **Exposures and Filter pick on a wheel.** Exposure in Preview, Plan, Guide and Polar align, and Preview's Filter, open a wheel in place instead of a list: the iOS wheel on iPhone, iPad and desktop, and Android's own number picker on Android.
- **Dec mode is segments.** The Guider panel's Dec mode shows Auto, North, South and Off on one row instead of a list.

- **Focuser zero and travel calibration.** Settings › Focuser has a Position section: Set zero makes where the focuser is now 0, and Calibrate travel runs a focuser that can find its own stops to both ends. Each shows only when the focuser's driver offers it. The full UI's Settings › Equipment › Focuser offers the same two actions. The daemon adds `POST /api/v1/equipment/focuser/setzero`, `/calibrate` and `/calibrate/abort`, and the focuser's capabilities report `can_set_zero` and `can_calibrate`; a refused zero or calibration answers 409.

### Fixed (Simple Mode)

- **Sky Map opens without tiles filling in.** The Sky Map now loads when Simple Mode opens and stays loaded, so opening it no longer starts the planetarium again. It shows once it has drawn its first sky, with "Loading the sky…" until then. While a frame shows, the loaded Sky Map rests and draws nothing, so keeping it ready costs no GPU.

- **Full filter names get their glass colour.** A wheel that names its filters Red, Green, Blue or Clear showed those cells as plain grey glass; they now take the same colour as R, G, B and L.
- **Versions last in Settings › Rig.** The Versions table now closes the Rig section, after Storage and Logs, as it closes each device's window in Settings.
- **Stats is a denser dashboard.** The total integration leads as a headline with the other totals listed beside it, the nights calendar follows, Achievements no longer repeats Overview's totals, and badges are medals with a progress ring on a shelf, earned first and all one size.
