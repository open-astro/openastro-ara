### Added (live stack)

- **Live Stack server for one-shot-colour cameras** (#1787): `POST /equipment/camera/live-stack/start|reset|stop`, `GET …/live-stack` and `GET …/live-stack/frame` stack the Session's saved lights in colour while the Queue runs (off until started; a 180° meridian-flip frame is folded in; one stacker plus one waiting light, a third is counted `skipped_busy`). The client switch follows in a later change.
