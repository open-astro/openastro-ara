import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/screens/simple/simple_monitor_screen.dart';
import 'package:openastroara/services/monitor_device_service.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_monitor_state.dart';
import 'package:openastroara/theme/ara_metrics.dart';

// Monitor's graphs (#1875 slice 7a): what the graph tiles say and flag, the
// tile header's corner (Tonight's dark-span line there only when it fits
// whole, read out once), and painters that keep inside their canvas.

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

/// HFR 2.0 for the first three frames, 2.36 for the last three: +18 %.
final _drifting = _frames([2.0, 2.0, 2.0, 2.2, 2.3, 2.36, 2.36, 2.36]);

MonitorSnapshot _snap({
  bool gear = true,
  List<MonitorFrame> frames = const [],
  bool frozen = false,
  MonitorConditions? conditions,
  List<MonitorUpNext>? upNext,
  MonitorVerdict? verdict,
}) => MonitorSnapshot(
  asOf: _asOf,
  frozenSince: frozen ? _asOf : null,
  verdict:
      verdict ??
      const MonitorVerdict(
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
  upNext:
      upNext ??
      [
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
  conditions:
      conditions ??
      (gear
          ? const MonitorConditions(
              weather: MonitorWeather(
                status: MonitorStatus.normal,
                cells: [
                  (cell: MonitorWeatherCell.cloud, value: 6, overLimit: null),
                  (cell: MonitorWeatherCell.air, value: 11.4, overLimit: null),
                ],
              ),
              rig: MonitorRigTemps(
                sensorC: -10.2,
                sensorTargetC: -10,
                coolerPowerPct: 41,
                focuserC: 8.4,
              ),
            )
          : null),
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

Future<void> _pump(
  WidgetTester tester,
  MonitorSnapshot snap, {
  Size size = const Size(1288, 780),
  Locale? locale,
  void Function(MonitorDestination)? onOpen,
}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(
    ProviderScope(
      // A fresh scope each pump: a kept one would keep the first snapshot.
      key: UniqueKey(),
      overrides: [
        keepAwakeProvider.overrideWithValue(const _KeepAwake()),
        monitorProvider.overrideWith(() => _Fixed(snap)),
      ],
      child: MaterialApp(
        locale: locale,
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: SimpleMonitorScreen(onOpen: onOpen ?? (_) {}),
      ),
    ),
  );
  await tester.pump();
  // A phone's column builds as it scrolls: jump to Tonight, its last tile (a
  // drag would scroll a tile's own content).
  if (size.width < kMonitorMinWidth) {
    final column = Scrollable.of(tester.element(_k('now'))).position;
    column.jumpTo(column.maxScrollExtent);
    await tester.pump();
  }
}

/// Records where each painted paragraph (while it is still alive), line and
/// path lands.
class _Painted implements Canvas {
  final text = <Rect>[], marks = <Rect>[];
  @override
  void drawParagraph(ui.Paragraph paragraph, Offset offset) =>
      text.add(offset & Size(0, paragraph.height));
  @override
  void drawLine(Offset p1, Offset p2, Paint paint) =>
      marks.add(Rect.fromPoints(p1, p2));
  @override
  void drawPath(Path path, Paint paint) => marks.add(path.getBounds());
  @override
  dynamic noSuchMethod(Invocation invocation) => null;
}

/// At 2x text and 1280x800, paints each graph in [ids] as laid out, on a
/// canvas shorter than one label and on a tall one: nothing lands outside
/// the canvas, and the tall one still has its labels.
Future<void> _inside(WidgetTester tester, List<String> ids) async {
  tester.platformDispatcher.textScaleFactorTestValue = 2;
  addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
  await _pump(tester, _snap(frames: _drifting), size: const Size(1280, 800));
  for (final id in ids) {
    final f = _k(id);
    for (final size in [
      tester.getSize(f),
      const Size(240, 20),
      const Size(240, 160),
    ]) {
      final canvas = _Painted();
      tester.widget<CustomPaint>(f).painter!.paint(canvas, size);
      final outside = [
        for (final r in [...canvas.text, ...canvas.marks])
          if (r.top < -0.01 || r.bottom > size.height + 0.01) r,
      ];
      expect(outside, isEmpty, reason: '$id at $size');
      if (size.height == 160) expect(canvas.text, isNotEmpty, reason: id);
    }
  }
}

Finder _k(String id) => find.byKey(Key('simple-monitor-$id'));

Finder _in(String id, String text) =>
    find.descendant(of: _k(id), matching: find.text(text));

/// Whether Tonight's [line] is cut short as laid out.
bool _cut(WidgetTester tester, String line) =>
    tester.renderObject<RenderParagraph>(_in('night', line)).didExceedMaxLines;

void main() {
  testWidgets('HFR flags a rise of 15 % or more against the session start', (
    tester,
  ) async {
    await _pump(tester, _snap(frames: _drifting));
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
    final word = tester.getRect(_in('now', 'Exposing'));
    expect(tile.right - word.right, lessThan(30));
  });

  testWidgets('a long tile name and the status word keep a gap between them', (
    tester,
  ) async {
    await _pump(tester, _snap(frames: _drifting));
    final name = tester.getRect(_in('last-frame', 'Last frame'));
    final word = tester.getRect(_in('last-frame', 'Drifting'));
    expect(word.left - name.right, greaterThanOrEqualTo(6));
  });

  testWidgets(
    "Tonight's hour marks and flip and park times follow 2x text at caption size",
    (tester) async {
      tester.platformDispatcher.textScaleFactorTestValue = 2;
      addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
      await _pump(tester, _snap(gear: false));
      final paint = tester.widget<CustomPaint>(_k('timeline'));
      final canvas = _Painted();
      // Tall enough for every label: at 2x the tile has no room for the
      // hour marks, which are then left out.
      final width = tester.getSize(_k('timeline')).width;
      paint.painter!.paint(canvas, Size(width, 120));
      final heights = [for (final r in canvas.text) r.height];
      // No plan blocks here: each paragraph is an hour mark, a time or a glyph.
      final tall = heights.where((h) => h >= 2 * AraFontSize.simpleCaption);
      expect(tall.length, greaterThanOrEqualTo(3), reason: '$heights');
    },
  );

  testWidgets(
    'at 2x text the altitude graph paints nothing outside its canvas',
    (tester) => _inside(tester, ['altitude']),
  );

  testWidgets(
    'at 2x text the guide trace paints nothing outside its canvas',
    (tester) => _inside(tester, ['trace']),
  );

  testWidgets(
    'at 2x text the per-frame graphs paint nothing outside their canvas',
    (tester) => _inside(tester, ['hfr-chart', 'stars-chart', 'rms-chart']),
  );

  testWidgets("a phone shows Tonight's dark-span line whole, in its own row", (
    tester,
  ) async {
    await _pump(tester, _snap(), size: const Size(390, 844));
    expect(_cut(tester, 'Dark 21:50 to 04:20 · 0 min imaged'), isFalse);
    // The timeline under that row keeps its 7 hour marks, 22 to 04.
    final canvas = _Painted();
    final night = tester.widget<CustomPaint>(_k('timeline')).painter!;
    night.paint(canvas, tester.getSize(_k('timeline')));
    expect(canvas.text, hasLength(4 + 7), reason: 'flip and park: glyph, time');
  });

  testWidgets(
    'a dark-span line too long for the corner takes its own row on a wide window too',
    (tester) async {
      tester.platformDispatcher.textScaleFactorTestValue = 2;
      addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
      const ptBR = Locale('pt', 'BR');
      await _pump(tester, _snap(), size: const Size(1280, 800), locale: ptBR);
      const line = 'Escuro de 21:50 a 04:20 · 0 min capturados';
      expect(_cut(tester, line), isFalse);
    },
  );

  testWidgets(
    "a screen reader hears Tonight's dark-span line once: in the corner, its own row or frozen",
    (tester) async {
      final handle = tester.ensureSemantics();
      const line = 'Dark 21:50 to 04:20 · 0 min imaged';
      for (final (size, frozen, corner) in [
        (const Size(1288, 780), false, true),
        (const Size(390, 844), false, false),
        (const Size(1288, 780), true, false),
      ]) {
        await _pump(tester, _snap(frozen: frozen), size: size);
        final dy = tester.getCenter(_in('night', 'Tonight')).dy;
        final inCorner = tester.getCenter(_in('night', line)).dy == dy;
        expect(inCorner, corner, reason: '$size, frozen: $frozen');
        final label = tester.getSemantics(_k('night')).label;
        expect(line.allMatches(label).length, 1, reason: label);
      }
      handle.dispose();
    },
  );

  testWidgets('Conditions holds the weather and a Camera and focuser group', (
    tester,
  ) async {
    await _pump(tester, _snap());
    expect(_in('conditions', 'Conditions'), findsOneWidget);
    expect(_in('conditions', 'Cloud'), findsOneWidget);
    expect(_in('conditions', 'Camera and focuser'), findsOneWidget);
    expect(_in('conditions', 'Sensor · -10'), findsOneWidget);
    expect(_in('conditions', 'Cooler power'), findsOneWidget);
    expect(_in('conditions', 'Focuser'), findsOneWidget);
    expect(_k('weather'), findsNothing);
  });

  testWidgets('a rig with no weather station shows the group alone', (
    tester,
  ) async {
    await _pump(
      tester,
      _snap(
        conditions: const MonitorConditions(
          rig: MonitorRigTemps(sensorC: -10.2),
        ),
      ),
    );
    expect(_in('conditions', 'Camera and focuser'), findsOneWidget);
    expect(_in('conditions', 'Sensor'), findsOneWidget);
    expect(_in('conditions', 'Cloud'), findsNothing);
    expect(_in('conditions', 'Cooler power'), findsNothing);
    expect(_in('conditions', 'Focuser'), findsNothing);
  });

  testWidgets(
    'a tap on Conditions opens the weather window when a station reports',
    (tester) async {
      final opened = <MonitorDestination>[];
      await _pump(tester, _snap(), onOpen: opened.add);
      await tester.tap(_k('conditions'));
      expect(opened, [MonitorDestination.weather]);
    },
  );

  testWidgets(
    'a tap on Conditions with no weather station opens the camera window',
    (tester) async {
      final opened = <MonitorDestination>[];
      await _pump(
        tester,
        _snap(
          conditions: const MonitorConditions(
            rig: MonitorRigTemps(sensorC: -10.2),
          ),
        ),
        onOpen: opened.add,
      );
      await tester.tap(_k('conditions'));
      expect(opened, [MonitorDestination.camera]);
    },
  );

  testWidgets(
    'a rig with no weather and no camera or focuser has no Conditions',
    (tester) async {
      await _pump(tester, _snap(gear: false));
      expect(_k('conditions'), findsNothing);
    },
  );

  testWidgets('an empty Up next shows no caption', (tester) async {
    await _pump(tester, _snap(upNext: const []));
    expect(_in('now', 'Up next'), findsNothing);
    await _pump(tester, _snap());
    expect(_in('now', 'Up next'), findsOneWidget);
  });

  testWidgets('the top line names Monitor, read as a heading', (tester) async {
    final handle = tester.ensureSemantics();
    await _pump(tester, _snap());
    expect(_k('title'), findsOneWidget);
    expect(
      tester.getSemantics(_k('title')),
      matchesSemantics(label: 'Monitor', isHeader: true),
    );
    handle.dispose();
  });

  testWidgets(
    "a wide window gives a stop's outcome the whole top line, no title",
    (tester) async {
      await _pump(
        tester,
        _snap(
          verdict: const MonitorVerdict(
            MonitorVerdictKind.message,
            MonitorStatus.warning,
            message: SimpleMessage(
              id: 1,
              kind: SimpleMessageKind.warning,
              title: 'Emergency Stop: done',
              sticky: true,
            ),
          ),
        ),
      );
      expect(_k('title'), findsNothing);
    },
  );

  testWidgets('a wide window above 1.3x text leaves the title out', (
    tester,
  ) async {
    tester.platformDispatcher.textScaleFactorTestValue = 1.5;
    addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
    await _pump(tester, _snap());
    expect(_k('title'), findsNothing);
  });
}
