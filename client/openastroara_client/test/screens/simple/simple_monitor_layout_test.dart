import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/screens/simple/simple_monitor_screen.dart';
import 'package:openastroara/services/monitor_device_service.dart';
import 'package:openastroara/state/simple_monitor_state.dart';
import 'package:openastroara/theme/ara_metrics.dart';

// Monitor's grid (#1875 slice 7): which tiles it builds for the rig, how a
// tap enlarges one, and what the graph tiles say.

class _KeepAwake extends KeepAwake {
  const _KeepAwake();
  @override
  Future<void> set(bool on) async {}
}

class _Fixed extends MonitorState {
  _Fixed(this.snap);
  final MonitorSnapshot snap;
  @override
  MonitorSnapshot build() => snap;
}

final _asOf = DateTime(2026, 11, 15, 0, 23);

List<MonitorFrame> _frames(List<double> hfr, {List<int>? stars}) => [
  for (var i = 0; i < hfr.length; i++)
    MonitorFrame(
      id: 'f$i',
      hfr: hfr[i],
      stars: stars?[i] ?? 390,
      background: 2400,
    ),
];

MonitorSnapshot _snap({
  bool gear = true,
  List<MonitorFrame> frames = const [],
}) => MonitorSnapshot(
  asOf: _asOf,
  verdict: const MonitorVerdict(
    MonitorVerdictKind.allGood,
    MonitorStatus.normal,
    target: 'NGC 1269',
  ),
  now: MonitorNow(
    status: MonitorStatus.working,
    word: MonitorNowWord.exposing,
    target: 'NGC 1269',
    subSeconds: 60,
    frameEndsAt: _asOf.add(const Duration(seconds: 36)),
    frameSeconds: 60,
    frame: 27,
    frames: 120,
    targetSecondsLeft: 5640,
    filters: const [(filter: '', done: 26, total: 120, inHand: true)],
  ),
  upNext: [
    MonitorUpNext(
      MonitorUpKind.nextFrame,
      at: _asOf.add(const Duration(seconds: 36)),
      frame: 28,
    ),
  ],
  target: MonitorTarget(
    name: 'NGC 1269',
    status: MonitorStatus.working,
    word: MonitorTargetWord.tracking,
    curve: [
      for (var i = 0; i <= 48; i++) 20 + 66 * (1 - ((i - 24) / 24).abs()),
    ],
    spanStart: DateTime(2026, 11, 14, 23, 57),
    spanEnd: DateTime(2026, 11, 15, 1, 57),
  ),
  flip: MonitorFlip(
    state: 'due_at',
    at: _asOf.add(const Duration(minutes: 35)),
  ),
  safety: gear
      ? const MonitorSafety(status: MonitorStatus.normal, safe: true)
      : null,
  weather: gear
      ? const MonitorWeather(
          status: MonitorStatus.normal,
          cells: [
            (cell: MonitorWeatherCell.cloud, value: 6, overLimit: null),
            (cell: MonitorWeatherCell.air, value: 11.4, overLimit: null),
          ],
        )
      : null,
  guider: MonitorGuider(
    status: MonitorStatus.working,
    word: MonitorGuiderWord.guiding,
    rms: 0.62,
    ra: 0.41,
    dec: 0.46,
    trace: [
      for (var i = 0; i < 50; i++)
        (ra: 0.4 * (i % 5 - 2), dec: 0.3 * (i % 3 - 1)),
    ],
    rmsPerFrame: [for (var i = 0; i < frames.length; i++) 1.1 + i * 0.01],
  ),
  lastFrame: MonitorLastFrame(frames: frames),
  night: MonitorNight(
    darkStart: DateTime(2026, 11, 14, 21, 50),
    darkEnd: DateTime(2026, 11, 15, 4, 20),
    flipAt: DateTime(2026, 11, 15, 0, 58),
    parkAt: DateTime(2026, 11, 15, 1, 58),
  ),
);

Future<List<MonitorDestination>> _pump(
  WidgetTester tester,
  MonitorSnapshot snap, {
  Size size = const Size(1288, 780),
}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final opened = <MonitorDestination>[];
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        keepAwakeProvider.overrideWithValue(const _KeepAwake()),
        monitorProvider.overrideWith(() => _Fixed(snap)),
      ],
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: SimpleMonitorScreen(onOpen: opened.add),
      ),
    ),
  );
  await tester.pump();
  return opened;
}

/// Records each painted paragraph's height while it is still alive.
class _Heights implements Canvas {
  final heights = <double>[];
  @override
  void drawParagraph(ui.Paragraph paragraph, Offset offset) =>
      heights.add(paragraph.height);
  @override
  dynamic noSuchMethod(Invocation invocation) => null;
}

Finder _k(String id) => find.byKey(Key('simple-monitor-$id'));

void main() {
  testWidgets('all gear: every tile is drawn at 1288x780', (tester) async {
    await _pump(tester, _snap());
    for (final id in [
      'now',
      'safety',
      'target',
      'conditions',
      'guider',
      'hfr',
      'stars',
      'flip',
      'night',
    ]) {
      expect(_k(id), findsOneWidget, reason: id);
    }
    expect(_k('title'), findsOneWidget);
  });

  testWidgets(
    'no Safety and no weather: their tiles are not built and the grid reflows',
    (tester) async {
      await _pump(tester, _snap(gear: false));
      expect(_k('safety'), findsNothing);
      expect(_k('conditions'), findsNothing);
      final alt = tester.getRect(_k('target')),
          hfr = tester.getRect(_k('hfr')),
          stars = tester.getRect(_k('stars'));
      // Target altitude takes two columns, HFR and Stars one each under it.
      expect(alt.width, closeTo(hfr.width + stars.width + 8, 1));
      expect(alt.left, hfr.left);
      // The flip sits beside Tonight.
      expect(tester.getRect(_k('flip')).top, tester.getRect(_k('night')).top);
    },
  );

  testWidgets(
    'a tap enlarges a tile inside Monitor, a second tap restores the grid',
    (tester) async {
      final opened = await _pump(tester, _snap());
      await tester.tap(_k('hfr'));
      await tester.pump();
      expect(find.byKey(const Key('simple-monitor-enlarged')), findsOneWidget);
      expect(_k('stars'), findsNothing);
      expect(opened, isEmpty, reason: 'a tile tap no longer leaves Monitor');
      await tester.tap(_k('hfr'));
      await tester.pump();
      expect(find.byKey(const Key('simple-monitor-enlarged')), findsNothing);
      expect(_k('stars'), findsOneWidget);
    },
  );

  testWidgets(
    'the enlarged tile has one button to the window the tap used to open',
    (tester) async {
      final opened = await _pump(tester, _snap());
      await tester.tap(_k('flip'));
      await tester.pump();
      await tester.tap(find.byKey(const Key('simple-monitor-tile-open')));
      expect(opened, [MonitorDestination.mount]);
    },
  );

  testWidgets('a phone: a tap enlarges a tile in the one column too', (
    tester,
  ) async {
    final opened = await _pump(tester, _snap(), size: const Size(390, 844));
    expect(_k('now'), findsOneWidget);
    await tester.tap(_k('now'));
    await tester.pump();
    expect(find.byKey(const Key('simple-monitor-enlarged')), findsOneWidget);
    await tester.ensureVisible(
      find.byKey(const Key('simple-monitor-tile-open')),
    );
    await tester.tap(find.byKey(const Key('simple-monitor-tile-open')));
    expect(opened, [MonitorDestination.queue]);
  });

  testWidgets('HFR flags a rise of 15 % or more against the session start', (
    tester,
  ) async {
    // First three 2.0, last three 2.36: +18 %.
    await _pump(
      tester,
      _snap(frames: _frames([2.0, 2.0, 2.0, 2.2, 2.3, 2.36, 2.36, 2.36])),
    );
    expect(find.text('HFR +18 % since session start'), findsOneWidget);
    expect(find.text('Drifting'), findsOneWidget);
  });

  testWidgets('no flag before six saved frames', (tester) async {
    await _pump(tester, _snap(frames: _frames([2.0, 2.0, 2.0, 2.6, 2.6])));
    expect(find.textContaining('since session start'), findsNothing);
  });

  testWidgets('stars flag a fall', (tester) async {
    await _pump(
      tester,
      _snap(
        frames: _frames(
          [2, 2, 2, 2, 2, 2, 2],
          stars: [400, 400, 400, 380, 330, 328, 326],
        ),
      ),
    );
    expect(find.text('Stars −18 % since session start'), findsOneWidget);
  });

  testWidgets('stars rising is no flag', (tester) async {
    await _pump(
      tester,
      _snap(
        frames: _frames(
          [2, 2, 2, 2, 2, 2, 2],
          stars: [400, 400, 400, 480, 490, 500, 500],
        ),
      ),
    );
    expect(find.textContaining('Stars +'), findsNothing);
    expect(find.textContaining('Stars −'), findsNothing);
  });

  testWidgets(
    'the guider graphs: a labelled fixed scale and the session RMS per frame',
    (tester) async {
      await _pump(tester, _snap(frames: _frames([2, 2, 2, 2, 2, 2])));
      expect(find.text('fixed ±2.5″'), findsOneWidget);
      expect(find.text('RMS per saved frame'), findsOneWidget);
      expect(find.byKey(const Key('simple-monitor-rms-chart')), findsOneWidget);
      expect(find.text('RA'), findsWidgets);
      expect(find.text('Dec'), findsWidgets);
    },
  );

  testWidgets(
    'the altitude graph names the plan span; Tonight labels the flip and park times',
    (tester) async {
      await _pump(tester, _snap());
      expect(find.byKey(const Key('simple-monitor-altitude')), findsOneWidget);
      expect(find.text('Imaging 23:57 to 01:57'), findsOneWidget);
      expect(find.byKey(const Key('simple-monitor-timeline')), findsOneWidget);
    },
  );

  testWidgets('the status word sits at the right corner of the tile header', (
    tester,
  ) async {
    await _pump(tester, _snap());
    final tile = tester.getRect(_k('now'));
    final word = tester.getRect(
      find.descendant(of: _k('now'), matching: find.text('Exposing')),
    );
    expect(tile.right - word.right, lessThan(30));
  });

  testWidgets(
    "a screen reader reaches the enlarged tile's button to its window",
    (tester) async {
      final semantics = tester.ensureSemantics();
      final opened = await _pump(tester, _snap());
      for (final (id, to) in [
        ('hfr', MonitorDestination.frameInfo),
        ('safety', MonitorDestination.safety),
      ]) {
        await tester.tap(_k(id));
        await tester.pump();
        expect(find.bySemanticsLabel('Open its window'), findsOneWidget);
        tester.semantics.tap(find.semantics.byLabel('Open its window'));
        expect(opened.last, to, reason: id);
        await tester.tap(_k(id));
        await tester.pump();
      }
      semantics.dispose();
    },
  );

  testWidgets('a long tile name and the status word keep a gap between them', (
    tester,
  ) async {
    await _pump(
      tester,
      _snap(frames: _frames([2.0, 2.0, 2.0, 2.2, 2.3, 2.36, 2.36, 2.36])),
    );
    final name = tester.getRect(
      find.descendant(of: _k('hfr'), matching: find.text('HFR')),
    );
    final word = tester.getRect(
      find.descendant(of: _k('hfr'), matching: find.text('Drifting')),
    );
    expect(word.left - name.right, greaterThanOrEqualTo(6));
  });

  testWidgets(
    "Tonight's hour marks and flip and park times follow 2x text at caption size",
    (tester) async {
      tester.platformDispatcher.textScaleFactorTestValue = 2;
      addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
      await _pump(tester, _snap(gear: false));
      final paint = tester.widget<CustomPaint>(
        find.byKey(const Key('simple-monitor-timeline')),
      );
      final canvas = _Heights();
      paint.painter!.paint(
        canvas,
        tester.getSize(find.byKey(const Key('simple-monitor-timeline'))),
      );
      final heights = canvas.heights;
      // No plan blocks here: each paragraph is an hour mark, a time or a glyph.
      final tall = heights.where((h) => h >= 2 * AraFontSize.simpleCaption);
      expect(tall.length, greaterThanOrEqualTo(3), reason: '$heights');
    },
  );
}
