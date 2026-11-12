### Added (session watch)

- **Monitor** in Simple Mode: a full-screen, read-only view of the Session, opened from the left rail on windows 900 pt wide or more. One line on top says whether all is well or names the most important problem. Tiles show what the rig is doing now with a frame countdown and Up next, the Target with its altitude tonight, the next meridian flip, safety, weather, Guiding and the last frame's quality with its trend, and tonight's timeline runs beside a large Emergency Stop. Tiles appear only for gear in the rig's profile. If the link to the rig is lost, the numbers freeze and say how old they are. Tapping a tile opens the window or panel that owns it. The screen stays awake while Monitor shows, and this device's battery shows beside the clock (#1724).
- In Simple Mode, a frame the main camera takes for another client gets its own Camera pill with the frame's progress. A plan's exposure fills the Plan pill's bar frame by frame (#1755).
- While a plan waits out the meridian, Simple Mode says "Waiting for the meridian flip" with a countdown, in a blue ring, in your language. The Plan square reads "Waiting for flip" and the shutter "Pause after the flip". The run state gains `meridian_wait_seconds_left`, and a flip the pre-flip check refused now has its own reason code, `meridian_refused` (#1747).

### Changed (session watch)

- A Simple Mode question pill puts its words across the full pill and its answers underneath, so a long message is no longer squeezed beside a wide button (#1745).
- After you pause a running plan in Simple Mode, the shutter greys out and the Plan pill says "Pausing after this frame" until the frame in hand lands. Another tap says so instead of doing nothing.
- The running-plan warning in the OpenAstro Ara sheet wraps to the sheet's width instead of widening it.
- The Simple Mode left rail scrolls when large text makes its items taller than the window.
- Pausing a plan now stops what it has in progress, such as an autofocus, a wait or centring, and runs it again on Resume. The frame being exposed, a flat set and a meridian flip still finish first.
- The Simple Mode Guider window may reach down over the guiding bar, so its options fit without scrolling.
- In Simple Mode, picking another mode from the mode grid opens its panel, so you no longer tap the settings chip as well. Preview opens nothing, so the frame stays clear. The chip still hides and shows the panel.
- Monitor leaves by a Back button at its top left, as the Sky Album does, instead of an ×.
- Info leaves the Simple Mode left rail now that Monitor watches the night. The last frame's facts turn on and off from an Info row in Preview's and Focus's panels.

### Fixed (session watch)

- The Simple Mode polar shutter's Stop no longer waits while another polar call is in flight, such as a settings save (#1770).
