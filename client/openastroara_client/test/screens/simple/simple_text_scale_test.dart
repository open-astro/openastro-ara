import 'dart:io';

import 'package:flutter/cupertino.dart' show CupertinoPicker;
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
import 'package:openastroara/models/camera_status.dart';
import 'package:openastroara/state/equipment/camera_state.dart';
import 'package:openastroara/models/focuser_status.dart';
import 'package:openastroara/models/library/live_library.dart';
import 'package:openastroara/state/equipment/focuser_state.dart';
import 'package:openastroara/state/library/live_library_state.dart';

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

/// A connected, cooled camera, for its window.
class _Camera extends CameraStatusNotifier {
  @override
  Future<CameraStatus?> build() async => CameraStatus.fromJson({
        'name': 'ZWO ASI2600MM Pro',
        'state': 'connected',
        'capabilities': {'has_cooler': true, 'can_set_temperature': true},
        'runtime': {'ccd_temperature': -9.8, 'cooler_power_pct': 34, 'cooler_on': true, 'cooler_setpoint_c': -10},
      });
}

class _Focuser extends FocuserNotifier {
  @override
  Future<FocuserStatus?> build() async => FocuserStatus.fromJson({
        'device_id': 'f', 'name': 'ZWO EAF', 'state': 'connected',
        'capabilities': {'min_position': 0, 'max_position': 60000, 'absolute_focuser': true},
        'runtime': {'state': 'idle', 'position': 31250, 'temperature': 8.4},
      });
}

/// An imaged target with a long name, for Search's result rows.
class _Sessions extends LiveLibrarySessionsNotifier {
  @override
  Future<List<LibrarySession>?> build() async => [
        LibrarySession(
            id: 's1', targetName: 'Sh2-129 Flying Bat and Ou4 Giant Squid Nebula',
            sessionStartUtc: DateTime.utc(2026, 11, 2, 20), sessionEndUtc: null,
            totalFrames: 21, lightFrames: 21, calibrationFrames: 0, filtersUsed: const ['Ha']),
      ];
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
    for (final open in ['Preview settings', 'Plan', 'Stop armed', 'Modes', 'Settings', 'Rig', 'Camera', 'Settings Focuser', 'Search', 'Wheels lat', 'Wheels lng', 'Wheels aperture', 'Wheels focal']) {
      testWidgets('text ${scale}x at ${size.width.toInt()}x${size.height.toInt()}, $open: nothing overflows',
          skip: !hasFonts, (tester) async {
        tester.view.physicalSize = size;
        tester.view.devicePixelRatio = 1;
        addTearDown(tester.view.reset);
        final c = ProviderContainer(overrides: [
          simpleModePrefsProvider.overrideWithValue(_MemPrefs()),
          selectedTabIndexProvider.overrideWith(_StartOnLive.new),
          // Plan with real content: Tonight's best (one picked) and a queue.
          if (open == 'Camera') cameraStatusProvider.overrideWith(_Camera.new),
          if (open == 'Settings Focuser') focuserProvider.overrideWith(_Focuser.new),
          if (open == 'Search') liveLibrarySessionsProvider.overrideWith(_Sessions.new),
          if (open == 'Plan') ...[
            tonightSkyProvider.overrideWith(
                (ref) async => [_o('M31', 62), _o('North America Nebula', 71), _o('M33', 48), _o('M45', 30)]),
            simpleRunProvider.overrideWith(_Queue.new),
          ],
        ]);
        addTearDown(c.dispose);
        await c.read(simpleModeProvider.future);
        if (open == 'Settings Focuser') await c.read(focuserProvider.future);
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
        } else if (open == 'Camera') {
          // What the top bar's camera dot does.
          c.read(selectedSettingsPanelProvider.notifier).select('eq.camera');
          c.read(selectedTabIndexProvider.notifier).select(kOptionsTabIndex);
          await tester.pump();
          expect(find.byKey(const Key('simple-device-camera')), findsOneWidget);
        } else if (open == 'Settings Focuser') {
          // Its page in Settings: the dial and the step segments.
          await tester.tap(find.byTooltip('Settings'));
          await tester.pump();
          await tester.tap(find.byKey(const Key('simple-menu-nav-Focuser')));
          await tester.pump();
          expect(find.byKey(const Key('simple-focuser-knob')), findsOneWidget);
        } else if (open.startsWith('Wheels')) {
          // Settings › Rig with a wheel row open: every wheel inside the
          // sheet, every digit whole in its slot.
          await tester.tap(find.byTooltip('Settings'));
          await tester.pump();
          await tester.tap(find.byKey(const Key('simple-menu-nav-Rig')));
          await tester.pump();
          final id = open.split(' ').last;
          final row = find.byKey(Key('simple-rig-$id'));
          await tester.ensureVisible(row);
          await tester.pump();
          await tester.tap(row);
          await tester.pump();
          final wheel = find.byKey(Key('simple-rig-$id-wheel-0'));
          expect(wheel, findsOneWidget);
          await tester.ensureVisible(wheel);
          await tester.pump();
          for (final p in tester.renderObjectList<RenderParagraph>(
              find.descendant(of: find.byType(CupertinoPicker), matching: find.byType(RichText)))) {
            expect(p.textSize.height, lessThanOrEqualTo(p.size.height + 0.5),
                reason: '"${p.text.toPlainText()}" is clipped in its slot');
            expect(p.textSize.width, lessThanOrEqualTo(p.size.width + 0.5),
                reason: '"${p.text.toPlainText()}" is squeezed in its wheel');
          }
        } else if (open == 'Search') {
          await tester.tap(find.byKey(const Key('simple-search-button')));
          await tester.pump();
          await tester.enterText(find.byKey(const Key('simple-search-field')), 'Flying');
          await tester.pump();
          await tester.pump();
          expect(find.byKey(const Key('simple-search-image-0')), findsOneWidget);
        } else if (open == 'Settings' || open == 'Rig') {
          await tester.tap(find.byTooltip('Settings'));
          await tester.pump();
          if (open == 'Rig') {
            await tester.tap(find.byKey(const Key('simple-menu-nav-Rig')));
            await tester.pump();
          }
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
