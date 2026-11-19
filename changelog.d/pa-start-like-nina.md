### Breaking changes

- **Polar Align needs your confirmation, and never homes or unparks** (Polar Align, #2048): Polar Align used to send the mount Home before every step and unpark a parked mount. It now starts from where you pointed the mount, refuses a parked mount, and moves only after you confirm. A client that starts it without the confirmation (an older app, a script calling `POST /equipment/polaralign/start`) is refused with `polar-align-unconfirmed`. The *Send the mount Home first* setting (`use_home`) is gone; a saved value is ignored. Simple Mode's Preflight no longer starts Polar Align on its own when you reach the step. **After upgrading:** update the app with the rig. Point the tube near the pole, counterweights down, unpark the mount, then start Polar Align and confirm the turn direction it shows.

### Fixed (Polar Align)

- **Polar Align starts like NINA's: no Find Home, no GoTo** (#2048): on RC51 the routine sent the mount Home before each step and took AlpacaBridge's "at Home" as proof of where the mount was; with *Send the mount Home first* off, each later step slewed back to the run's start. Now no step calls Find Home or slews, as NINA's Three Point Polar Alignment, which never homes and starts from where the mount is. With no Home between the steps, each later step turns inside the arc you confirmed: Fine turns back over Rough's arc, and Verify's turns stay inside it too (Rough, Fine, Verify at 10°: 0 → 20° → 0 → 20°). A parked mount is refused ("Mount is parked — unpark it and point near the pole first"), as NINA does; Polar Align never unparks it.

### Added (Polar Align)

- **A confirm step before the mount moves** (#2048): Start shows "point the tube near the pole, counterweights down", which way RA will turn (east or west, about 10° twice), the most the run turns, and "keep a hand on the mount's power switch", in English and pt-BR. The daemon reads the direction from the mount (`GET /equipment/polaralign/start-plan`) and refuses a start that does not carry it back, or whose direction changed since. Full UI and Simple Mode.
