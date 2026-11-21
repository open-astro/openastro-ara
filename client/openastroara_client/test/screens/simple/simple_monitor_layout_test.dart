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
import 'package:openastroara/widgets/emergency_stop_button.dart';

// Monitor's grid (#2014): which tiles it builds for the rig, how a
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

/// HFR 2.0 for the first three frames, 2.36 for the last three: +18 %.
final _drifting = _frames([2.0, 2.0, 2.0, 2.2, 2.3, 2.36, 2.36, 2.36]);

MonitorSnapshot _snap({
  bool gear = true,
  bool guider = true,
  MonitorConditions? conditions,
  List<MonitorFrame> frames = const [],
  bool frozen = false,
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
  guider: guider
      ? MonitorGuider(
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
        )
      : null,
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
  Locale? locale,
  void Function(MonitorDestination)? onOpen,
}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final opened = <MonitorDestination>[];
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

        home: SimpleMonitorScreen(
          onOpen: (d) {
            opened.add(d);
            onOpen?.call(d);
          },
        ),
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
  return opened;
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

  testWidgets('a long press opens the tile\'s window, enlarged or not', (
    tester,
  ) async {
    final opened = await _pump(tester, _snap());
    await tester.longPress(_k('flip'));
    expect(opened, [MonitorDestination.mount]);
    expect(
      _k('stars'),
      findsOneWidget,
      reason: 'a long press does not enlarge',
    );
    await tester.tap(_k('hfr'));
    await tester.pump();
    await tester.longPress(_k('hfr'));
    expect(opened.last, MonitorDestination.frameInfo);
    expect(
      find.byKey(const Key('simple-monitor-tile-open')),
      findsNothing,
      reason: 'no visible Open button',
    );
  });

  testWidgets('the Safety strip does not enlarge: a tap opens its window', (
    tester,
  ) async {
    final opened = await _pump(tester, _snap());
    await tester.tap(_k('safety'));
    await tester.pump();
    expect(opened, [MonitorDestination.safety]);
    expect(find.byKey(const Key('simple-monitor-enlarged')), findsNothing);
    await tester.longPress(_k('safety'));
    expect(opened.last, MonitorDestination.safety);
  });

  testWidgets('an enlarged tile leaves Emergency Stop on screen beside it', (
    tester,
  ) async {
    await _pump(tester, _snap());
    final stop = find.byType(EmergencyStopButton);
    for (final id in ['guider', 'hfr', 'conditions']) {
      await tester.tap(_k(id));
      await tester.pump();
      expect(stop, findsOneWidget);
      final big = tester.getRect(
        find.byKey(const Key('simple-monitor-enlarged')),
      );
      final tile = tester.getRect(_k(id));
      final s = tester.getRect(stop);
      expect(tile.overlaps(s), isFalse, reason: id);
      expect(tile.width, greaterThan(500), reason: id);
      expect(big.contains(tile.center), isTrue);
      await tester.tap(_k(id));
      await tester.pump();
    }
  });

  testWidgets(
    'a screen reader is offered Enlarge or Shrink and, apart, Open <window>',
    (tester) async {
      final semantics = tester.ensureSemantics();
      final opened = await _pump(tester, _snap());
      List<CustomSemanticsAction> actions() => [
        for (final id
            in tester
                .getSemantics(_k('flip'))
                .getSemanticsData()
                .customSemanticsActionIds!)
          CustomSemanticsAction.getAction(id)!,
      ];
      var node = tester.getSemantics(_k('flip'));
      expect(node.getSemanticsData().hasAction(SemanticsAction.tap), isTrue);
      expect([
        for (final a in actions()) a.hint ?? a.label,
      ], unorderedEquals(['Enlarge', 'Open Mount']));
      expect(node.label, isNot(contains('enlarged')));
      final open = actions().firstWhere((a) => a.label == 'Open Mount');
      tester.semantics.performAction(
        find.semantics.byLabel(RegExp('^Meridian flip')),
        SemanticsAction.customAction,
        args: CustomSemanticsAction.getIdentifier(open),
      );
      expect(opened, [MonitorDestination.mount]);
      tester.semantics.tap(find.semantics.byLabel(RegExp('^Meridian flip')));
      await tester.pump();
      node = tester.getSemantics(_k('flip'));
      expect(node.label, contains('enlarged'));
      expect([
        for (final a in actions()) a.hint ?? a.label,
      ], unorderedEquals(['Shrink', 'Open Mount']));
      semantics.dispose();
    },
  );

  testWidgets('the title is a heading with its icon', (tester) async {
    final semantics = tester.ensureSemantics();
    await _pump(tester, _snap());
    expect(find.text('Monitor'), findsOneWidget);
    expect(find.byIcon(Icons.monitor_heart_outlined), findsOneWidget);
    expect(
      tester.getSemantics(find.text('Monitor')).flagsCollection.isHeader,
      isTrue,
    );
    semantics.dispose();
  });

  testWidgets(
    'a phone: a tap enlarges a tile in the one column, a long press opens it',
    (tester) async {
      final opened = await _pump(tester, _snap(), size: const Size(390, 844));
      expect(_k('night'), findsOneWidget);
      await tester.tap(_k('night'));
      await tester.pump();
      expect(find.byKey(const Key('simple-monitor-enlarged')), findsOneWidget);
      await tester.longPress(_k('night'));
      expect(opened, [MonitorDestination.queue]);
    },
  );

  for (final (name, snap) in [
    ('full rig', _snap()),
    ('no Safety, no Conditions', _snap(gear: false)),
    ('no Guider', _snap(guider: false)),
    ('no Guider, no Safety, no Conditions', _snap(gear: false, guider: false)),
    (
      'Conditions with no weather station, no Safety',
      _snap(
        conditions: const MonitorConditions(
          rig: MonitorRigTemps(sensorC: -9.6),
        ),
      ).copyWithoutSafety(),
    ),
  ]) {
    testWidgets(
      '$name: tiles are inside the grid, none overlaps, none is zero',
      (tester) async {
        await _pump(tester, snap);
        final ids = [
          'now',
          if (snap.safety != null) 'safety',
          'target',
          if (snap.conditions != null) 'conditions',
          if (snap.guider != null) 'guider',
          'hfr',
          'stars',
          'flip',
          'night',
        ];
        final rects = {for (final id in ids) id: tester.getRect(_k(id))};
        final screen = Offset.zero & const Size(1288, 780);
        for (final MapEntry(:key, :value) in rects.entries) {
          expect(
            screen.contains(value.topLeft) &&
                screen.contains(value.bottomRight),
            isTrue,
            reason: key,
          );
          expect(value.width, greaterThan(100), reason: key);
          expect(value.height, greaterThan(50), reason: key);
          for (final other in rects.entries.where((e) => e.key != key)) {
            expect(
              value.deflate(1).overlaps(other.value.deflate(1)),
              isFalse,
              reason: '$key and ${other.key}',
            );
          }
        }
      },
    );
  }

  testWidgets('no Guider: HFR and Stars take its place beside Now', (
    tester,
  ) async {
    await _pump(tester, _snap(guider: false));
    expect(_k('guider'), findsNothing);
    final now = tester.getRect(_k('now'));
    final hfr = tester.getRect(_k('hfr'));
    final stars = tester.getRect(_k('stars'));
    expect(hfr.left, now.left);
    expect(stars.right, closeTo(now.right, 1));
    expect(hfr.top, greaterThan(now.bottom - 1));
  });

  testWidgets(
    'Conditions with no weather station shows the camera and focuser group alone',
    (tester) async {
      await _pump(
        tester,
        _snap(
          conditions: const MonitorConditions(
            rig: MonitorRigTemps(
              sensorC: -9.6,
              sensorTargetC: -10,
              coolerPowerPct: 62,
              focuserC: 7.5,
            ),
          ),
        ),
      );
      expect(_in('conditions', 'Camera and focuser'), findsOneWidget);
      for (final text in ['Sensor · -10', 'Cooler power', 'Focuser']) {
        expect(_in('conditions', text), findsOneWidget, reason: text);
      }
      expect(_in('conditions', 'Cloud'), findsNothing);
    },
  );

  testWidgets('Conditions with weather and rig shows both groups', (
    tester,
  ) async {
    await _pump(tester, _snap());
    expect(_in('conditions', 'Cloud'), findsOneWidget);
    expect(_in('conditions', 'Camera and focuser'), findsOneWidget);
  });

  testWidgets('Conditions at 2x text does not overflow', (tester) async {
    tester.platformDispatcher.textScaleFactorTestValue = 2;
    addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
    await _pump(tester, _snap(), size: const Size(1280, 800));
    final wide = tester.takeException();
    expect(wide, isNull);
  });

  testWidgets('an enlarged tile whose gear leaves the profile is shrunk', (
    tester,
  ) async {
    await _pump(tester, _snap());
    await tester.tap(_k('guider'));
    await tester.pump();
    expect(find.byKey(const Key('simple-monitor-enlarged')), findsOneWidget);
    await tester.pumpWidget(const SizedBox());
    await _pump(tester, _snap(guider: false));
    expect(find.byKey(const Key('simple-monitor-enlarged')), findsNothing);
    expect(_k('hfr'), findsOneWidget);
  });

  testWidgets(
    'Now has no blank band at 1288x780: Up next takes what the countdown leaves',
    (tester) async {
      await _pump(tester, _snap());
      final now = tester.getRect(_k('now'));
      final upNext = tester.getRect(
        find.descendant(
          of: _k('now'),
          matching: find.byKey(const Key('simple-monitor-up-next')),
        ),
      );
      expect(now.right - upNext.right, lessThan(AraSpace.s12 + 2));
      final countdown = tester.getRect(
        find.byKey(const Key('simple-monitor-countdown')),
      );
      expect(upNext.left - countdown.right, lessThan(80));
    },
  );

  testWidgets('a rig with neither source has no Conditions tile', (
    tester,
  ) async {
    await _pump(tester, _snap(gear: false));
    expect(_k('conditions'), findsNothing);
  });

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
    'a long press on Conditions opens the weather window when a station reports',
    (tester) async {
      final opened = <MonitorDestination>[];
      await _pump(tester, _snap(), onOpen: opened.add);
      await tester.longPress(_k('conditions'));
      expect(opened, [MonitorDestination.weather]);
    },
  );

  testWidgets(
    'a long press on Conditions with no weather station opens the camera window',
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
      await tester.longPress(_k('conditions'));
      expect(opened, [MonitorDestination.camera]);
    },
  );

  testWidgets(
    'a long press on Conditions with only a focuser temperature opens the focuser window',
    (tester) async {
      final opened = <MonitorDestination>[];
      await _pump(
        tester,
        _snap(
          conditions: const MonitorConditions(
            rig: MonitorRigTemps(focuserC: 7.5),
          ),
        ),
        onOpen: opened.add,
      );
      await tester.longPress(_k('conditions'));
      expect(opened, [MonitorDestination.focuser]);
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

extension on MonitorSnapshot {
  MonitorSnapshot copyWithoutSafety() => MonitorSnapshot(
    asOf: asOf,
    verdict: verdict,
    now: now,
    upNext: upNext,
    target: target,
    flip: flip,
    conditions: conditions,
    guider: guider,
    lastFrame: lastFrame,
    night: night,
  );
}
