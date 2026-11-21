import 'dart:async';
import 'dart:ui' show Tristate;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/models/stats/stats_target.dart';
import 'package:openastroara/screens/simple/simple_best_screen.dart';
import 'package:openastroara/services/tonight_sky_api.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/settings/loaded_section.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_run_state.dart';
import 'package:openastroara/state/sky_atlas/planetarium_selection_state.dart';
import 'package:openastroara/state/sky_atlas/tonight_sky_state.dart';
import 'package:openastroara/state/sky_atlas/tonights_best_list_state.dart';
import 'package:openastroara/state/stats/stats_targets_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/theme/ara_metrics.dart';

final _now = DateTime.utc(2026, 11, 17, 22);

TonightSkyObject _o(
  String id, {
  double score = 50,
  double alt = 40,
  double maxAlt = 70,
  String type = 'G',
  String? name,
  TonightFraming framing = TonightFraming.good,
  DateTime? transit,
  double? moonSep,
}) => TonightSkyObject(
  id: id,
  name: name ?? id,
  type: type,
  magnitude: 9,
  raDeg: 10,
  decDeg: 20,
  altitudeDeg: alt,
  maxAltitudeDeg: maxAlt,
  score: score,
  transitUtc: transit,
  windowStartUtc: _now.subtract(const Duration(hours: 1)),
  windowEndUtc: _now.add(const Duration(hours: 4)),
  framing: framing,
  moonSeparationDeg: moonSep,
  moonUpFraction: moonSep == null ? null : 0.5,
  moonIlluminationPct: moonSep == null ? null : 80,
);

class _Site extends SiteSettingsNotifier {
  _Site(this.set);
  final bool set;
  @override
  LoadedSection<SiteSettings> build() =>
      LoadedSection(value: set
      ? const SiteSettings(latitudeDeg: -33.856784, longitudeDeg: 151.215297)
      : const SiteSettings(), source: SectionSource.rig);
}

class _Stats extends StatsTargetsNotifier {
  _Stats(this._v);
  final List<String> _v;
  @override
  Future<List<StatsTarget>?> build() async => [
    for (final n in _v) StatsTarget(targetName: n),
  ];
}

class _Run extends SequenceRunStateNotifier {
  _Run(this._v);
  final SequenceRunState _v;
  @override
  Future<SequenceRunStateInfo?> build() async =>
      SequenceRunStateInfo(state: _v);
}

/// create() adds the selected Target as a block, as the rig would.
class _FakeRun extends SimpleRunNotifier {
  @override
  Future<bool> create({bool first = false, DateTime? startsAt}) async {
    final sel = ref.read(planetariumSelectionProvider)!;
    state = state.copyWith(
      targets: [
        ...state.targets,
        SimpleTarget(name: sel.name),
      ],
    );
    return true;
  }
}

/// create() waits for [answer], then the rig refuses the add.
class _RefusedRun extends SimpleRunNotifier {
  _RefusedRun(this.answer);
  final Completer<void> answer;
  @override
  Future<bool> create({bool first = false, DateTime? startsAt}) async {
    await answer.future;
    state = state.copyWith(error: 'The plan is ending.');
    return false;
  }
}

class _World {
  _World(this.container, this.images);
  final ProviderContainer container;
  final List<String> images;
}

Future<_World> _pump(
  WidgetTester tester,
  List<TonightSkyObject> ranked, {
  Size size = const Size(390, 1600),
  double text = 1,
  bool siteSet = true,
  bool linkUp = true,
  SequenceRunState run = SequenceRunState.idle,
  List<String> queued = const [],
  List<String> collected = const [],
  List<int>? builds,
  SimpleRunNotifier Function() simpleRun = _FakeRun.new,
}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  tester.platformDispatcher.textScaleFactorTestValue = text;
  addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
  final images = <String>[];
  final container = ProviderContainer(
    overrides: [
      siteSettingsProvider.overrideWith(() => _Site(siteSet)),
      // A live Stream.periodic would outlive the test; the list follows this
      // provider, so a tick is simulated by invalidating the ranking.
      skyClockProvider.overrideWith((ref) => const Stream<int>.empty()),
      tonightsBestProvider.overrideWith((ref) async {
        builds?.add(builds.length);
        return ranked;
      }),
      statsTargetsProvider.overrideWith(() => _Stats(collected)),
      serverLinkUpProvider.overrideWithValue(linkUp),
      sequenceRunStateProvider.overrideWith(() => _Run(run)),
      simpleRunProvider.overrideWith(simpleRun),
      tonightsBestPrefsServiceProvider.overrideWithValue(
        TonightsBestPrefsService(
          supportDir: () async => throw StateError('no disk'),
        ),
      ),
    ],
  );
  addTearDown(container.dispose);
  container.read(simpleRunProvider.notifier).state = SimpleRun(
    targets: [for (final n in queued) SimpleTarget(name: n)],
  );
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: TonightsBestScreen(
          onImage: (o) => images.add(o.id),
          onBack: () {},
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
  return _World(container, images);
}

Future<void> _tap(WidgetTester tester, String key) async {
  final f = find.byKey(Key(key));
  await tester.ensureVisible(f);
  await tester.tap(f);
  await tester.pumpAndSettle();
}

List<String> _order(WidgetTester tester) {
  final ids = <String>[];
  for (final e
      in find
          .byWidgetPredicate(
            (w) =>
                w.key is ValueKey<String> &&
                (w.key! as ValueKey<String>).value.startsWith('best-row-'),
          )
          .evaluate()) {
    ids.add((e.widget.key! as ValueKey<String>).value.substring(9));
  }
  return ids;
}

void main() {
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

  testWidgets('a row tap shows the Target on the sky; Image this plans it', (
    tester,
  ) async {
    final w = await _pump(tester, [a, c, b]);
    await tester.tap(find.byKey(const Key('best-row-A')));
    await tester.pump();
    expect(w.container.read(planetariumSelectionProvider)?.name, 'A');
    expect(w.images, isEmpty, reason: 'a row tap never plans');
    await tester.tap(find.byKey(const Key('best-image-C')));
    await tester.pump();
    expect(w.container.read(planetariumSelectionProvider)?.name, 'C');
    expect(w.images, ['C']);
    expect(
      w.container.read(simpleRunProvider).targets,
      isEmpty,
      reason: 'no plan running: nothing queued',
    );
  });

  testWidgets('No rig: Image this stays Image this and opens the plan sheet', (
    tester,
  ) async {
    final w = await _pump(tester, [a], linkUp: false);
    expect(find.byKey(const Key('best-offline')), findsOneWidget);
    expect(find.text('Rig not connected'), findsOneWidget);
    expect(
      find.descendant(
        of: find.byKey(const Key('best-image-A')),
        matching: find.text('Image this'),
      ),
      findsOneWidget,
    );
    await tester.tap(find.byKey(const Key('best-image-A')));
    await tester.pump();
    expect(w.images, ['A']);
    expect(
      find.byKey(const Key('best-list')),
      findsOneWidget,
      reason: 'the list shows in full',
    );
  });

  testWidgets('the offline note, the controls and the rows sit s8 apart', (
    tester,
  ) async {
    await _pump(tester, [a], linkUp: false);
    final note = tester.getRect(find.byKey(const Key('best-offline')));
    expect(tester.getTopLeft(find.text('Sort')).dy - note.bottom, AraSpace.s8);
    final count = tester.getRect(find.byKey(const Key('best-count')));
    final row = tester.getRect(find.byKey(const Key('best-row-A')));
    expect(row.top - count.bottom, AraSpace.s8);
  });

  testWidgets('a Target below the horizon now says so in the altitude line', (
    tester,
  ) async {
    await _pump(tester, [_o('LOW', alt: -8), _o('HIGH', alt: 33)]);
    expect(
      find.descendant(
        of: find.byKey(const Key('best-altitude-LOW')),
        matching: find.text('Now: below the horizon'),
      ),
      findsNothing,
    );
    expect(
      tester.widget<Text>(find.byKey(const Key('best-altitude-LOW'))).data,
      'Now: below the horizon',
    );
    expect(
      tester.widget<Text>(find.byKey(const Key('best-altitude-HIGH'))).data,
      'Now 33°',
    );
    expect(
      find.byKey(const Key('best-row-LOW')),
      findsOneWidget,
      reason: 'kept in All night',
    );
  });

  testWidgets('the Sort segments reorder and Refresh ranks again', (
    tester,
  ) async {
    final builds = <int>[];
    await _pump(tester, [a, c, b], builds: builds);
    expect(_order(tester), ['A', 'C', 'B']);
    await _tap(tester, 'best-sort-altitude');
    expect(_order(tester), ['C', 'B', 'A']);
    await _tap(tester, 'best-sort-transit');
    expect(_order(tester), ['B', 'C', 'A']);
    await _tap(tester, 'best-sort-score');
    expect(_order(tester), ['A', 'C', 'B']);
    final before = builds.length;
    await _tap(tester, 'best-refresh');
    expect(builds.length, before + 1);
  });

  testWidgets('a filter that hides every Target offers Clear filters', (
    tester,
  ) async {
    final w = await _pump(tester, [a, b]);
    await _tap(tester, 'best-type-cluster');
    expect(find.byKey(const Key('best-empty-match')), findsOneWidget);
    expect(find.text('No Targets match'), findsOneWidget);
    // The controls stay so the filter can be changed.
    expect(find.byKey(const Key('best-type-cluster')), findsOneWidget);
    await _tap(tester, 'best-clear-filters');
    expect(find.byKey(const Key('best-list')), findsOneWidget);
    expect(w.container.read(tonightsBestChoicesProvider).filtered, isFalse);
  });

  testWidgets(
    'Fits my field is a pair of segments, disabled with a reason when no framing is known',
    (tester) async {
      final w = await _pump(tester, [
        _o('A', framing: TonightFraming.unknown),
        _o('B', framing: TonightFraming.unknown),
      ]);
      expect(find.byType(Switch), findsNothing);
      expect(find.byKey(const Key('best-fits-on')), findsOneWidget);
      expect(find.byKey(const Key('best-fits-off')), findsOneWidget);
      expect(find.byKey(const Key('best-field-unknown')), findsOneWidget);
      expect(
        find.text(
          'Field not known. Set your camera and telescope in Settings > Rig.',
        ),
        findsOneWidget,
      );
      // Disabled as a widget (N12), not only by what a tap does.
      final sem = tester.getSemantics(find.byKey(const Key('best-fits-on')));
      expect(
        sem.getSemanticsData().flagsCollection.isEnabled,
        Tristate.isFalse,
      );
      final ink = tester.widget<InkWell>(
        find.descendant(
          of: find.byKey(const Key('best-fits-on')),
          matching: find.byType(InkWell),
        ),
      );
      expect(ink.onTap, isNull);
      await tester.tap(
        find.byKey(const Key('best-fits-on')),
        warnIfMissed: false,
      );
      await tester.pump();
      expect(w.container.read(tonightsBestChoicesProvider).fitsField, isFalse);
      expect(_order(tester), ['A', 'B']);
      expect(find.byKey(const Key('best-empty-match')), findsNothing);
    },
  );

  testWidgets('Peak altitude and Fits my field narrow the list, Clear resets', (
    tester,
  ) async {
    final w = await _pump(tester, [
      _o('OK', maxAlt: 80),
      _o('LOW', maxAlt: 40),
      _o('BIG', maxAlt: 80, framing: TonightFraming.tooBig),
    ]);
    expect(find.byKey(const Key('best-field-unknown')), findsNothing);
    await _tap(tester, 'best-peak-60');
    expect(_order(tester), ['OK', 'BIG']);
    await _tap(tester, 'best-fits-on');
    expect(_order(tester), ['OK']);
    await _tap(tester, 'best-type-nebula');
    expect(find.byKey(const Key('best-empty-match')), findsOneWidget);
    await _tap(tester, 'best-clear-filters');
    final got = w.container.read(tonightsBestChoicesProvider);
    expect(got.minPeakAltDeg, 0);
    expect(got.fitsField, isFalse);
    expect(got.types, isEmpty);
    expect(_order(tester), ['OK', 'LOW', 'BIG']);
  });

  testWidgets(
    'Image this with a plan running adds to the Queue, shows the pill, then In Queue disabled',
    (tester) async {
      final w = await _pump(tester, [a, b], run: SequenceRunState.running);
      expect(
        find.descendant(
          of: find.byKey(const Key('best-image-A')),
          matching: find.text('Add to Queue'),
        ),
        findsOneWidget,
      );
      await tester.tap(find.byKey(const Key('best-image-A')));
      await tester.pumpAndSettle();
      expect(w.container.read(simpleRunProvider).targets.map((t) => t.name), [
        'A',
      ]);
      expect(
        w.images,
        isEmpty,
        reason: 'a running plan is added to, not replanned',
      );
      final pill = w.container.read(simpleMessagesProvider).single;
      expect(pill.title, 'A added to the Queue');
      expect(pill.kind, SimpleMessageKind.result);
      final button = tester.widget<FilledButton>(
        find.byKey(const Key('best-image-A')),
      );
      expect(button.onPressed, isNull);
      expect(
        find.descendant(
          of: find.byKey(const Key('best-image-A')),
          matching: find.text('In Queue'),
        ),
        findsOneWidget,
      );
      expect(
        find.text('In Queue'),
        findsNWidgets(2),
        reason: 'marker and button',
      );
      // Dismiss the pill timer.
      w.container.read(simpleMessagesProvider.notifier).dismiss(pill.id);
    },
  );

  testWidgets('a refused add still says why after Back closes the page', (
    tester,
  ) async {
    final answer = Completer<void>();
    final w = await _pump(
      tester,
      [a],
      run: SequenceRunState.running,
      simpleRun: () => _RefusedRun(answer),
    );
    await tester.tap(find.byKey(const Key('best-image-A')));
    await tester.pump();
    // Back before the rig answers: the page leaves, the providers stay.
    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: w.container,
        child: const SizedBox(),
      ),
    );
    answer.complete();
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);
    final pill = w.container.read(simpleMessagesProvider).single;
    expect(pill.kind, SimpleMessageKind.error);
    expect(pill.title, 'The plan is ending.');
  });

  testWidgets('a queued Target still opens the plan sheet when no plan runs', (
    tester,
  ) async {
    final w = await _pump(tester, [a], queued: ['A']);
    final button = tester.widget<FilledButton>(
      find.byKey(const Key('best-image-A')),
    );
    expect(button.onPressed, isNotNull);
    await tester.tap(find.byKey(const Key('best-image-A')));
    await tester.pump();
    expect(w.images, ['A']);
  });

  testWidgets(
    'empty state: no site says Set your site and hides the controls',
    (tester) async {
      await _pump(tester, [a], siteSet: false);
      expect(find.text('Set your site'), findsOneWidget);
      expect(find.byKey(const Key('best-set-site')), findsOneWidget);
      expect(
        find.byKey(const Key('best-sort-score')),
        findsNothing,
        reason: 'nothing to sort',
      );
    },
  );

  testWidgets(
    'empty state: a site and an empty ranked list says nothing is well placed',
    (tester) async {
      await _pump(tester, const []);
      expect(find.text('Nothing well placed tonight'), findsOneWidget);
      expect(find.byKey(const Key('best-search')), findsOneWidget);
      expect(find.byKey(const Key('best-sort-score')), findsNothing);
    },
  );

  // N10: every row cut as never rising is "nothing well placed", not a filter
  // that hides everything (Clear filters would do nothing).
  testWidgets(
    'empty state: a list of Targets that never rise says nothing is well placed',
    (tester) async {
      await _pump(tester, [_o('N1', maxAlt: 0), _o('N2', maxAlt: -5)]);
      expect(find.text('Nothing well placed tonight'), findsOneWidget);
      expect(find.byKey(const Key('best-clear-filters')), findsNothing);
    },
  );

  testWidgets('markers read as a word plus a glyph, never colour alone', (
    tester,
  ) async {
    await _pump(
      tester,
      [
        _o('Q', name: 'Queued one'),
        _o(
          'K',
          name: 'Collected one',
          moonSep: 28,
          framing: TonightFraming.tooBig,
        ),
      ],
      queued: ['Q'],
      collected: ['Collected one'],
    );
    final q = find.byKey(const Key('best-row-Q'));
    final k = find.byKey(const Key('best-row-K'));
    expect(
      find.descendant(of: q, matching: find.text('In Queue')),
      findsOneWidget,
    );
    expect(
      find.descendant(of: q, matching: find.byIcon(Icons.playlist_add_check)),
      findsOneWidget,
    );
    expect(
      find.descendant(of: k, matching: find.text('Collected')),
      findsOneWidget,
    );
    expect(
      find.descendant(of: k, matching: find.byIcon(Icons.check_circle_outline)),
      findsOneWidget,
    );
    expect(
      find.descendant(
        of: k,
        matching: find.text('Moon 28° away: brighter sky'),
      ),
      findsOneWidget,
    );
    expect(
      find.descendant(of: k, matching: find.byIcon(Icons.nightlight_round)),
      findsOneWidget,
    );
    expect(
      find.descendant(of: k, matching: find.text('Needs a mosaic')),
      findsOneWidget,
    );
    expect(
      find.descendant(of: k, matching: find.byIcon(Icons.grid_view)),
      findsOneWidget,
    );
  });

  testWidgets('on a wide window Sort and Show sit side by side', (
    tester,
  ) async {
    await _pump(tester, [a, c, b], size: const Size(1280, 800));
    final top = tester.getTopLeft(find.byKey(const Key('best-sort-score'))).dy;
    expect(tester.getTopLeft(find.byKey(const Key('best-show-all'))).dy, top);
  });

  testWidgets(
    'no overflow at 2x text and 390 px, every target at least 44 px',
    (tester) async {
      await _pump(
        tester,
        [
          _o(
            'NGC 7000',
            name: 'North America Nebula',
            type: 'EmN',
            moonSep: 20,
            framing: TonightFraming.tooSmall,
            transit: _now,
          ),
          _o('M 31', name: 'Andromeda Galaxy'),
        ],
        size: const Size(390, 844),
        text: 2,
        // Offline, so the note is on the page too.
        linkUp: false,
        queued: ['M 31'],
        collected: ['M 31'],
      );
      expect(tester.takeException(), isNull);
      // A phone's height leaves the lower rows unbuilt until scrolled to.
      final list = find
          .descendant(
            of: find.byKey(const Key('best-list')),
            matching: find.byType(Scrollable),
          )
          .first;
      for (final k in [
        'best-back',
        'best-refresh',
        'best-sort-score',
        'best-show-up',
        'best-fits-on',
        'best-peak-30',
        'best-type-galaxy',
        'best-image-NGC 7000',
      ]) {
        final f = find.byKey(Key(k));
        await tester.scrollUntilVisible(f, 100, scrollable: list);
        final r = tester.getRect(f);
        expect(r.height, greaterThanOrEqualTo(44), reason: '$k height');
        expect(r.width, greaterThanOrEqualTo(44), reason: '$k width');
      }
    },
  );
}
