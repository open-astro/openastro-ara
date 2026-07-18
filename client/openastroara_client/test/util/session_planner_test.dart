import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/tonight_sky_api.dart';
import 'package:openastroara/util/session_planner.dart';

TonightSkyObject obj(
  String id, {
  required DateTime winStart,
  required DateTime winEnd,
  double score = 80,
  double hoursFree = 60,
  double? subS = 120,
}) =>
    TonightSkyObject(
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
      score: score,
      hoursFreeScore: hoursFree,
      optimalSubS: subS,
    );

void main() {
  final winStart = DateTime.utc(2026, 7, 18, 4); // 22:00 MDT
  final winEnd = DateTime.utc(2026, 7, 18, 7); // 01:00 MDT

  test('one target: the best-valued object gets the whole usable window', () {
    final plan = planImagingSession(
      ranked: [
        obj('A',
            winStart: DateTime.utc(2026, 7, 18, 3),
            winEnd: DateTime.utc(2026, 7, 18, 9),
            hoursFree: 70),
        obj('B',
            winStart: DateTime.utc(2026, 7, 18, 3),
            winEnd: DateTime.utc(2026, 7, 18, 9),
            hoursFree: 50),
      ],
      windowStartUtc: winStart,
      windowEndUtc: winEnd,
    );
    expect(plan.targets, hasLength(1));
    expect(plan.targets.single.object.id, 'A');
    expect(plan.targets.single.hours, closeTo(3.0, 0.01));
    // Overhead-aware Glover subs: 3 h minus 6 min setup minus one autofocus
    // run (60 s), at 120 s + 10 s dither settle per sub → 79 subs (defaults).
    expect(plan.targets.single.subCount, 79);
  });

  test('overheads reduce the sub count honestly', () {
    const none = SessionOverheads(
        setupMinutesPerTarget: 0,
        ditherEnabled: false,
        autofocusEveryHours: 0);
    final ideal = planImagingSession(
      ranked: [
        obj('A',
            winStart: DateTime.utc(2026, 7, 18, 3),
            winEnd: DateTime.utc(2026, 7, 18, 9)),
      ],
      windowStartUtc: winStart,
      windowEndUtc: winEnd,
      overheads: none,
    );
    // No overheads: 3 h / 120 s = 90 subs exactly.
    expect(ideal.targets.single.subCount, 90);
  });

  test('two targets: window splits where both slices are shootable', () {
    // A sets mid-window; B rises mid-window — the natural split.
    final plan = planImagingSession(
      ranked: [
        obj('A',
            winStart: DateTime.utc(2026, 7, 18, 3),
            winEnd: DateTime.utc(2026, 7, 18, 5, 30)),
        obj('B',
            winStart: DateTime.utc(2026, 7, 18, 5),
            winEnd: DateTime.utc(2026, 7, 18, 9)),
      ],
      windowStartUtc: winStart,
      windowEndUtc: winEnd,
      targetCount: 2,
    );
    expect(plan.targets, hasLength(2));
    expect(plan.targets[0].object.id, 'A');
    expect(plan.targets[1].object.id, 'B');
    // Slices abut inside the window and never overlap.
    expect(
        plan.targets[0].endUtc.isAfter(plan.targets[1].startUtc), isFalse);
    expect(plan.targets[0].hours, greaterThanOrEqualTo(0.75));
    expect(plan.targets[1].hours, greaterThanOrEqualTo(0.75));
  });

  test('two targets degrade to one with a note when nothing else fits', () {
    final plan = planImagingSession(
      ranked: [
        obj('A',
            winStart: DateTime.utc(2026, 7, 18, 3),
            winEnd: DateTime.utc(2026, 7, 18, 9)),
      ],
      windowStartUtc: winStart,
      windowEndUtc: winEnd,
      targetCount: 2,
    );
    expect(plan.targets, hasLength(1));
    expect(plan.notes, isNotEmpty);
  });

  test('a long window supports 3-4 targets; a short one degrades with a note',
      () {
    // Four all-night candidates over a 6 h window: each can get ≥45 min.
    final longWin = planImagingSession(
      ranked: [
        for (final id in ['A', 'B', 'C', 'D'])
          obj(id,
              winStart: DateTime.utc(2026, 7, 18, 2),
              winEnd: DateTime.utc(2026, 7, 18, 12),
              hoursFree: 70 - 'ABCD'.indexOf(id) * 5),
      ],
      windowStartUtc: DateTime.utc(2026, 7, 18, 4),
      windowEndUtc: DateTime.utc(2026, 7, 18, 10),
      targetCount: 4,
    );
    expect(longWin.targets.length, inInclusiveRange(3, 4));
    // Concave hours value ⇒ near-equal allocation: no slice may hog the
    // window while others sit at the 45-min floor (the linear-value bug).
    final hours = longWin.targets.map((t) => t.hours).toList()..sort();
    expect(hours.last / hours.first, lessThanOrEqualTo(2.5),
        reason: 'slices must be balanced, not floor + remainder');
    // Slices are chronological and non-overlapping.
    for (var i = 1; i < longWin.targets.length; i++) {
      expect(
          longWin.targets[i - 1]
              .endUtc
              .isAfter(longWin.targets[i].startUtc),
          isFalse);
    }
    for (final t in longWin.targets) {
      expect(t.hours, greaterThanOrEqualTo(0.75));
    }

    // A 1.5 h window can't honestly hold 4 targets — degrade with a note.
    final shortWin = planImagingSession(
      ranked: [
        for (final id in ['A', 'B', 'C', 'D'])
          obj(id,
              winStart: DateTime.utc(2026, 7, 18, 2),
              winEnd: DateTime.utc(2026, 7, 18, 12)),
      ],
      windowStartUtc: DateTime.utc(2026, 7, 18, 4),
      windowEndUtc: DateTime.utc(2026, 7, 18, 5, 30),
      targetCount: 4,
    );
    expect(shortWin.targets.length, lessThan(4));
    expect(shortWin.notes, isNotEmpty);
  });

  test('empty window / no overlap produce an explanatory empty plan', () {
    final plan = planImagingSession(
      ranked: [
        obj('A',
            winStart: DateTime.utc(2026, 7, 18, 10),
            winEnd: DateTime.utc(2026, 7, 18, 12)),
      ],
      windowStartUtc: winStart,
      windowEndUtc: winEnd,
    );
    expect(plan.targets, isEmpty);
    expect(plan.notes, isNotEmpty);
  });
}
