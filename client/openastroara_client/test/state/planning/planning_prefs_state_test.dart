import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/planning_prefs_service.dart';
import 'package:openastroara/state/planning/planning_prefs_state.dart';

class _MemPrefs extends PlanningPrefsService {
  PlanningPrefs stored;
  int saves = 0;
  _MemPrefs(this.stored);
  @override
  Future<PlanningPrefs> load() async => stored;
  @override
  Future<void> save(PlanningPrefs prefs) async {
    stored = prefs;
    saves++;
  }
}

void main() {
  test('#1140 restores the saved knob, clamps and persists edits', () async {
    final prefs = _MemPrefs(const PlanningPrefs(noiseTolerancePct: 3));
    final c = ProviderContainer(
      overrides: [planningPrefsServiceProvider.overrideWithValue(prefs)],
    );
    addTearDown(c.dispose);
    c.listen(planningPrefsProvider, (_, _) {});
    await Future<void>.delayed(Duration.zero);
    expect(c.read(planningPrefsProvider).noiseTolerancePct, 3);

    c.read(planningPrefsProvider.notifier).setNoiseTolerancePct(40);
    expect(
      c.read(planningPrefsProvider).noiseTolerancePct,
      PlanningPrefs.maxNoiseTolerancePct,
    );
    c.read(planningPrefsProvider.notifier).setNoiseTolerancePct(double.nan);
    expect(
      c.read(planningPrefsProvider).noiseTolerancePct,
      PlanningPrefs.maxNoiseTolerancePct,
      reason: 'NaN ignored',
    );
    c.read(planningPrefsProvider.notifier).setNoiseTolerancePct(2);
    expect(prefs.stored.noiseTolerancePct, 2);
    expect(prefs.saves, 2, reason: 'the NaN edit was ignored, not saved');
  });

  test('#1140 ready completes after the saved value is applied', () async {
    final prefs = _MemPrefs(const PlanningPrefs(noiseTolerancePct: 7));
    final c = ProviderContainer(overrides: [planningPrefsServiceProvider.overrideWithValue(prefs)]);
    addTearDown(c.dispose);
    await c.read(planningPrefsProvider.notifier).ready;
    expect(c.read(planningPrefsProvider).noiseTolerancePct, 7);
  });

  test('#1140 a user edit that races ahead of the restore wins', () async {
    final prefs = _MemPrefs(const PlanningPrefs(noiseTolerancePct: 3));
    final c = ProviderContainer(
      overrides: [planningPrefsServiceProvider.overrideWithValue(prefs)],
    );
    addTearDown(c.dispose);
    c.read(planningPrefsProvider.notifier).setNoiseTolerancePct(8);
    await Future<void>.delayed(Duration.zero);
    expect(c.read(planningPrefsProvider).noiseTolerancePct, 8);
  });
}
