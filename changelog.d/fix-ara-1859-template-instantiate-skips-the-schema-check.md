### Fixed

- **A template that is not a valid sequence is refused** (#1859): creating a sequence from a template whose body has no `schemaVersion` now answers 422 with the reason and stores nothing. Before, it stored a sequence the engine could not load.
