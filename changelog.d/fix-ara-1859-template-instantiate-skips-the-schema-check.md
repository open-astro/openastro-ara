### Fixed

- **A template that is not a valid sequence is refused** (#1859): creating a sequence from a template whose body has no `schemaVersion` now answers 422 with the reason and stores nothing. Before, it stored a sequence the engine could not load.
- **The built-in templates are real sequences** (#1859): `single-target-lrgb`, `single-target-narrowband` and `all-night-dso-roster` now carry a full sequence tree (one frame-count loop per filter) instead of a placeholder, so the engine loads them as a sequence, not an unknown container.
