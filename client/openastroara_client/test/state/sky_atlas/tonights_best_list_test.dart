import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/stats/stats_target.dart';
import 'package:openastroara/services/tonight_sky_api.dart';
import 'package:openastroara/state/sky_atlas/tonight_sky_state.dart';
import 'package:openastroara/state/sky_atlas/tonights_best_list_state.dart';
import 'package:openastroara/state/stats/stats_targets_state.dart';

final _now = DateTime.utc(2026, 11, 17, 22);

TonightSkyObject _o(
  String id, {
  double score = 50,
  double alt = 40,
  double maxAlt = 70,
  String type = 'G',
  List<String> aliases = const [],
  String? name,
  DateTime? transit,
  DateTime? start,
  DateTime? end,
  TonightFraming framing = TonightFraming.good,
  double? moonSep,
  double? moonUp,
  double? moonIllum,
}) => TonightSkyObject(
  id: id,
  name: name ?? id,
  type: type,
  magnitude: 9,
  raDeg: 10,
  decDeg: 20,
  altitudeDeg: alt,
  maxAltitudeDeg: maxAlt,
  aliases: aliases,
  score: score,
  transitUtc: transit,
  windowStartUtc: start ?? _now.subtract(const Duration(hours: 1)),
  windowEndUtc: end ?? _now.add(const Duration(hours: 4)),
  framing: framing,
  moonSeparationDeg: moonSep,
  moonUpFraction: moonUp,
  moonIlluminationPct: moonIllum,
);

List<String> _ids(List<BestRow> r) => [for (final x in r) x.object.id];

List<BestRow> _rows(
  List<TonightSkyObject> ranked, [
  BestChoices c = const BestChoices(),
  List<String> queued = const [],
  List<String> collected = const [],
]) => bestRows(ranked, c, nowUtc: _now, queued: queued, collected: collected);

class _Stats extends StatsTargetsNotifier {
  _Stats(this._v);
  final List<StatsTarget> _v;
  @override
  Future<List<StatsTarget>?> build() async => _v;
}

/// Loads a saved Transit only when [gate] opens; saves nothing.
class _GatedPrefs extends TonightsBestPrefsService {
  final gate = Completer<void>();
  @override
  Future<BestChoices> load() async {
    await gate.future;
    return const BestChoices(sort: BestSort.transit);
  }

  @override
  Future<void> save(BestChoices c) async {}
}

void main() {
  // N1: B and C tie on altitude; ranker order C, B disagrees with name and
  // with score order (B 90 > C 70).
  final a = _o(
    'A',
    score: 80,
    alt: 20,
    transit: _now.add(const Duration(hours: 3)),
  );
  final c = _o(
    'C',
    score: 70,
    alt: 60,
    transit: _now.add(const Duration(hours: 2)),
  );
  final b = _o(
    'B',
    score: 90,
    alt: 60,
    transit: _now.add(const Duration(hours: 1)),
  );
  final ranked = [a, c, b];

  test('Score keeps the ranker order and never re-ranks', () {
    expect(_ids(_rows(ranked)), ['A', 'C', 'B']);
  });

  test('Transit puts the earliest transit first, an unknown transit last', () {
    final none = _o('N');
    expect(
      _ids(_rows([none, ...ranked], const BestChoices(sort: BestSort.transit))),
      ['B', 'C', 'A', 'N'],
    );
  });

  test('Altitude now sorts highest first and ties keep the ranking order', () {
    expect(_ids(_rows(ranked, const BestChoices(sort: BestSort.altitudeNow))), [
      'C',
      'B',
      'A',
    ]);
  });

  test('the type codes fall in five groups', () {
    for (final t in ['G', 'GGroup', 'GPair', 'GTrpl']) {
      expect(bestTypeOf(t), BestType.galaxy, reason: t);
    }
    for (final t in ['HII', 'EmN', 'RfN', 'Neb', 'SNR', 'Cl+N', 'DrkN']) {
      expect(bestTypeOf(t), BestType.nebula, reason: t);
    }
    for (final t in ['OCl', 'GCl', '*Ass']) {
      expect(bestTypeOf(t), BestType.cluster, reason: t);
    }
    expect(bestTypeOf('PN'), BestType.planetary);
    expect(bestTypeOf('*'), BestType.other);
    expect(bestTypeOf('???'), BestType.other);
  });

  test('several chips keep their union; no chip (All) keeps every row', () {
    final rows = [
      _o('G1', type: 'G'),
      _o('N1', type: 'HII'),
      _o('C1', type: 'OCl'),
    ];
    expect(_ids(_rows(rows)), ['G1', 'N1', 'C1']);
    expect(
      _ids(
        _rows(
          rows,
          const BestChoices(types: {BestType.galaxy, BestType.cluster}),
        ),
      ),
      ['G1', 'C1'],
    );
  });

  test(
    'All night keeps every ranked Target, Up now keeps open windows only',
    () {
      final later = _o(
        'L',
        start: _now.add(const Duration(hours: 2)),
        end: _now.add(const Duration(hours: 6)),
      );
      final gone = _o(
        'P',
        start: _now.subtract(const Duration(hours: 6)),
        end: _now.subtract(const Duration(hours: 1)),
      );
      final rows = [later, _o('U'), gone];
      expect(_ids(_rows(rows)), ['L', 'U', 'P']);
      expect(_ids(_rows(rows, const BestChoices(show: BestShow.upNow))), ['U']);
    },
  );

  test('below the horizon now is flagged and kept in All night', () {
    // Open window but altitude -6 at the rank instant: Up now must not use altitude.
    final low = _o('LOW', alt: -6);
    final rows = _rows([low, _o('HI', alt: 30)]);
    expect(_ids(rows), ['LOW', 'HI']);
    expect(rows.map((r) => r.belowHorizon), [true, false]);
    expect(_ids(_rows([low], const BestChoices(show: BestShow.upNow))), [
      'LOW',
    ]);
  });

  test('a Target that never rises is dropped, as the tiles drop it', () {
    final rows = [_o('UP'), _o('NEVER', maxAlt: 0), _o('BELOW', maxAlt: -12)];
    expect(_ids(_rows(rows)), ['UP']);
  });

  test('Queue and Collected read any designation of the row', () {
    final row = _o(
      'NGC 7000',
      name: 'North America Nebula',
      aliases: ['C 20', 'Sh2-117'],
    );
    final other = _o('M 31');
    final rows = _rows([row, other], const BestChoices(), ['c20'], ['NGC7000']);
    expect(rows.map((r) => r.inQueue), [true, false]);
    expect(rows.map((r) => r.collected), [true, false]);
    // The common name counts too (a queued block is named by what was picked).
    expect(
      _rows(
        [row],
        const BestChoices(),
        ['North America Nebula'],
      ).single.inQueue,
      isTrue,
    );
  });

  test('the Moon distance shows only on a Target the Moon affects', () {
    final hurt = _o('H', moonSep: 28, moonUp: 0.6, moonIllum: 80);
    final far = _o('F', moonSep: 75, moonUp: 0.6, moonIllum: 80);
    final down = _o('D', moonSep: 28, moonUp: 0, moonIllum: 80);
    final dark = _o('K', moonSep: 28, moonUp: 0.6, moonIllum: 0);
    final unknown = _o('U');
    final rows = _rows([hurt, far, down, dark, unknown]);
    expect(rows.map((r) => r.moonSeparationDeg), [28, null, null, null, null]);
  });

  test('Peak altitude keeps Targets whose transit altitude reaches the chosen height', () {
    final rows = [
      _o('P59', maxAlt: 59.9),
      _o('P60', maxAlt: 60),
      _o('P30', maxAlt: 30, alt: 80),
    ];
    expect(_ids(_rows(rows, const BestChoices(minPeakAltDeg: 60))), ['P60']);
    expect(_ids(_rows(rows, const BestChoices(minPeakAltDeg: 30))), [
      'P59',
      'P60',
      'P30',
    ]);
    expect(_ids(_rows(rows)), ['P59', 'P60', 'P30']);
  });

  test('Fits my field keeps good and good-fit framing', () {
    final rows = [
      _o('GOOD', framing: TonightFraming.good),
      _o('FIT', framing: TonightFraming.goodFit),
      _o('SMALL', framing: TonightFraming.tooSmall),
      _o('BIG', framing: TonightFraming.tooBig),
      _o('UNK', framing: TonightFraming.unknown),
    ];
    expect(_ids(_rows(rows, const BestChoices(fitsField: true))), [
      'GOOD',
      'FIT',
    ]);
  });

  test(
    'Fits my field is disabled with a reason when no row knows its framing',
    () {
      final rows = [
        _o('A', framing: TonightFraming.unknown),
        _o('B', framing: TonightFraming.unknown),
      ];
      expect(fieldKnown(rows), isFalse);
      expect(fieldKnown(const []), isFalse);
      expect(
        fieldKnown([...rows, _o('C', framing: TonightFraming.tooSmall)]),
        isTrue,
      );
      expect(_ids(_rows(rows, const BestChoices(fitsField: true))), ['A', 'B']);
    },
  );

  test('Peak altitude and Fits my field combine with the other filters', () {
    final rows = [
      _o('OK', type: 'G', maxAlt: 80, framing: TonightFraming.good),
      _o('LOWPEAK', type: 'G', maxAlt: 40, framing: TonightFraming.good),
      _o('BADFIT', type: 'G', maxAlt: 80, framing: TonightFraming.tooBig),
      _o('NEB', type: 'HII', maxAlt: 80, framing: TonightFraming.good),
    ];
    const ch = BestChoices(
      types: {BestType.galaxy},
      minPeakAltDeg: 60,
      fitsField: true,
    );
    expect(_ids(_rows(rows, ch)), ['OK']);
  });

  // Time-range filter (#2005): the range is minutes of the local day, so
  // each case builds its window from local DateTimes.
  group('time range', () {
    DateTime at(int day, int h, [int m = 0]) => DateTime(2026, 11, day, h, m);
    TonightSkyObject w(String id, DateTime? s, DateTime? e) => TonightSkyObject(
      id: id,
      name: id,
      type: 'G',
      magnitude: 9,
      raDeg: 10,
      decDeg: 20,
      altitudeDeg: 40,
      maxAltitudeDeg: 70,
      score: 50,
      windowStartUtc: s?.toUtc(),
      windowEndUtc: e?.toUtc(),
    );
    List<String> kept(TonightSkyObject o, int from, int to) =>
        _ids(_rows([o], BestChoices(windowFromMin: from, windowToMin: to)));
    const evening = 21 * 60, one = 60;

    test('an unset range leaves every row, windowless ones too', () {
      expect(_ids(_rows([w('A', at(17, 22), at(18, 2)), w('B', null, null)])), [
        'A',
        'B',
      ]);
    });

    test('a window inside the range, and one that only touches it, stay', () {
      expect(kept(w('A', at(17, 22), at(17, 23)), evening, 23 * 60 + 30), [
        'A',
      ]);
      expect(kept(w('A', at(17, 19), at(17, 21)), evening, 23 * 60), ['A']);
    });

    test('a window wholly outside the range goes', () {
      expect(
        kept(w('A', at(17, 18), at(17, 20, 59)), evening, 23 * 60),
        isEmpty,
      );
      expect(kept(w('A', at(17, 23, 1), at(18, 2)), evening, 23 * 60), isEmpty);
    });

    test(
      'a range that crosses midnight matches a window either side of it',
      () {
        expect(kept(w('A', at(17, 20), at(17, 21, 30)), evening, one), ['A']);
        expect(kept(w('A', at(18, 0, 30), at(18, 3)), evening, one), ['A']);
        expect(kept(w('A', at(18, 2), at(18, 5)), evening, one), isEmpty);
      },
    );

    test('a window that crosses midnight matches a range on the later day', () {
      expect(kept(w('A', at(17, 22), at(18, 4)), 2 * 60, 3 * 60), ['A']);
      expect(kept(w('A', at(17, 22), at(18, 4)), 5 * 60, 6 * 60), isEmpty);
    });

    test('a row with no dark window is dropped under an active range', () {
      expect(kept(w('A', null, null), evening, one), isEmpty);
      expect(kept(w('A', at(17, 22), null), evening, one), isEmpty);
    });

    test(
      'a window ending at midnight touches a range starting at midnight',
      () {
        expect(kept(w('A', at(17, 22), at(18, 0)), 0, one), ['A']);
      },
    );

    test('equal ends mean the whole day', () {
      expect(kept(w('A', at(17, 3), at(17, 4)), 600, 600), ['A']);
    });
  });

  group('kept choices', () {
    late Directory dir;
    setUp(() => dir = Directory.systemTemp.createTempSync('best_prefs'));
    tearDown(() => dir.deleteSync(recursive: true));

    ProviderContainer make({List<TonightSkyObject> list = const []}) {
      final container = ProviderContainer(
        overrides: [
          tonightsBestPrefsServiceProvider.overrideWithValue(
            TonightsBestPrefsService(supportDir: () async => dir),
          ),
          tonightsBestProvider.overrideWith((ref) async => list),
          statsTargetsProvider.overrideWith(() => _Stats(const [])),
        ],
      );
      addTearDown(container.dispose);
      return container;
    }

    test('sort, types, Show, Peak altitude and Fits my field come back after a restart', () async {
      final first = make();
      final n = first.read(tonightsBestChoicesProvider.notifier);
      await n.ready;
      n.setSort(BestSort.transit);
      n.toggleType(BestType.nebula);
      n.toggleType(BestType.galaxy);
      n.toggleType(BestType.galaxy);
      n.setShow(BestShow.upNow);
      n.setMinPeakAlt(45);
      n.setFitsField(true);
      n.setWindow(21 * 60, 60);
      await n.saved;
      final second = make();
      await second.read(tonightsBestChoicesProvider.notifier).ready;
      final got = second.read(tonightsBestChoicesProvider);
      expect(got.sort, BestSort.transit);
      expect(got.types, {BestType.nebula});
      expect(got.show, BestShow.upNow);
      expect(got.minPeakAltDeg, 45);
      expect(got.fitsField, isTrue);
      expect(got.windowFromMin, 21 * 60);
      expect(got.windowToMin, 60);
    });

    test(
      'a stored range that is half set or out of the day is dropped',
      () async {
        File('${dir.path}/tonights_best.json').writeAsStringSync(
          jsonEncode({'windowFromMin': 90, 'windowToMin': 1440}),
        );
        final c = make();
        await c.read(tonightsBestChoicesProvider.notifier).ready;
        final got = c.read(tonightsBestChoicesProvider);
        expect(got.windowFromMin, isNull);
        expect(got.windowToMin, isNull);
      },
    );

    test(
      'an unknown stored value falls back to the default, not a crash',
      () async {
        File('${dir.path}/tonights_best.json').writeAsStringSync(
          jsonEncode({
            'sort': 'luck',
            'types': ['galaxy', 'quasar', 7],
            'show': 'always',
            'minPeakAltDeg': 33,
            'fitsField': 'yes',
          }),
        );
        final c = make();
        await c.read(tonightsBestChoicesProvider.notifier).ready;
        final got = c.read(tonightsBestChoicesProvider);
        expect(got.sort, BestSort.score);
        expect(got.types, {
          BestType.galaxy,
        }, reason: 'one bad entry keeps the good one');
        expect(got.show, BestShow.allNight);
        expect(got.minPeakAltDeg, 0);
        expect(got.fitsField, isFalse);
        // One bad field never discards the others.
        File('${dir.path}/tonights_best.json')
            .writeAsStringSync(jsonEncode({'sort': 'transit', 'show': 42}));
        final d = make();
        await d.read(tonightsBestChoicesProvider.notifier).ready;
        expect(d.read(tonightsBestChoicesProvider).sort, BestSort.transit);
        // Non-map and malformed file.
        File('${dir.path}/tonights_best.json').writeAsStringSync('[1]');
        final e = make();
        await e.read(tonightsBestChoicesProvider.notifier).ready;
        expect(e.read(tonightsBestChoicesProvider).sort, BestSort.score);
        File('${dir.path}/tonights_best.json').writeAsStringSync('{nope');
        final f = make();
        await f.read(tonightsBestChoicesProvider.notifier).ready;
        expect(f.read(tonightsBestChoicesProvider).show, BestShow.allNight);
      },
    );

    test(
      'a Peak altitude saved as 30.0 loads as 30 and keeps the other choices',
      () async {
        // A hand edit of the file can write the whole number as a double.
        File('${dir.path}/tonights_best.json').writeAsStringSync(
          '{"sort": "transit", "minPeakAltDeg": 30.0, "fitsField": true}',
        );
        final c = make();
        await c.read(tonightsBestChoicesProvider.notifier).ready;
        final got = c.read(tonightsBestChoicesProvider);
        expect(got.minPeakAltDeg, 30);
        expect(got.sort, BestSort.transit);
        expect(got.fitsField, isTrue);
      },
    );

    test('a change that races the load wins', () async {
      // The load is held until after the change, and the save never reaches
      // the file, so only the race guard keeps the change.
      final prefs = _GatedPrefs();
      final c = ProviderContainer(
        overrides: [tonightsBestPrefsServiceProvider.overrideWithValue(prefs)],
      );
      addTearDown(c.dispose);
      final n = c.read(tonightsBestChoicesProvider.notifier);
      n.setSort(BestSort.altitudeNow);
      prefs.gate.complete();
      await n.ready;
      expect(c.read(tonightsBestChoicesProvider).sort, BestSort.altitudeNow);
    });

    test('Clear filters resets types, Show, Peak altitude and Fits my field; Sort stays', () async {
      final c = make();
      final n = c.read(tonightsBestChoicesProvider.notifier);
      await n.ready;
      n
        ..setSort(BestSort.transit)
        ..toggleType(BestType.cluster)
        ..setShow(BestShow.upNow)
        ..setMinPeakAlt(60)
        ..setFitsField(true)
        ..setWindow(21 * 60, 60)
        ..clearFilters();
      // Windows cannot delete the dir while a save holds the file open.
      await n.saved;
      final got = c.read(tonightsBestChoicesProvider);
      expect(got.sort, BestSort.transit);
      expect(got.filtered, isFalse);
      expect(got.types, isEmpty);
      expect(got.minPeakAltDeg, 0);
      expect(got.fitsField, isFalse);
      expect(got.windowFromMin, isNull);
      expect(got.windowToMin, isNull);
    });

    test('Score with no filter starts with the four tiles of tonightsBest, not of tonightSkyProvider', () async {
      final best = [
        _o('N1', maxAlt: 0),
        for (var i = 1; i <= 5; i++) _o('B$i'),
      ];
      final whole = [_o('WHOLE_NIGHT'), for (var i = 1; i <= 5; i++) _o('B$i')];
      final container = ProviderContainer(
        overrides: [
          tonightsBestPrefsServiceProvider.overrideWithValue(
            TonightsBestPrefsService(supportDir: () async => dir),
          ),
          tonightsBestProvider.overrideWith((ref) async => best),
          tonightSkyProvider.overrideWith((ref) async => whole),
          statsTargetsProvider.overrideWith(() => _Stats(const [])),
        ],
      );
      addTearDown(container.dispose);
      await container.read(tonightsBestChoicesProvider.notifier).ready;
      await container.read(tonightsBestProvider.future);
      final rows = container.read(tonightsBestListProvider).value!;
      expect(_ids(rows).take(4), ['B1', 'B2', 'B3', 'B4']);
    });

    test('a sky clock tick ranks again and the list follows', () async {
      final tick = StreamController<int>();
      addTearDown(tick.close);
      var n = 0;
      final container = ProviderContainer(
        overrides: [
          tonightsBestPrefsServiceProvider.overrideWithValue(
            TonightsBestPrefsService(supportDir: () async => dir),
          ),
          skyClockProvider.overrideWith((ref) => tick.stream),
          tonightsBestProvider.overrideWith((ref) async {
            ref.watch(skyClockProvider);
            return [_o(n++ == 0 ? 'A' : 'B')];
          }),
          statsTargetsProvider.overrideWith(() => _Stats(const [])),
        ],
      );
      addTearDown(container.dispose);
      container.listen(tonightsBestListProvider, (_, _) {});
      await container.read(tonightsBestProvider.future);
      expect(_ids(container.read(tonightsBestListProvider).value!), ['A']);
      tick.add(1);
      await Future<void>.delayed(Duration.zero);
      await container.read(tonightsBestProvider.future);
      expect(_ids(container.read(tonightsBestListProvider).value!), ['B']);
    });

    test('markers come from the Queue and the Collected targets', () async {
      final container = ProviderContainer(
        overrides: [
          tonightsBestPrefsServiceProvider.overrideWithValue(
            TonightsBestPrefsService(supportDir: () async => dir),
          ),
          tonightsBestProvider.overrideWith(
            (ref) async => [
              _o('M 31', aliases: ['NGC 224']),
            ],
          ),
          statsTargetsProvider.overrideWith(
            () => _Stats(const [StatsTarget(targetName: 'NGC224')]),
          ),
        ],
      );
      addTearDown(container.dispose);
      await container.read(tonightsBestProvider.future);
      await container.read(statsTargetsProvider.future);
      final row = container.read(tonightsBestListProvider).value!.single;
      expect(row.collected, isTrue);
      expect(row.inQueue, isFalse);
    });
  });
}
