### Added

- **A profile file the daemon set aside now reaches the inbox.** A profile file that could not be read is renamed to `<id>.json.corrupt.<unix-time>` at start; the daemon now also posts one Critical notification per such file, naming the profile id and the new path, once (#1955).
