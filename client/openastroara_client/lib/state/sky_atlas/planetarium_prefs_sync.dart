import '../../services/planetarium_prefs_service.dart';
import '../prefs_sync.dart';

/// #1644 — the planetarium's layer and `cat:{id}` catalog toggles follow the
/// user across devices as one top-level key of the rig's client-settings blob.
/// A toggle only one side knows about survives; where both know it, the side
/// being applied wins (the rig's on load, this device's when sending).
SyncedPref<Map<String, bool>> planetariumSyncedPref(
  PlanetariumPrefsService svc,
) => SyncedPref<Map<String, bool>>(
  key: 'planetarium',
  readLocal: () async {
    final stored = await svc.load();
    return stored.isEmpty ? null : stored;
  },
  writeLocal: svc.save,
  encode: (m) => m,
  decode: (raw) => raw is Map
      ? {
          for (final e in raw.entries)
            if (e.key is String && e.value is bool)
              e.key as String: e.value as bool,
        }
      : null,
  pushOver: (local, server) => {...server, ...local},
  applyOver: (local, server) => {...local, ...server},
);
