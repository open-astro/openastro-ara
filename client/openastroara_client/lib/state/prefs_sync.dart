import 'dart:convert';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../services/client_settings_api.dart' show isMergeUnsupported;
import '../services/synced_prefs_pending_service.dart';
import 'client_settings_state.dart';

final syncedPrefsPendingProvider = Provider<SyncedPrefsPendingService>(
  (ref) => SyncedPrefsPendingService(),
);

/// One preference that follows the user across devices (#1644): stored under
/// its own top-level [key] of the rig's client-settings blob, with the local
/// file kept as the offline fallback and the seed for a rig that has none.
class SyncedPref<T> {
  const SyncedPref({
    required this.key,
    required this.readLocal,
    required this.writeLocal,
    required this.encode,
    required this.decode,
    this.pushOver,
    this.applyOver,
  });

  final String key;

  /// The local file's value, or null when nothing is stored on this device.
  final Future<T?> Function() readLocal;
  final Future<void> Function(T value) writeLocal;
  final Object? Function(T value) encode;

  /// The value in a server blob, or null when malformed.
  final T? Function(Object? raw) decode;

  /// How a local value goes over the rig's when sent (default: local wins).
  final T Function(T local, T server)? pushOver;

  /// How the rig's value lands on the local one (default: the rig wins).
  final T Function(T local, T server)? applyOver;
}

final prefsSyncProvider = Provider<PrefsSync>((ref) => PrefsSync(ref));

/// Moves synced preferences between the local files and the rig's
/// client-settings blob. Every failure degrades to local-only: a preference is
/// never worth an error on screen.
class PrefsSync {
  PrefsSync(this._ref);
  final Ref _ref;

  // One job at a time per key, so a reconcile never reads the local file
  // between a change and its upload.
  final _chains = <String, Future<void>>{};

  Future<R> _serial<R>(String key, Future<R> Function() job) {
    final previous = _chains[key] ?? Future<void>.value();
    final run = previous.then((_) => job());
    _chains[key] = run.then<void>((_) {}, onError: (Object _) {});
    return run;
  }

  /// Bring [pref] in line with the rig once its settings are loaded and return
  /// the value now in force, or null when this device stays local-only (no rig,
  /// an unreadable rig, nothing stored anywhere). In order:
  ///  * a key marked pending goes to the rig first (the local change wins);
  ///  * a rig with no value for the key is seeded from the local one;
  ///  * otherwise the rig's value lands locally (lists combine, see [SyncedPref]).
  Future<T?> reconcile<T>(SyncedPref<T> pref) => _serial(pref.key, () async {
    if (!_ref.mounted) return null;
    try {
      await _ref.read(clientSettingsProvider.future);
    } catch (_) {
      return null; // an unreachable rig: this device stays local-only
    }
    if (!_ref.mounted) return null;
    final settings = _ref.read(clientSettingsProvider).asData?.value;
    if (settings == null) return null;
    final pending = await _ref.read(syncedPrefsPendingProvider).load();
    final local = await pref.readLocal();
    if (!_ref.mounted) return null;
    final raw = settings.settings[pref.key];
    final serverVal = raw == null ? null : pref.decode(raw);

    T? result;
    var upload = false;
    if (pending.contains(pref.key) && local != null) {
      result = serverVal == null
          ? local
          : (pref.pushOver?.call(local, serverVal) ?? local);
      upload = true;
    } else if (serverVal == null) {
      result = local;
      upload = local != null;
    } else {
      final T next = local == null
          ? serverVal
          : (pref.applyOver?.call(local, serverVal) ?? serverVal);
      result = next;
      upload = !_same(pref.encode(next), raw);
    }
    if (result == null) return null;
    final value = result as T;
    if (local == null || !_same(pref.encode(value), pref.encode(local))) {
      await pref.writeLocal(value);
    }
    if (upload && _ref.mounted) await _send(pref, value);
    return value;
  });

  /// [value] was just written to the local file: send it to the rig, or leave
  /// the key marked pending for the next load when that fails or no rig is
  /// connected. An older daemon (no merge route) keeps it local, unmarked.
  Future<void> push<T>(SyncedPref<T> pref, T value) =>
      _serial(pref.key, () async {
        if (!_ref.mounted) return;
        var v = value;
        final raw = _ref
            .read(clientSettingsProvider)
            .asData
            ?.value
            ?.settings[pref.key];
        final serverVal = raw == null ? null : pref.decode(raw);
        if (serverVal != null && pref.pushOver != null) {
          v = pref.pushOver!(value, serverVal);
        }
        await _send(pref, v);
      });

  Future<void> _send<T>(SyncedPref<T> pref, T value) async {
    final pending = _ref.read(syncedPrefsPendingProvider);
    try {
      final stored = await _ref.read(clientSettingsProvider.notifier).merge({
        pref.key: pref.encode(value),
      });
      if (stored == null) {
        await pending.mark(pref.key); // no rig bound
      } else {
        await pending.clear(pref.key);
      }
    } catch (e) {
      if (!isMergeUnsupported(e)) await pending.mark(pref.key);
    }
  }
}

// JSON equality that ignores map key order.
bool _same(Object? a, Object? b) =>
    jsonEncode(_canon(a)) == jsonEncode(_canon(b));

Object? _canon(Object? v) => switch (v) {
  Map() => {for (final k in (v.keys.toList()..sort())) '$k': _canon(v[k])},
  List() => [for (final e in v) _canon(e)],
  _ => v,
};
