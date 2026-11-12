### Fixed (meridian flip)

- A flip leaves a connected guider alone when the plan was not guiding; it used to start guiding, and calibrate, after the flip.
- Runs the app builds now carry the meridian flip trigger, so Auto flip takes effect. They never flipped before, and the Auto flip setting was not read at run time. With Auto flip off, a run neither flips nor pauses at the meridian.
