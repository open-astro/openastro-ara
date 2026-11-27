### Changed (internal)
- **One module admits every mount move** (#2139): whether a GoTo, park or home may go is now decided in one place in the daemon, Move Admission, which checks each asker (a person, a Session, Center, the meridian flip, the shutdown, a safety park) against one table of rules. A new limit is added there once.
