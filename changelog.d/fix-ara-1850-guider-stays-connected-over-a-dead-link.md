### Fixed

- **Guider no longer reports connected over a dead link** (guider, #1850): if the guider's event socket drops while Ara is still connecting (during the post-connect setup), the connect now ends in Error and starts the automatic reconnect, instead of showing `connected` while every guide command fails with `guider is not connected`.
