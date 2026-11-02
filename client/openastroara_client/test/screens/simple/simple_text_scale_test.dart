import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart' show RenderParagraph;
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/imaging_run_body.dart';
import 'package:openastroara/screens/simple/simple_plan_panel.dart';
import 'package:openastroara/screens/simple/simple_shell.dart';
import 'package:openastroara/services/tonight_sky_api.dart';
import 'package:openastroara/state/sky_atlas/planetarium_selection_state.dart';
import 'package:openastroara/state/sky_atlas/tonight_sky_state.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/state/settings/settings_nav.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/widgets/emergency_stop_button.dart';

class _MemPrefs extends SimpleModePrefsService {
  bool value = true;
  @override
  Future<bool> load() async => value;
  @override
  Future<void> save(bool enabled) async => value = enabled;
}

class _StartOnLive extends SelectedTabIndexNotifier {
  @override
  int build() => kLiveTabIndex;
}

// The test font draws every glyph as a square, far wider than a real face,
// so fit is measured with the Roboto the Flutter SDK ships.
final _fonts = '${Platform.environment['FLUTTER_ROOT']}/bin/cache/artifacts/material_fonts';

Future<void> _loadFonts() async {
  ByteData bytes(String f) => ByteData.view(Uint8List.fromList(File('$_fonts/$f').readAsBytesSync()).buffer);
  final roboto = FontLoader('Roboto');
  for (final f in ['Roboto-Regular.ttf', 'Roboto-Medium.ttf', 'Roboto-Bold.ttf']) {
    roboto.addFont(Future.value(bytes(f)));
  }
  await roboto.load();
  await (FontLoader('MaterialIcons')..addFont(Future.value(bytes('MaterialIcons-Regular.otf')))).load();
}

/// A queue of two, so the plan squares draw.
class _Queue extends SimpleRunNotifier {
  @override
  SimpleRun build() => const SimpleRun(sequenceId: 'seq-1', targets: [
        SimpleTarget(name: 'M 31', steps: [FilterPlanStep(filterName: 'L', exposureSeconds: 120, frameCount: 30)]),
        SimpleTarget(name: 'NGC 7000', steps: [FilterPlanStep(filterName: 'Ha', exposureSeconds: 300, frameCount: 12)]),
      ]);
}

TonightSkyObject _o(String name, double alt) => TonightSkyObject(
    id: name, name: name, type: 'Galaxy', magnitude: 5, raDeg: 10, decDeg: 40, altitudeDeg: alt, maxAltitudeDeg: 70);

void main() {
  final hasFonts = File('$_fonts/Roboto-Regular.ttf').existsSync();
  setUpAll(() async {
    if (hasFonts) await _loadFonts();
  });

  // iPad landscape and portrait at the user's text sizes up to 2x, and a
  // one-third Split View (or a phone) at the default size.
  final cases = <(double, Size)>[
    for (final s in [1.0, 1.3, 1.6, 2.0]) ...[(s, const Size(1024, 768)), (s, const Size(768, 1024))],
    (1.0, const Size(320, 768)),
  ];
  for (final (scale, size) in cases) {
    for (final open in ['Preview settings', 'Plan', 'Stop armed', 'Modes']) {
      testWidgets('text ${scale}x at ${size.width.toInt()}x${size.height.toInt()}, $open: nothing overflows',
          skip: !hasFonts, (tester) async {
        tester.view.physicalSize = size;
        tester.view.devicePixelRatio = 1;
        addTearDown(tester.view.reset);
        final c = ProviderContainer(overrides: [
          simpleModePrefsProvider.overrideWithValue(_MemPrefs()),
          selectedTabIndexProvider.overrideWith(_StartOnLive.new),
          // Plan with real content: Tonight's best (one picked) and a queue.
          if (open == 'Plan') ...[
            tonightSkyProvider.overrideWith(
                (ref) async => [_o('M31', 62), _o('North America Nebula', 71), _o('M33', 48), _o('M45', 30)]),
            simpleRunProvider.overrideWith(_Queue.new),
          ],
        ]);
        addTearDown(c.dispose);
        await c.read(simpleModeProvider.future);
        await tester.pumpWidget(UncontrolledProviderScope(
          container: c,
          child: MaterialApp(
            theme: ThemeData(fontFamily: 'Roboto'),
            builder: (context, child) => MediaQuery(
              data: MediaQuery.of(context).copyWith(textScaler: TextScaler.linear(scale)),
              child: child!,
            ),
            home: const SimpleShell(planetarium: SizedBox()),
          ),
        ));
        await tester.pump();
        if (open == 'Plan') {
          await tester.tap(find.byKey(const Key('simple-mode')), warnIfMissed: false);
          await tester.pump();
          await tester.tap(find.text('Plan').first, warnIfMissed: false);
          await tester.pump();
          c.read(planetariumSelectionProvider.notifier).set(const PlanetariumSelection(
              name: 'M31', kind: 'Galaxy', raHours: 0.73, decDeg: 41.4, raJ2000Hours: 0.71, decJ2000Deg: 41.27,
              altDeg: 62, maxAltDeg: 74));
          await tester.pump();
          // Picking Plan opens nothing; the chip opens the sheet.
          await tester.tap(find.byKey(const Key('simple-settings')), warnIfMissed: false);
          await tester.pump();
          // (The one-third width has no sheet layout yet: phone layout, ALP-4865.)
          if (size.width >= 768) {
            expect(find.textContaining("TONIGHT'S BEST"), findsOneWidget, reason: 'the sheet with its suggestions');
          }
        } else if (open == 'Modes') {
          await tester.tap(find.byKey(const Key('simple-mode')), warnIfMissed: false);
          await tester.pump();
          // Every mode name whole, on the rail and in the grid: no "Previ…".
          for (final name in ['Autopilot', 'Preview', 'Focus', 'Polar align', 'Plan']) {
            for (final p in tester.renderObjectList<RenderParagraph>(find.text(name))) {
              expect(p.didExceedMaxLines, isFalse, reason: '"$name" is cut off');
            }
          }
        } else if (open == 'Stop armed') {
          // A keyboard press arms it: PRESS AGAIN, the longest label it shows.
          tester.widget<FilledButton>(find.descendant(
                  of: find.byType(EmergencyStopButton), matching: find.byType(FilledButton)))
              .onPressed!();
          await tester.pump();
          expect(find.textContaining('AGAIN'), findsOneWidget);
          // A clipped or mid-word-broken label throws nothing, so check the lines.
          for (final p in tester.renderObjectList<RenderParagraph>(
              find.descendant(of: find.byType(EmergencyStopButton), matching: find.byType(RichText)))) {
            expect(p.didExceedMaxLines, isFalse, reason: '"${p.text.toPlainText()}" is cut off');
          }
          await tester.pump(const Duration(seconds: 4)); // the press-again window lapses
        } else {
          await tester.tap(find.byKey(const Key('simple-settings')), warnIfMissed: false);
        }
        await tester.pump();
        // An overflow fails the test on its own; this names the case.
        expect(tester.takeException(), isNull);
      });
    }
  }
}
