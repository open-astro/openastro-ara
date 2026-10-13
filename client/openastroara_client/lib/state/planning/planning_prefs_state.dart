import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/planning_prefs_service.dart';

final planningPrefsServiceProvider = Provider<PlanningPrefsService>(
  (ref) => PlanningPrefsService(),
);

/// #1140 — the client-local planning knobs, restored from disk on first read
/// and persisted on every change. Synchronous state (defaults until the file
/// loads) so the ranker and the advisor never wait on it.
class PlanningPrefsNotifier extends Notifier<PlanningPrefs> {
  bool _userTouched = false;

  @override
  PlanningPrefs build() {
    unawaited(_restore());
    return const PlanningPrefs();
  }

  Future<void> _restore() async {
    final saved = await ref.read(planningPrefsServiceProvider).load();
    // A user edit that raced ahead of the load wins.
    if (_userTouched || !ref.mounted) {
      return;
    }
    state = saved;
  }

  /// Clamped to [PlanningPrefs.minNoiseTolerancePct]..[PlanningPrefs.maxNoiseTolerancePct];
  /// a non-finite value is ignored.
  void setNoiseTolerancePct(double pct) {
    if (!pct.isFinite) return;
    _userTouched = true;
    state = state.copyWith(
      noiseTolerancePct: pct.clamp(
        PlanningPrefs.minNoiseTolerancePct,
        PlanningPrefs.maxNoiseTolerancePct,
      ),
    );
    unawaited(ref.read(planningPrefsServiceProvider).save(state));
  }
}

final planningPrefsProvider =
    NotifierProvider<PlanningPrefsNotifier, PlanningPrefs>(
      PlanningPrefsNotifier.new,
    );
