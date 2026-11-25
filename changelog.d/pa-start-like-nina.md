### Breaking changes

- **Polar Align never sends the mount Home and never unparks it** (Polar Align, #2048): Polar Align used to send the mount Home before every step and unpark a parked mount. It now refuses a parked mount and never calls Find Home or slews. The *Send the mount Home first* setting (`use_home`) is gone; a saved value is ignored. Simple Mode's Preflight no longer starts Polar Align on its own when you reach the step. **After upgrading:** update the app with the rig, unpark the mount and send it Home yourself, then start Polar Align.

### Fixed (Polar Align)

- **Polar Align starts like NINA's: no Find Home, no GoTo** (#2048): on RC51 the routine sent the mount Home before each step and took AlpacaBridge's "at Home" as proof of where the mount was; with *Send the mount Home first* off, each later step slewed back to the run's start. Now no step calls Find Home or slews, as NINA's Three Point Polar Alignment. A parked mount is refused ("Mount is parked — unpark it and send it Home first"), as NINA does; Polar Align never unparks it.
