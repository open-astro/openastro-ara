### Fixed (client)

- **The Simple Mode right rail keeps one size** (#2038): the rail was taller on Calibration than on Preview whenever the shutter had a job, because its caption sat under the button, so the whole rail grew and its top moved. Now the rail has the same size and top on Preview and Calibration, idle or with a job ready. The caption shows only where it fits (a window at least 600 high, text up to 1.3x); on a short window or at larger text it is the shutter's tooltip instead.
