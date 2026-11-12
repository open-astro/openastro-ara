import 'dart:io';

import 'package:flutter/cupertino.dart' show CupertinoPicker;
import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart' show RenderParagraph;
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/state/server_state.dart' show discoveredServersProvider;
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/screens/simple/simple_guide_panel.dart' show SimpleGuidePanel;
import 'package:openastroara/screens/simple/simple_calibration.dart' show SimpleCalibrationPanel, SimpleCalibrationShutter;
import 'package:openastroara/models/calibration_status.dart';
import 'package:openastroara/state/guider/guider_calibration_state.dart';
import 'package:openastroara/screens/simple/simple_polar_panel.dart';
import 'package:openastroara/screens/simple/simple_focus_panel.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/models/sequence/imaging_run_body.dart';
import 'package:openastroara/screens/simple/simple_plan_panel.dart';
import 'package:openastroara/screens/simple/simple_rig_storage.dart' show SimpleHostedText;
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
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/screens/simple/simple_preview_panel.dart' show SimpleStatusPill;
import 'package:openastroara/state/equipment/focuser_state.dart';
import 'package:openastroara/models/filter_offset_calibration.dart';
import 'package:openastroara/models/filter_wheel_status.dart';
import 'package:openastroara/state/equipment/filter_wheel_state.dart';
import 'package:openastroara/state/focus/filter_offset_calibration_state.dart';
import 'package:openastroara/state/library/live_library_state.dart';
import 'package:openastroara/widgets/inline_choices.dart';
import 'package:openastroara/widgets/support/bug_report_card.dart';
import 'package:openastroara/state/rig_setup_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart' show serverLinkUpProvider;

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

/// A guider guiding, so the bottom row's guiding bar shows.
class _GuideBuilt extends GuiderCalibrationNotifier {
  @override
  Future<CalibrationStatusResponse?> build() async => const CalibrationStatusResponse(
      connected: true,
      status: CalibrationStatus(darkLibraryExists: true, defectMapExists: true, darkLibraryLoaded: true, defectMapLoaded: true));
}

class _Guiding extends GuiderStatusNotifier {
  @override
  Future<GuiderStatus?> build() async => GuiderStatus.fromJson(const {
        'device_id': 'g', 'name': 'PHD2', 'state': 'connected',
        'runtime': {'state': 'guiding', 'rms_total_arcsec': 0.62},
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

/// A polar routine mid-adjustment: the bullseye and both turn tiles show.
class _Adjusting extends PolarAlignLiveNotifier {
  @override
  PolarAlignLive build() => const PolarAlignLive(
        phase: PolarAlignStates.adjusting,
        altErrorArcmin: 12.4,
        azErrorArcmin: -8.1,
        totalErrorArcmin: 14.8,
      );
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
  // In each language Simple Mode ships: Portuguese runs longer than English.
  for (final locale in const [Locale('en'), Locale('pt', 'BR')]) {
  final lang = locale.toLanguageTag();
  final l = lookupAppLocalizations(locale);
  for (final (scale, size) in cases) {
    for (final open in ['Preview settings', 'Plan', 'Stop armed', 'Modes', 'Settings', 'Rig', 'Camera', 'Settings Focuser', 'Search', 'Wheels lat', 'Wheels lng', 'Wheels aperture', 'Wheels focal', 'Settings Guider', 'Guiding', 'Focus Auto', 'Focus Manual', 'Focus Guide', 'Focus Offsets', 'Focus Offsets results', 'Polar adjusting', 'Guide', 'Preflight', 'Calibration flats', 'Calibration darks', 'Calibration guide']) {
      testWidgets('$lang text ${scale}x at ${size.width.toInt()}x${size.height.toInt()}, $open: nothing overflows',
          skip: !hasFonts, (tester) async {
        tester.view.physicalSize = size;
        tester.view.devicePixelRatio = 1;
        addTearDown(tester.view.reset);
        final c = ProviderContainer(overrides: [
          simpleModePrefsProvider.overrideWithValue(_MemPrefs()),
          selectedTabIndexProvider.overrideWith(_StartOnLive.new),
          // The Connect to your rig card (no rig link here) scans: no network in a test.
          discoveredServersProvider.overrideWith((ref) => const Stream<AraServer>.empty()),
          // Plan with real content: Tonight's best (one picked) and a queue.
          if (open == 'Camera') cameraStatusProvider.overrideWith(_Camera.new),
          if (open == 'Settings Focuser') focuserProvider.overrideWith(_Focuser.new),
          if (open == 'Search') liveLibrarySessionsProvider.overrideWith(_Sessions.new),
          if (open == 'Settings Guider' || open == 'Guiding' || open == 'Focus Guide' || open == 'Guide' ||
              open == 'Calibration guide')
            guiderStatusProvider.overrideWith(_Guiding.new),
          // Both guide sets on file, so their Delete pair and its question show.
          if (open == 'Calibration guide') guiderCalibrationProvider.overrideWith(_GuideBuilt.new),
          if (open.startsWith('Focus')) focuserProvider.overrideWith(_Focuser.new),
          // #1675: the Offsets picker over a seven-filter wheel, and its results table.
          if (open.startsWith('Focus Offsets')) filterWheelProvider.overrideWith(_Wheel.new),
          if (open == 'Focus Offsets results') filterOffsetCalibrationProvider.overrideWith(_OffsetResults.new),
          if (open == 'Polar adjusting') polarAlignLiveProvider.overrideWith(_Adjusting.new),
          if (open == 'Plan') ...[
            tonightSkyProvider.overrideWith(
                (ref) async => [_o('M31', 62), _o('North America Nebula', 71), _o('M33', 48), _o('M45', 30)]),
            simpleRunProvider.overrideWith(_Queue.new),
          ],
        ]);
        addTearDown(c.dispose);
        await c.read(simpleModeProvider.future);
        if (open == 'Settings Focuser') await c.read(focuserProvider.future);
        if (open == 'Settings Guider' || open == 'Guiding' || open == 'Focus Guide' || open == 'Guide' ||
            open == 'Calibration guide') {
          await c.read(guiderStatusProvider.future);
        }
        if (open == 'Calibration guide') await c.read(guiderCalibrationProvider.future);
        if (open.startsWith('Focus')) await c.read(focuserProvider.future);
        if (open.startsWith('Focus Offsets')) await c.read(filterWheelProvider.future);
        await tester.pumpWidget(UncontrolledProviderScope(
          container: c,
          child: MaterialApp(
            locale: locale,
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
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
          await tester.tap(find.text(l.shellModePlan).first, warnIfMissed: false);
          await tester.pump();
          c.read(planetariumSelectionProvider.notifier).set(const PlanetariumSelection(
              name: 'M31', kind: 'Galaxy', raHours: 0.73, decDeg: 41.4, raJ2000Hours: 0.71, decJ2000Deg: 41.27,
              altDeg: 62, maxAltDeg: 74));
          await tester.pump();
          // (The one-third width has no sheet layout yet: phone layout, ALP-4865.)
          if (size.width >= 768) {
            expect(find.textContaining(l.planTonightsBest), findsOneWidget, reason: 'the sheet with its suggestions');
          }
        } else if (open == 'Modes') {
          await tester.tap(find.byKey(const Key('simple-mode')), warnIfMissed: false);
          await tester.pump();
          // Every mode name whole, on the rail and in the grid: no "Previ…".
          for (final name in [l.shellModePreflight, l.shellModePreview, l.shellModeFocus, l.shellModePolarAlign, l.shellModePlan]) {
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
          expect(find.textContaining(l.estopCompactPressAgain.split('\n').last), findsOneWidget);
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
          // Its page in Settings: the coarse and fine knobs.
          await tester.tap(find.byTooltip(l.shellSettings));
          await tester.pump();
          await tester.tap(find.byKey(const Key('simple-menu-nav-Focuser')));
          await tester.pump();
          expect(find.byKey(const Key('simple-focuser-coarse')), findsOneWidget);
        } else if (open.startsWith('Wheels')) {
          // Settings › Rig with a wheel row open: every wheel inside the
          // sheet, every digit whole in its slot.
          await tester.tap(find.byTooltip(l.shellSettings));
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
        } else if (open == 'Settings Guider') {
          // Its page in Settings: state, RMS, gain and the dither steppers.
          await tester.tap(find.byTooltip(l.shellSettings));
          await tester.pump();
          await tester.tap(find.byKey(const Key('simple-menu-nav-Guider')));
          await tester.pump();
        } else if (open == 'Guiding') {
          // The bottom row's guiding bar: "Guiding" over the RMS, RA over Dec.
          expect(find.byKey(const Key('simple-guide-trace')), findsOneWidget);
        } else if (open == 'Search') {
          await tester.tap(find.byKey(const Key('simple-search-button')));
          await tester.pump();
          await tester.enterText(find.byKey(const Key('simple-search-field')), 'Flying');
          await tester.pump();
          await tester.pump();
          expect(find.byKey(const Key('simple-search-image-0')), findsOneWidget);
        } else if (open == 'Preflight') {
          // The step strip once it runs.
          void whole(Finder of) {
            for (final p in tester.renderObjectList<RenderParagraph>(find.descendant(of: of, matching: find.byType(RichText)))) {
              expect(p.didExceedMaxLines, isFalse, reason: '"${p.text.toPlainText()}" is cut off');
            }
          }
          // Started from the modes (the Start banner went, user 2026-11-04).
          await tester.tap(find.byKey(const Key('simple-mode')), warnIfMissed: false);
          await tester.pump();
          await tester.tap(find.byKey(const Key('simple-mode-preflight')), warnIfMissed: false);
          await tester.pump();
          final strip = find.byKey(const Key('preflight-strip'));
          if (size.width >= 768) expect(strip, findsOneWidget);
          whole(strip);
        } else if (open.startsWith('Focus') || open == 'Polar adjusting' || open == 'Guide') {
          // A mode panel: picking the mode from the grid opens it.
          final focus = open.startsWith('Focus');
          final guide = open == 'Guide';
          await tester.tap(find.byKey(const Key('simple-mode')), warnIfMissed: false);
          await tester.pump();
          await tester.tap(
              guide
                  // pt-BR's guiding bar reads Guiagem too: the tile by its key.
                  ? find.byKey(const Key('simple-mode-guide'))
                  : find.text(focus ? l.shellModeFocus : l.shellModePolarAlign).first,
              warnIfMissed: false);
          await tester.pump();
          final panel = find.byType(focus ? SimpleFocusPanel : guide ? SimpleGuidePanel : SimplePolarPanel);
          // (The one-third width has no panel layout yet: phone layout, ALP-4865.)
          if (size.width >= 768) expect(panel, findsOneWidget);
          if (size.width < 768) {
            // no panel to switch in
          } else if (open == 'Focus Manual') {
            await tester.tap(find.byKey(const Key('simple-focus-method-manual')), warnIfMissed: false);
            await tester.pump();
          } else if (open == 'Focus Guide') {
            await tester.tap(find.byKey(const Key('simple-focus-scope-guide')), warnIfMissed: false);
            await tester.pump();
          } else if (open.startsWith('Focus Offsets')) {
            await tester.tap(find.byKey(const Key('simple-focus-method-offsets')), warnIfMissed: false);
            await tester.pump();
            expect(
                find.byKey(Key(open == 'Focus Offsets' ? 'simple-offsets-pick-0' : 'simple-offsets-table')),
                findsOneWidget);
          }
          if (!focus && !guide && size.width >= 768) expect(find.byKey(const Key('simple-polar-bullseye')), findsOneWidget);
          // Every label whole: "Principal", "Manual", "Contínuo" were cut at 1.3x-2x.
          for (final p in tester.renderObjectList<RenderParagraph>(find.descendant(of: panel, matching: find.byType(RichText)))) {
            expect(p.didExceedMaxLines, isFalse, reason: '"${p.text.toPlainText()}" is cut off');
          }
        } else if (open.startsWith('Calibration')) {
          // Each tab, the shutter's caption under it, and the guide Delete's question.
          await tester.tap(find.byKey(const Key('simple-mode')), warnIfMissed: false);
          await tester.pump();
          await tester.tap(find.byKey(const Key('simple-mode-calibration')), warnIfMissed: false);
          await tester.pump();
          final panel = find.byType(SimpleCalibrationPanel);
          // (The one-third width has no panel layout yet: phone layout, ALP-4865.)
          if (size.width >= 768) {
            expect(panel, findsOneWidget);
            final tab = open.split(' ').last;
            await tester.tap(find.byKey(Key('simple-calibration-tab-$tab')), warnIfMissed: false);
            await tester.pump();
            if (tab == 'guide') {
              final delete = find.byKey(const Key('simple-calibration-guide-delete-darks'));
              await tester.ensureVisible(delete);
              await tester.tap(delete, warnIfMissed: false);
              await tester.pump();
              expect(find.byKey(const Key('simple-calibration-guide-delete-darks-ask-confirm')), findsOneWidget);
            }
            for (final of in [panel, find.byType(SimpleCalibrationShutter)]) {
              for (final p in tester.renderObjectList<RenderParagraph>(find.descendant(of: of, matching: find.byType(RichText)))) {
                expect(p.didExceedMaxLines, isFalse, reason: '"${p.text.toPlainText()}" is cut off');
              }
            }
          }
        } else if (open == 'Settings' || open == 'Rig') {
          await tester.tap(find.byTooltip(l.shellSettings));
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

  // #1560 audit: the queue's park line on the last 72 px plan square renders at
  // its full 11.5 px, like the square's other secondary lines, not shrunk to fit
  // (measured with real Roboto; it read 8.6 px in English and 6.7 px in pt-BR).
  for (final locale in const [Locale('en'), Locale('pt', 'BR')]) {
    testWidgets('${locale.toLanguageTag()} the plan square\'s park line is full size', skip: !hasFonts, (tester) async {
      tester.view.physicalSize = const Size(1024, 768);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(ProviderScope(
        overrides: [simpleRunProvider.overrideWith(_Queue.new)],
        child: MaterialApp(
          locale: locale,
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          theme: ThemeData(fontFamily: 'Roboto'),
          home: Scaffold(body: Center(child: SimpleRunTracker(onTap: () {}))),
        ),
      ));
      await tester.pumpAndSettle();
      final line = find.text(lookupAppLocalizations(locale).planSquareParks);
      final text = tester.widget<Text>(line);
      final scale = tester.getRect(line).height / tester.renderObject<RenderBox>(line).size.height;
      expect(text.style!.fontSize! * scale, greaterThanOrEqualTo(11.5 * 0.99),
          reason: '"${text.data}" renders at ${(text.style!.fontSize! * scale).toStringAsFixed(1)} px');
    });
  }

  // The longest Covers value keeps the whole row once the card is stacked.
  for (final (scale, width) in const [(1.0, 320.0), (1.5, 420.0)]) {
    testWidgets('pt-BR text ${scale}x at ${width.toInt()} px: the bug report options are not cut off', skip: !hasFonts, (tester) async {
      tester.view.physicalSize = Size(width, 800);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(ProviderScope(
        overrides: [simpleModeActiveProvider.overrideWithValue(true)],
        child: MaterialApp(
          locale: const Locale('pt', 'BR'),
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          theme: simpleTheme(ThemeData(fontFamily: 'Roboto')),
          builder: (context, child) => MediaQuery(
            data: MediaQuery.of(context).copyWith(textScaler: TextScaler.linear(scale)),
            child: child!,
          ),
          home: const Scaffold(body: SingleChildScrollView(child: SimpleHostedText(child: BugReportCard()))),
        ),
      ));
      await tester.pumpAndSettle();
      expect(tester.takeException(), isNull);
      final value = lookupAppLocalizations(const Locale('pt', 'BR')).bugRangeNight;
      final line = tester.renderObject<RenderParagraph>(find.descendant(of: find.text(value).first, matching: find.byType(RichText)));
      expect(line.didExceedMaxLines, isFalse);
    });
  }

  // The bug report's review, the longest inline confirm, inside its card's 20 px.
  for (final locale in const [Locale('en'), Locale('pt', 'BR')]) {
    for (final (scale, width) in const [(1.0, 320.0), (2.0, 320.0), (2.0, 420.0)]) {
      final lang = locale.toLanguageTag();
      testWidgets('$lang text ${scale}x at ${width.toInt()} px: the inline confirm keeps its buttons on screen', skip: !hasFonts, (tester) async {
        tester.view.physicalSize = Size(width, 800);
        tester.view.devicePixelRatio = 1;
        addTearDown(tester.view.reset);
        final l = lookupAppLocalizations(locale);
        await tester.pumpWidget(MaterialApp(
          locale: locale,
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          theme: simpleTheme(ThemeData(fontFamily: 'Roboto')),
          builder: (context, child) => MediaQuery(
            data: MediaQuery.of(context).copyWith(textScaler: TextScaler.linear(scale)),
            child: child!,
          ),
          home: Scaffold(
            body: SingleChildScrollView(
              padding: const EdgeInsets.all(20),
              child: InlineConfirm(
                id: 'ask',
                title: l.bugReviewTitle,
                body: Text(l.bugReviewTrust),
                confirmLabel: l.bugReviewDownload,
                cancelLabel: l.planCancel,
                onConfirm: () {},
                onCancel: () {},
              ),
            ),
          ),
        ));
        await tester.pumpAndSettle();
        expect(tester.takeException(), isNull);
      });
    }
  }

  // #1596: the skipped-run pill is one line in a 300 px pill; its cause must
  // survive the ellipsis in every language (measured with real Roboto).
  for (final locale in const [Locale('en'), Locale('pt', 'BR')]) {
    testWidgets('${locale.toLanguageTag()} the skipped-run status pill fits on its line', skip: !hasFonts, (tester) async {
      tester.view.physicalSize = const Size(1024, 768);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      final c = ProviderContainer(overrides: [
        simpleModePrefsProvider.overrideWithValue(_MemPrefs()),
        selectedTabIndexProvider.overrideWith(_StartOnLive.new),
        discoveredServersProvider.overrideWith((ref) => const Stream<AraServer>.empty()),
        sequenceRunStateProvider.overrideWith(() => _SkippedRun()),
      ]);
      addTearDown(c.dispose);
      await c.read(simpleModeProvider.future);
      await tester.pumpWidget(UncontrolledProviderScope(
        container: c,
        child: MaterialApp(
          locale: locale,
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          theme: ThemeData(fontFamily: 'Roboto'),
          home: const SimpleShell(planetarium: SizedBox()),
        ),
      ));
      await tester.pump();
      await tester.pump();
      final skipped = lookupAppLocalizations(locale).previewPlanSkipped;
      final lines = tester
          .renderObjectList<RenderParagraph>(find.descendant(of: find.byType(SimpleStatusPill), matching: find.byType(RichText)))
          .where((p) => p.text.toPlainText().contains(skipped))
          .toList();
      expect(lines, isNotEmpty, reason: 'the pill shows the skip reason');
      for (final p in lines) {
        expect(p.didExceedMaxLines, isFalse, reason: '"${p.text.toPlainText()}" is cut off');
      }
    });
  }

  // #1562: on a phone the Rig setup sheet puts each fix's button under its
  // words (beside them, pt-BR's "Luneta de guiagem" went one letter per line),
  // and Not now keeps its own width (measured with real Roboto).
  for (final locale in const [Locale('en'), Locale('pt', 'BR')]) {
    testWidgets('${locale.toLanguageTag()} at 390x844 the Rig setup sheet puts each button under its words, every label on one line',
        skip: !hasFonts, (tester) async {
      tester.view.physicalSize = const Size(390, 844);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      final c = ProviderContainer(overrides: [
        simpleModePrefsProvider.overrideWithValue(_MemPrefs()),
        selectedTabIndexProvider.overrideWith(_StartOnLive.new),
        discoveredServersProvider.overrideWith((ref) => const Stream<AraServer>.empty()),
        serverLinkUpProvider.overrideWithValue(true),
        // The site, the guide scope and its calibration missing (the Guider not connected).
        rigSetupFactsProvider.overrideWith(
            (ref) => const RigSetupFacts(latitudeDeg: 0, longitudeDeg: 0, opticsConfigured: true, hasGuider: true)),
      ]);
      addTearDown(c.dispose);
      await c.read(simpleModeProvider.future);
      c.read(rigSetupSheetProvider.notifier).set(true);
      await tester.pumpWidget(UncontrolledProviderScope(
        container: c,
        child: MaterialApp(
          locale: locale,
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          theme: ThemeData(fontFamily: 'Roboto'),
          home: const SimpleShell(planetarium: SizedBox()),
        ),
      ));
      await tester.pump();
      final l = lookupAppLocalizations(locale);
      final sheet = find.byKey(const Key('simple-rig-setup'));
      Finder inSheet(String text) => find.descendant(of: sheet, matching: find.text(text));
      final oneLine = tester.getSize(inSheet(l.rigSetupSite)).height;
      for (final (id, label, missing) in [
        ('site', l.rigSetupSite, l.rigSetupSiteMissing),
        ('guide-scope', l.rigSetupGuideScope, l.rigSetupGuideScopeMissing),
        ('calibration', l.rigSetupCalibration, l.rigSetupCalibrationConnect),
      ]) {
        expect(tester.getTopLeft(find.byKey(Key('simple-rig-setup-$id-action'))).dy,
            greaterThanOrEqualTo(tester.getBottomLeft(inSheet(missing)).dy), reason: 'the button under "$label"');
        expect(tester.getSize(inSheet(label)).height, oneLine, reason: '"$label" on one line, no word broken');
      }
      expect(tester.getSize(find.byKey(const Key('simple-rig-setup-not-now'))).width,
          lessThan(tester.getSize(sheet).width / 2), reason: 'Not now keeps its own width: the fixes are the main actions');
      expect(tester.takeException(), isNull);
    });
  }
}

class _SkippedRun extends SequenceRunStateNotifier {
  @override
  Future<SequenceRunStateInfo?> build() async => const SequenceRunStateInfo(
      sequenceId: 'seq-1', state: SequenceRunState.completed,
      reasonCode: 'target_skipped', reasonText: 'Skipped, nothing was imaged: M31 did not run');
}

/// A seven-filter wheel, for the Offsets picker (#1675).
class _Wheel extends FilterWheelNotifier {
  @override
  Future<FilterWheelStatus?> build() async => FilterWheelStatus.fromJson({
        'device_id': 'w', 'name': 'ZWO EFW 7', 'state': 'connected',
        'slots': [
          for (final (i, n) in ['Luminance', 'Red', 'Green', 'Blue', 'H-alpha', 'OIII', 'SII'].indexed)
            {'position': i, 'name': n, 'focus_offset': 0},
        ],
        'runtime': {'state': 'idle', 'current_slot': 0},
      });
}

/// A finished offset calibration: a weak fit, a failure and drift (#1675).
class _OffsetResults extends FilterOffsetCalibrationNotifier {
  @override
  FilterOffsetLive build() => FilterOffsetLive(
      calibration: FilterOffsetCalibration.fromJson(const {
        'state': 'complete', 'reference_position': 0, 'reference_name': 'Luminance',
        'reference_first_position': 31200, 'reference_second_position': 31240, 'cfz_steps': 22,
        'drift_checked': true, 'drift_warning': true, 'run_index': 7, 'run_count': 7,
        'filters': [
          {'position': 0, 'name': 'Luminance', 'status': 'measured', 'offset': 0, 'r_squared': 0.98, 'previous_offset': 0},
          {'position': 1, 'name': 'Red', 'status': 'measured', 'offset': -12, 'r_squared': 0.62, 'previous_offset': -10},
          {'position': 2, 'name': 'Green', 'status': 'failed', 'previous_offset': 5,
            'failure_reason': 'No stars were found in the autofocus frame.'},
          {'position': 3, 'name': 'Blue', 'status': 'measured', 'offset': 18, 'r_squared': 0.95, 'previous_offset': 20},
          {'position': 4, 'name': 'H-alpha', 'status': 'measured', 'offset': 41, 'r_squared': 0.93, 'previous_offset': 0},
          {'position': 5, 'name': 'OIII', 'status': 'measured', 'offset': 42, 'r_squared': 0.97, 'previous_offset': 30},
          {'position': 6, 'name': 'SII', 'status': 'skipped', 'previous_offset': 0},
        ],
      }));
  @override
  Future<void> refresh() async {}
}
