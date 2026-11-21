### Fixed (logging)

- **A refused frame file is logged once** (#2077): a frame catalogued under an earlier save directory (for example, before the §29 store was configured) is still refused, but the daemon now writes the "Frame file refused" warning once per path instead of on every gallery thumbnail, preview, histogram or download request; repeats go to the Debug log. Past 4,096 distinct refused paths, the warning repeats at most once a minute.
