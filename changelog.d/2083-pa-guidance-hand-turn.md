### Added (polar alignment)
- **NINA's polar-align guidance** (#2083): the confirm step, the help and the user guide say where to point (a field fairly high toward the pole, at least about 15° above the horizon) and to keep the turns from crossing the meridian, so the tube cannot swing into the pier.

### Changed (polar alignment)
- **The azimuth readout says it is a true arc** (#2083): the azimuth error is labelled "true arc (cos alt)", and the Full UI shows NINA's figure beside it (larger by about 1/cos(latitude), 1.5× at 48°), so the two programs' numbers compare. A measurement that starts more than 2° from the pole now warns that the correction is less reliable, and over 10° to check the site, the start field, and that the mount turned only in RA, as NINA does.
