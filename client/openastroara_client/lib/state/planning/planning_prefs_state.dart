import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/planning_prefs_service.dart';
import '../client_settings_state.dart';
import '../prefs_sync.dart';

final planningPrefsServiceProvider = Provider<PlanningPrefsService>(
  (ref) => PlanningPrefsService(),
);

/// #1140 — the client-local planning knobs, restored from disk on first read
/// and persisted on every change. Synchronous state (defaults until the file
/// loads) so the ranker and the advisor never wait on it.
class PlanningPrefsNotifier extends Notifier<PlanningPrefs> {
  bool _userTouched = false;
  // Counts edits so a rig value that was read before an edit never lands over it.
  int _edits = 0;

  // #1644 — follows the user across devices: one top-level key on the rig.
  SyncedPref<PlanningPrefs> get _synced {
    final svc = ref.read(planningPrefsServiceProvider);
    return SyncedPref<PlanningPrefs>(
      key: 'planning',
      readLocal: svc.loadStored,
      writeLocal: svc.save,
      encode: (p) => p.toJson(),
      decode: (raw) => raw is Map ? PlanningPrefs.fromJson(raw) : null,
    );
  }

  final _ready = Completer<void>();

  /// Completes once the saved knobs have been applied (or the load failed),
  /// so a first-open ranker can wait instead of ranking twice.
  Future<void> get ready => _ready.future;

  @override
  PlanningPrefs build() {
    unawaited(_restore());
    // A later change on the rig (another device saved) lands here too.
    ref.listen(clientSettingsProvider, (_, next) {
      if (next.hasValue) unawaited(_syncFromRig());
    }, fireImmediately: true);
    return const PlanningPrefs();
  }

  Future<void> _restore() async {
    try {
      final saved = await ref.read(planningPrefsServiceProvider).load();
      // A user edit that raced ahead of the load wins.
      if (!_userTouched && ref.mounted) {
        state = saved;
      }
    } finally {
      if (!_ready.isCompleted) _ready.complete();
    }
    await _syncFromRig();
  }

  Future<void> _syncFromRig() async {
    final edits = _edits;
    final synced = await ref.read(prefsSyncProvider).reconcile(_synced);
    if (synced != null && ref.mounted && edits == _edits) state = synced;
  }

  Future<void> _persist() async {
    final pref = _synced;
    final value = state;
    await ref.read(planningPrefsServiceProvider).save(value);
    if (ref.mounted) await ref.read(prefsSyncProvider).push(pref, value);
  }

  /// Clamped to [PlanningPrefs.minNoiseTolerancePct]..[PlanningPrefs.maxNoiseTolerancePct];
  /// a non-finite value is ignored.
  void setNoiseTolerancePct(double pct) {
    if (!pct.isFinite) return;
    _userTouched = true;
    _edits++;
    state = state.copyWith(
      noiseTolerancePct: pct.clamp(
        PlanningPrefs.minNoiseTolerancePct,
        PlanningPrefs.maxNoiseTolerancePct,
      ),
    );
    unawaited(_persist());
  }

  /// #1176 — null switches the magnitude floor off; a finite value is clamped
  /// to [PlanningPrefs.minMagnitudeFloor]..[PlanningPrefs.maxMagnitudeFloor]
  /// (non-finite ignored).
  void setMagnitudeFloor(double? floor) {
    if (floor != null && !floor.isFinite) return;
    _userTouched = true;
    _edits++;
    state = state.withMagnitudeFloor(
      floor?.clamp(
        PlanningPrefs.minMagnitudeFloor,
        PlanningPrefs.maxMagnitudeFloor,
      ),
    );
    unawaited(_persist());
  }
}

final planningPrefsProvider =
    NotifierProvider<PlanningPrefsNotifier, PlanningPrefs>(
      PlanningPrefsNotifier.new,
    );
