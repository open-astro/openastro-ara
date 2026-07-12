import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Shared hydrate/persist fencing for the daemon-backed Settings notifiers.
///
/// Every profile-settings section follows the same round-trip shape: a
/// `hydrateFromServer` that replaces local state with the daemon's copy, and a
/// `persistToServer` that PUTs local state and adopts the daemon's normalized
/// echo. Done naively (`state = await api.getX()` / `state = await api.putX()`)
/// this has three race hazards, all of which this mixin closes with the same
/// fences the §37.4 filter-wheel notifier already uses:
///
///  * a stale *hydrate* landing after a newer one — e.g. the active server was
///    switched mid-flight — would overwrite the new server's state. Guarded by a
///    per-call generation counter (last call wins).
///  * a stale *persist echo* landing after the user edited a field while the PUT
///    was in flight would silently revert that newer edit. Guarded by an
///    identity fence: the echo is only adopted when `state` is still the exact
///    snapshot that was sent (any local edit or hydrate replaces the instance).
///  * a `state =` after the notifier was disposed throws. Guarded by
///    `ref.mounted`.
///
/// Persists are additionally serialized (each waits for the previous), so
/// per-row auto-persist can't let out-of-order responses adopt an older
/// snapshot over a newer one.
mixin SettingsSyncMixin<T> on Notifier<T> {
  // Bumped per hydrate call (and any switch that triggers one): an in-flight
  // load/echo from an OLD server must not land over a newer server's state.
  int _syncGeneration = 0;

  // Serializes persists so tabbing through fields (several PUTs) can't adopt an
  // out-of-order echo; each link also sends the latest snapshot at send time.
  Future<void>? _persistChain;

  /// Hydrate local state from [fetch], ignoring the result if a newer hydrate
  /// started or the notifier was disposed while it was in flight.
  Future<void> hydrateGuarded(Future<T> Function() fetch) async {
    final gen = ++_syncGeneration;
    final loaded = await fetch();
    if (ref.mounted && gen == _syncGeneration) state = loaded;
  }

  /// Persist the current state via [put] (called with the snapshot that is
  /// sent) and adopt its echo — but only when no newer local edit or hydrate
  /// landed while the PUT was in flight and the notifier is still mounted.
  /// Persists are serialized. Returns the echo; rethrows transport failures so
  /// the caller can surface them.
  Future<T> persistGuarded(Future<T> Function(T sent) put) {
    // A failed predecessor must not wedge the chain — its error already went to
    // ITS caller; this persist proceeds regardless.
    final previous =
        (_persistChain ?? Future<void>.value()).catchError((Object _) {});
    final run = previous.then((_) async {
      final sent = state;
      final genAtSend = _syncGeneration;
      final echoed = await put(sent);
      if (ref.mounted &&
          genAtSend == _syncGeneration &&
          identical(state, sent)) {
        state = echoed;
      }
      return echoed;
    });
    _persistChain = run;
    return run;
  }
}
