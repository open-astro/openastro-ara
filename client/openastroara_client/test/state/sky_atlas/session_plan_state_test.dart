import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/tonight_sky_api.dart';
import 'package:openastroara/state/sky_atlas/session_plan_state.dart';
import 'package:openastroara/util/session_planner.dart';

TonightSkyObject _obj(
  String id,
  DateTime winStart,
  DateTime winEnd, {
  double? budgetFullHours,
}) => TonightSkyObject(
  id: id,
  name: id,
  type: 'HII',
  magnitude: 7,
  raDeg: 0,
  decDeg: 0,
  altitudeDeg: 50,
  maxAltitudeDeg: 80,
  windowStartUtc: winStart,
  windowEndUtc: winEnd,
  score: 80,
  hoursFreeScore: 60,
  optimalSubS: 120,
  budgetFullHours: budgetFullHours,
);

void main() {
  final winStart = DateTime.utc(2026, 7, 18, 4);
  final winEnd = DateTime.utc(2026, 7, 18, 7); // 3 h

  test('P5: setMosaic hands the banked hours on, so a tier note survives a grid round trip', () {
    final container = ProviderContainer();
    addTearDown(container.dispose);
    final near = _obj('near', winStart, winEnd, budgetFullHours: 9);
    const banked = {'near': 7.0};
    final plan = planImagingSession(
      ranked: [near],
      windowStartUtc: winStart,
      windowEndUtc: winEnd,
      bankedHours: banked,
    );
    expect(
      plan.notes.single,
      'near: this slice completes its full-structure tier.',
    );

    final n = container.read(sessionPlanProvider.notifier);
    n.setPlan(
      plan,
      ranked: [near],
      overheads: const SessionOverheads(),
      bankedHours: banked,
    );

    n.setMosaic(0, (cols: 2, rows: 2, overlapPct: 10));
    expect(
      container.read(sessionPlanProvider).plan!.notes,
      isEmpty,
      reason: 'a quarter of the night per panel no longer completes the tier',
    );

    n.setMosaic(0, (cols: 1, rows: 1, overlapPct: 0));
    expect(
      container.read(sessionPlanProvider).plan!.notes.single,
      'near: this slice completes its full-structure tier.',
      reason: 'the note only comes back if the notifier passed the banked hours through',
    );
  });

  test('P5: a fully banked target keeps its "already complete" note across a grid change', () {
    final container = ProviderContainer();
    addTearDown(container.dispose);
    final done = _obj('done', winStart, winEnd, budgetFullHours: 9);
    const banked = {'done': 9.5};
    final plan = planImagingSession(
      ranked: [done],
      windowStartUtc: winStart,
      windowEndUtc: winEnd,
      bankedHours: banked,
    );
    expect(plan.notes.single, contains('already complete'));

    final n = container.read(sessionPlanProvider.notifier);
    n.setPlan(
      plan,
      ranked: [done],
      overheads: const SessionOverheads(),
      bankedHours: banked,
    );
    n.setMosaic(0, (cols: 2, rows: 1, overlapPct: 10));
    expect(
      container.read(sessionPlanProvider).plan!.notes.single,
      contains('already complete'),
    );
  });
}
