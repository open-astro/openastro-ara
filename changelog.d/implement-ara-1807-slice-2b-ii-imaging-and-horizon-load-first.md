### Fixed

- **Unguided, plate solve, camera electronics and the custom horizon load from the rig before they save** (#1807): Settings. A Save that was never loaded, or that follows a failed load, now writes nothing and says why. Before, it could overwrite the rig's skyline with an empty one or the electronics with zeros. A copy another device saved in between is refused and the panel offers Reload. A rig or profile change sends all four back to not loaded. The offline cache fills them for planning only and is never saved; with no rig their panels say so instead of showing that copy.
