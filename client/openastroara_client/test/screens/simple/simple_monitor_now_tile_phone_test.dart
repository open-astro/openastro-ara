import 'dart:io';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/screens/simple/simple_monitor_screen.dart';
import 'package:openastroara/services/monitor_device_service.dart';
import 'package:openastroara/state/simple_monitor_state.dart';

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

// The test font draws every glyph as a square, far wider than a real face,
// so fit is measured with the Roboto the Flutter SDK ships.
final _fonts =
    '${Platform.environment['FLUTTER_ROOT']}/bin/cache/artifacts/material_fonts';

Future<void> _loadFonts() async {
  ByteData bytes(String f) => ByteData.view(
    Uint8List.fromList(File('$_fonts/$f').readAsBytesSync()).buffer,
  );
  final roboto = FontLoader('Roboto');
  for (final f in [
    'Roboto-Regular.ttf',
    'Roboto-Medium.ttf',
    'Roboto-Bold.ttf',
  ]) {
    roboto.addFont(Future.value(bytes(f)));
  }
  await roboto.load();
  await (FontLoader(
    'MaterialIcons',
  )..addFont(Future.value(bytes('MaterialIcons-Regular.otf')))).load();
}

final _asOf = DateTime(2026, 11, 11, 23, 52);

/// The busiest Now tile the state builds: a frame in hand with its bar, four
/// Up next rows and a four-filter plan.
MonitorSnapshot _busy() => MonitorSnapshot(
  asOf: _asOf,
  verdict: const MonitorVerdict(
    MonitorVerdictKind.allGood,
    MonitorStatus.normal,
    target: 'M31',
  ),
  now: MonitorNow(
    status: MonitorStatus.working,
    word: MonitorNowWord.exposing,
    target: 'M31',
    filter: 'Ha',
    subSeconds: 300,
    frameEndsAt: _asOf.add(const Duration(seconds: 150)),
    frameSeconds: 300,
    frame: 42,
    frames: 110,
    targetSecondsLeft: 20400,
    filters: const [
      (filter: 'L', done: 20, total: 20, inHand: false),
      (filter: 'R', done: 10, total: 10, inHand: false),
      (filter: 'Ha', done: 11, total: 40, inHand: true),
      (filter: 'OIII', done: 0, total: 40, inHand: false),
    ],
  ),
  upNext: [
    MonitorUpNext(
      MonitorUpKind.nextFrame,
      at: _asOf.add(const Duration(seconds: 150)),
      frame: 43,
      name: 'Ha',
    ),
    MonitorUpNext(
      MonitorUpKind.meridianFlip,
      at: _asOf.add(const Duration(minutes: 40)),
    ),
    MonitorUpNext(
      MonitorUpKind.nextTarget,
      name: 'NGC 7000',
      at: _asOf.add(const Duration(hours: 5, minutes: 40)),
    ),
    MonitorUpNext(MonitorUpKind.park, at: _asOf.add(const Duration(hours: 7))),
  ],
  flip: MonitorFlip(
    state: 'due_at',
    at: _asOf.add(const Duration(minutes: 40)),
  ),
  lastFrame: const MonitorLastFrame(),
  night: const MonitorNight(),
);

Future<void> _pump(
  WidgetTester tester, {
  double text = 1,
  Size size = const Size(390, 844),
  MonitorSnapshot? snap,
  Locale? locale,
}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  tester.platformDispatcher.textScaleFactorTestValue = text;
  addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
  addTearDown(tester.view.reset);
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        keepAwakeProvider.overrideWithValue(const _KeepAwake()),
        monitorProvider.overrideWith(() => _Fixed(snap ?? _busy())),
      ],
      child: MaterialApp(
        locale: locale,
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: SimpleMonitorScreen(onOpen: (_) {}),
      ),
    ),
  );
  await tester.pump();
}

final _tile = find.byKey(const Key('simple-monitor-now'));

void main() {
  final hasFonts = File('$_fonts/Roboto-Regular.ttf').existsSync();
  setUpAll(() async {
    if (hasFonts) await _loadFonts();
  });
  // A dev box without the SDK fonts skips; CI must run them. No
  // takeException(): an overflow anywhere fails the case.
  final skip = !hasFonts && !Platform.environment.containsKey('CI');

  for (final size in const [
    Size(390, 844),
    Size(1280, 800),
    Size(1133, 744),
    Size(1024, 768),
  ]) {
    testWidgets(
      'the Now tile shows a busy exposure without scrolling at ${size.width.toInt()}x${size.height.toInt()} (#1915, #2014)',
      (tester) async {
        expect(hasFonts, isTrue, reason: 'Roboto not found under $_fonts');
        await _pump(tester, size: size);
        final scroll = find.descendant(
          of: _tile,
          matching: find.byType(Scrollable),
        );
        expect(scroll, findsOneWidget);
        // The tile scrolls only when larger text no longer fits (DESIGN.md).
        expect(
          tester.state<ScrollableState>(scroll).position.maxScrollExtent,
          0,
          reason: 'part of the Now tile is hidden below its edge',
        );
      },
      skip: skip,
    );
  }

  for (final size in const [Size(390, 844), Size(1280, 800)]) {
    for (final text in const [1.0, 2.0]) {
      testWidgets(
        'the Now tile shows each filter count whole at the end of its bar at ${size.width.toInt()}x${size.height.toInt()} at ${text}x text (#1915)',
        (tester) async {
          expect(hasFonts, isTrue, reason: 'Roboto not found under $_fonts');
          await _pump(tester, text: text, size: size);
          for (final count in ['20/20', '10/10', '11/40', '0/40']) {
            final label = find.descendant(
              of: _tile,
              matching: find.text(count),
            );
            expect(label, findsOneWidget);
            expect(
              tester.renderObject<RenderParagraph>(label).didExceedMaxLines,
              isFalse,
              reason: '$count is cut',
            );
            // At the end of its bar, as the Monitor mock has it.
            final bar = tester.getRect(
              find.descendant(
                of: find
                    .ancestor(of: label, matching: find.byType(Column))
                    .first,
                matching: find.byType(LinearProgressIndicator),
              ),
            );
            final shown = tester.getRect(label);
            expect(shown.right, moreOrLessEquals(bar.right, epsilon: 0.01));
            expect(shown.left, greaterThanOrEqualTo(bar.left));
            if (text == 1) {
              expect(
                shown.width,
                moreOrLessEquals(tester.getSize(label).width, epsilon: 0.01),
                reason: '$count is shrunk',
              );
            }
          }
        },
        skip: skip,
      );
    }
  }

  testWidgets(
    'the Now tile shows each short filter name whole at 390x844 at 2x text (#1915)',
    (tester) async {
      expect(hasFonts, isTrue, reason: 'Roboto not found under $_fonts');
      await _pump(tester, text: 2);
      for (final filter in ['L', 'R', 'Ha']) {
        final name = find.descendant(of: _tile, matching: find.text(filter));
        expect(name, findsOneWidget);
        expect(
          tester.getSize(name).width,
          greaterThanOrEqualTo(
            tester
                .renderObject<RenderParagraph>(name)
                .getMaxIntrinsicWidth(double.infinity),
          ),
          reason: '$filter is cut',
        );
      }
    },
    skip: skip,
  );

  testWidgets(
    'the Now tile keeps each filter name clear of its count at 390x844 at 2x text (#1915)',
    (tester) async {
      expect(hasFonts, isTrue, reason: 'Roboto not found under $_fonts');
      await _pump(tester, text: 2);
      for (final (filter, count) in const [
        ('L', '20/20'),
        ('R', '10/10'),
        ('Ha', '11/40'),
        ('OIII', '0/40'),
      ]) {
        final name = find.descendant(of: _tile, matching: find.text(filter));
        final box = tester.getRect(name);
        // One glyph is never elided, so it paints past a box narrower than it.
        final end = tester
            .renderObject<RenderParagraph>(name)
            .getBoxesForSelection(
              TextSelection(baseOffset: 0, extentOffset: filter.length),
            )
            .fold(box.right, (end, g) => math.max(end, box.left + g.right));
        final label = find.descendant(of: _tile, matching: find.text(count));
        expect(
          end,
          lessThanOrEqualTo(tester.getRect(label).left),
          reason: '$filter runs into $count',
        );
      }
    },
    skip: skip,
  );

  // Seven filters, three-digit totals, long names (#1942).
  const names = ['Luminance', 'Red', 'Green', 'Blue', 'Ha', 'OIII', 'SII'];
  for (final size in const [Size(360, 780), Size(390, 844)]) {
    for (final text in const [1.0, 2.0]) {
      testWidgets(
        'the phone Now tile keeps seven filter names and counts readable at ${size.width.toInt()} pt at ${text}x text (#1942)',
        (tester) async {
          expect(hasFonts, isTrue, reason: 'Roboto not found under $_fonts');
          final base = _busy();
          final snap = MonitorSnapshot(
            asOf: base.asOf,
            verdict: base.verdict,
            now: MonitorNow(
              status: base.now.status,
              word: base.now.word,
              target: base.now.target,
              filter: 'Ha',
              subSeconds: 300,
              frameEndsAt: base.now.frameEndsAt,
              frameSeconds: 300,
              frame: 42,
              frames: 110,
              targetSecondsLeft: 20400,
              filters: [
                for (final (i, n) in names.indexed)
                  (
                    filter: n,
                    done: 100 + i,
                    total: 100 + 111 * i,
                    inHand: i == 4,
                  ),
              ],
            ),
            upNext: base.upNext,
            flip: base.flip,
            lastFrame: base.lastFrame,
            night: base.night,
          );
          await _pump(tester, text: text, size: size, snap: snap);
          if (text == 1.0) {
            // The busy tile with seven filters shows without scrolling at 1x.
            expect(
              tester
                  .state<ScrollableState>(
                    find.descendant(
                      of: _tile,
                      matching: find.byType(Scrollable),
                    ),
                  )
                  .position
                  .maxScrollExtent,
              0,
              reason: 'the Now tile scrolls with seven filters',
            );
          }
          for (final (i, n) in names.indexed) {
            final name = find.descendant(of: _tile, matching: find.text(n));
            expect(name, findsOneWidget);
            final para = tester.renderObject<RenderParagraph>(name);
            // At least one glyph shows (an ellipsis alone is nothing).
            expect(
              tester.getSize(name).width,
              greaterThanOrEqualTo(
                (TextPainter(
                  text: TextSpan(text: n[0], style: para.text.style),
                  textDirection: TextDirection.ltr,
                  textScaler: para.textScaler,
                )..layout()).width,
              ),
              reason: '$n shows no glyph',
            );
            final label = find.descendant(
              of: _tile,
              matching: find.text('${100 + i}/${100 + 111 * i}'),
            );
            expect(label, findsOneWidget);
            // A count is never scaled below its 14 px floor (DESIGN.md).
            expect(
              find.ancestor(of: label, matching: find.byType(FittedBox)),
              findsNothing,
              reason: '$n count is scaled',
            );
            expect(
              tester.renderObject<RenderParagraph>(label).didExceedMaxLines,
              isFalse,
            );
          }
        },
        skip: skip,
      );
    }
  }
  // Four filters keep the side-by-side wrap (#1942).
  testWidgets(
    'the phone Now tile keeps four long filter names and three-digit counts unscaled at 360 pt at 1x text (#1942)',
    (tester) async {
      expect(hasFonts, isTrue, reason: 'Roboto not found under $_fonts');
      final base = _busy();
      const names = ['Luminance', 'Red', 'Green', 'Blue'];
      final snap = MonitorSnapshot(
        asOf: base.asOf,
        verdict: base.verdict,
        now: MonitorNow(
          status: base.now.status,
          word: base.now.word,
          target: base.now.target,
          filter: 'Red',
          subSeconds: 300,
          frameEndsAt: base.now.frameEndsAt,
          frameSeconds: 300,
          frame: 42,
          frames: 110,
          targetSecondsLeft: 20400,
          filters: [
            for (final (i, n) in names.indexed)
              (filter: n, done: 100 + i, total: 766 + 111 * i, inHand: i == 1),
          ],
        ),
        upNext: base.upNext,
        flip: base.flip,
        lastFrame: base.lastFrame,
        night: base.night,
      );
      await _pump(tester, size: const Size(360, 780), snap: snap);
      for (final (i, n) in names.indexed) {
        final label = find.descendant(
          of: _tile,
          matching: find.text('${100 + i}/${766 + 111 * i}'),
        );
        expect(label, findsOneWidget, reason: n);
        expect(
          find.ancestor(of: label, matching: find.byType(FittedBox)),
          findsNothing,
          reason: '$n count is scaled',
        );
        expect(
          tester.renderObject<RenderParagraph>(label).didExceedMaxLines,
          isFalse,
        );
      }
    },
    skip: skip,
  );

  // A full rig fills Conditions most: all seven weather readings and the
  // whole Camera and focuser group, under a Safety strip, beside a full
  // Target; no other tile gives up room for it (#2014).
  for (final size in const [
    Size(1280, 800),
    Size(1133, 744),
    Size(1024, 768),
    Size(390, 844),
  ]) {
    for (final locale in const [Locale('en'), Locale('pt', 'BR')]) {
      testWidgets(
        'a full rig shows all of Conditions without scrolling at ${size.width.toInt()}x${size.height.toInt()} in $locale (#2014)',
        (tester) async {
          expect(hasFonts, isTrue, reason: 'Roboto not found under $_fonts');
          final base = _busy();
          final snap = MonitorSnapshot(
            asOf: base.asOf,
            verdict: base.verdict,
            now: base.now,
            upNext: base.upNext,
            target: MonitorTarget(
              name: 'M31',
              longName: 'Andromeda Galaxy',
              status: MonitorStatus.working,
              word: MonitorTargetWord.tracking,
              imagingTo: DateTime(2026, 11, 12, 3, 40),
              maxAltitudeDeg: 78,
              transit: DateTime(2026, 11, 11, 21, 40),
              moonPct: 62,
              curve: [
                for (var i = 0; i <= 48; i++)
                  20 + 58 * (1 - ((i - 18) / 30).abs()),
              ],
              spanStart: DateTime(2026, 11, 11, 22, 30),
              spanEnd: DateTime(2026, 11, 12, 3, 40),
            ),
            flip: base.flip,
            safety: MonitorSafety(
              status: MonitorStatus.normal,
              safe: true,
              since: _asOf.subtract(const Duration(hours: 2)),
            ),
            conditions: MonitorConditions(
              weather: const MonitorWeather(
                status: MonitorStatus.normal,
                windDeg: 300,
                gustKmh: 13,
                cells: [
                  (cell: MonitorWeatherCell.cloud, value: 6, overLimit: null),
                  (
                    cell: MonitorWeatherCell.skyQuality,
                    value: 21.32,
                    overLimit: null,
                  ),
                  (cell: MonitorWeatherCell.air, value: 11.4, overLimit: null),
                  (
                    cell: MonitorWeatherCell.dewMargin,
                    value: 6.8,
                    overLimit: null,
                  ),
                  (
                    cell: MonitorWeatherCell.humidity,
                    value: 64,
                    overLimit: null,
                  ),
                  (cell: MonitorWeatherCell.rain, value: 0, overLimit: null),
                  (cell: MonitorWeatherCell.wind, value: 8, overLimit: null),
                ],
              ),
              rig: MonitorRigTemps(
                sensorC: -10.2,
                sensorTargetC: -10,
                coolerPowerPct: 38,
                focuserC: 11.9,
                autofocusAt: _asOf.subtract(const Duration(minutes: 2)),
                tempSinceAutofocus: -0.8,
              ),
            ),
            lastFrame: base.lastFrame,
            night: MonitorNight(
              darkStart: DateTime(2026, 11, 11, 19, 10),
              darkEnd: DateTime(2026, 11, 12, 5),
            ),
          );
          await _pump(tester, size: size, snap: snap, locale: locale);
          // A phone's column builds as it scrolls, so each tile is checked in
          // its turn. The Safety strip does not scroll: an overflow there
          // fails the case.
          final column = find.byKey(const Key('simple-monitor-column'));
          for (final key in [
            'now',
            'target',
            'flip',
            'conditions',
            'hfr',
            'stars',
            'night',
          ]) {
            final tile = find.byKey(Key('simple-monitor-$key'));
            if (column.evaluate().isNotEmpty) {
              await tester.scrollUntilVisible(
                tile,
                200,
                scrollable: find
                    .descendant(of: column, matching: find.byType(Scrollable))
                    .first,
              );
            }
            final scroll = find
                .descendant(of: tile, matching: find.byType(Scrollable))
                .first;
            expect(
              tester.state<ScrollableState>(scroll).position.maxScrollExtent,
              0,
              reason: 'part of $key is hidden below its edge',
            );
            // The strings checked below are English.
            if (key != 'conditions' || locale.languageCode != 'en') continue;
            final box = tester.getRect(tile);
            for (final text in [
              'Sky quality',
              'Wind · NW · gusts 13',
              'Camera and focuser',
              'Sensor · -10',
              'Cooler power',
              'Focuser',
              'Autofocus 23:50',
            ]) {
              final r = tester.getRect(
                find.descendant(of: tile, matching: find.text(text)),
              );
              expect(
                box.top <= r.top && r.bottom <= box.bottom,
                isTrue,
                reason: '$text: $r outside $box',
              );
            }
          }
        },
        skip: skip,
      );
    }
  }
}
