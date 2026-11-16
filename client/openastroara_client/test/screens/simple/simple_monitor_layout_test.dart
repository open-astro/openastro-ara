import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/screens/simple/simple_monitor_screen.dart';
import 'package:openastroara/services/monitor_device_service.dart';
import 'package:openastroara/state/simple_monitor_state.dart';
import 'package:openastroara/theme/ara_metrics.dart';

// Monitor's graphs (#1875 slice 7a): what the graph tiles say and flag, the
// tile header's corner, and painters that keep inside their canvas.

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

Future<void> _pump(
  WidgetTester tester,
  MonitorSnapshot snap, {
  Size size = const Size(1288, 780),
}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        keepAwakeProvider.overrideWithValue(const _KeepAwake()),
        monitorProvider.overrideWith(() => _Fixed(snap)),
      ],
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: SimpleMonitorScreen(onOpen: (_) {}),
      ),
    ),
  );
  await tester.pump();
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
  await _pump(
    tester,
    _snap(frames: _frames([2.0, 2.0, 2.0, 2.2, 2.3, 2.36, 2.36, 2.36])),
    size: const Size(1280, 800),
  );
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

void main() {
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

  testWidgets('a long tile name and the status word keep a gap between them', (
    tester,
  ) async {
    await _pump(
      tester,
      _snap(frames: _frames([2.0, 2.0, 2.0, 2.2, 2.3, 2.36, 2.36, 2.36])),
    );
    final name = tester.getRect(
      find.descendant(of: _k('last-frame'), matching: find.text('Last frame')),
    );
    final word = tester.getRect(
      find.descendant(of: _k('last-frame'), matching: find.text('Drifting')),
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
      final canvas = _Painted();
      // Tall enough for every label: at 2x the tile has no room for the
      // hour marks, which are then left out.
      paint.painter!.paint(
        canvas,
        Size(tester.getSize(_k('timeline')).width, 120),
      );
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
}
