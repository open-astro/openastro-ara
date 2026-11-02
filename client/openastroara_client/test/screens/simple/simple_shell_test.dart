import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart' show RenderParagraph;
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/imaging_run_body.dart';
import 'package:openastroara/screens/simple/simple_plan_panel.dart';
import 'package:openastroara/state/sky_atlas/planetarium_selection_state.dart';
import 'package:openastroara/main.dart' show isShell, shellFor;
import 'package:openastroara/models/frame_histogram.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/stats/server_internal_state.dart';
import 'package:openastroara/services/storage_space_api.dart';
import 'package:openastroara/state/stats/server_internal_state_state.dart';
import 'package:openastroara/screens/app_shell.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/screens/simple/simple_polar_panel.dart';
import 'package:openastroara/screens/simple/simple_preview_panel.dart';
import 'package:openastroara/screens/simple/simple_shell.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/state/guider/guide_step_state.dart';
import 'package:openastroara/state/imaging/capture_progress_state.dart';
import 'package:openastroara/state/imaging/exposure_state.dart';
import 'package:openastroara/state/imaging/last_frame_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/settings_nav.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/widgets/emergency_stop_button.dart';
import 'package:openastroara/widgets/imaging/histogram_strip.dart' show frameHistogramProvider;
import 'package:openastroara/widgets/night_hotkey.dart';

class _MemPrefs extends SimpleModePrefsService {
  bool value = true;
  @override
  Future<bool> load() async => value;
  @override
  Future<void> save(bool enabled) async => value = enabled;
}

/// Simple Mode is entered from the full UI's Live tab (the switch button
/// points it there), so start the tab state where the switch leaves it.
class _StartOnLive extends SelectedTabIndexNotifier {
  @override
  int build() => kLiveTabIndex;
}

class _Internal extends ServerInternalStateNotifier {
  _Internal(this.value);
  final ServerInternalState value;
  @override
  Future<ServerInternalState?> build() async => value;
  @override
  Future<void> refresh() async {}
}

class _Steps extends GuideStepNotifier {
  _Steps(this.steps);
  final List<GuideStep> steps;
  @override
  List<GuideStep> build() => steps;
}

final _someSteps = [
  for (var i = 0; i < 20; i++)
    GuideStep(at: DateTime(2026, 11, 1, 22, 0, i), raPx: (i % 5 - 2) * 0.3, decPx: (i % 3 - 1) * 0.2),
];

Future<ProviderContainer> _pump(WidgetTester tester, Size size,
    {List<GuideStep> steps = const [], AraServer? server, FrameHistogram? histogram,
    ServerInternalState? internal, StorageSpace? storage, SimpleRun? plan}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final container = ProviderContainer(overrides: [
    simpleModePrefsProvider.overrideWithValue(_MemPrefs()),
    selectedTabIndexProvider.overrideWith(_StartOnLive.new),
    guideStepsProvider.overrideWith(() => _Steps(steps)),
    if (server != null) activeServerProvider.overrideWithValue(server),
    if (histogram != null) frameHistogramProvider.overrideWith((ref, id) async => histogram),
    if (internal != null) serverInternalStateProvider.overrideWith(() => _Internal(internal)),
    if (storage != null) storageSpaceProvider.overrideWith((ref) async => storage),
    if (plan != null) simpleRunProvider.overrideWith(() => _Plan(plan)),
  ]);
  addTearDown(container.dispose);
  await container.read(simpleModeProvider.future);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: container,
    child: const MaterialApp(home: SimpleShell(planetarium: SizedBox(key: Key('sky')))),
  ));
  await tester.pump();
  return container;
}

class _Plan extends SimpleRunNotifier {
  _Plan(this.value);
  final SimpleRun value;
  @override
  SimpleRun build() => value;
}

Future<void> _frame(WidgetTester tester, ProviderContainer c) async {
  c.read(lastCapturedFrameIdProvider.notifier).set('frame-1');
  await tester.pump();
}

Rect _panelOf(WidgetTester tester, Finder f) =>
    tester.getRect(find.ancestor(of: f, matching: find.byType(SimplePanel)).first);

/// What the settings chip should read for the current exposure parameters.
String _summary(ProviderContainer c) {
  final p = c.read(exposureControllerProvider);
  return '${formatExposure(p.exposure)} · ${p.gain}';
}

void main() {
  for (final size in const [Size(1024, 768), Size(1024, 744), Size(1470, 854)]) {
    testWidgets('at $size: compact right rail (mode, settings, shutter), Stop below, left rail (Sky, Info, Library)',
        (tester) async {
      await _pump(tester, size, steps: _someSteps);
      expect(tester.takeException(), isNull, reason: 'no overflow');
      final right = _panelOf(tester, find.byKey(const Key('simple-shutter')));
      expect(right.width, kSimpleRailWidth);
      expect(right.right, size.width - kSimpleInset);
      for (final key in ['simple-mode', 'simple-settings']) {
        expect(_panelOf(tester, find.byKey(Key(key))), right, reason: '$key on the right rail');
      }
      final shutter = tester.getRect(find.byKey(const Key('simple-shutter')));
      final mode = tester.getRect(find.byKey(const Key('simple-mode')));
      final settings = tester.getRect(find.byKey(const Key('simple-settings')));
      expect(mode.bottom, lessThanOrEqualTo(shutter.top), reason: 'the mode on top of the shutter');
      expect(shutter.bottom, lessThanOrEqualTo(settings.top), reason: 'settings under it');
      final stop = tester.getRect(find.byType(EmergencyStopButton));
      expect(stop.size, const Size(60, 60));
      expect(stop.bottom, size.height - kSimpleInset, reason: 'Stop on its own at the bottom');
      expect(stop.center.dx, closeTo(right.center.dx, 0.5), reason: 'under the rail');
      expect(right.height, lessThan(320), reason: 'the rail is only as tall as its controls');
      expect(right.bottom, lessThan(stop.top));
      final left = _panelOf(tester, find.byKey(const Key('simple-sky')));
      expect(left.left, kSimpleInset);
      for (final key in ['simple-info', 'simple-library']) {
        expect(_panelOf(tester, find.byKey(Key(key))), left, reason: '$key on the left rail');
      }
    });
  }

  testWidgets('at rest only the essentials show: no mode list, no settings, no info', (tester) async {
    await _pump(tester, const Size(1024, 768));
    for (final hidden in ['Focus', 'Polar align', 'Plan', 'Exposure', 'Gain', 'Loop', 'HFR', 'Guiding']) {
      expect(find.text(hidden), findsNothing, reason: '$hidden is one tap away, not out');
    }
    expect(find.text('Tap the shutter to take a frame'), findsOneWidget);
    expect(find.byKey(const Key('sky')), findsOneWidget, reason: 'the sky until there is a frame');
  });

  testWidgets('the mode name opens the four modes in order; picking one closes them', (tester) async {
    final container = await _pump(tester, const Size(1024, 768));
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    final modes = ['Preview', 'Focus', 'Polar align', 'Plan'];
    // .first: the grid sits before the rail (whose label also reads Preview).
    final order = [for (final m in modes) tester.getTopLeft(find.text(m).first)];
    for (var i = 1; i < order.length; i++) {
      final before = order[i - 1], now = order[i];
      expect(now.dy > before.dy || (now.dy == before.dy && now.dx > before.dx), isTrue,
          reason: '${modes[i]} after ${modes[i - 1]}');
    }
    await tester.tap(find.text('Preview').first);
    await tester.pump();
    expect(find.text('Focus'), findsNothing, reason: 'closed');

    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.text('Plan'));
    await tester.pump();
    expect(container.read(simpleModeProvider).value, isTrue, reason: 'Plan stays in Simple Mode');
    expect(find.descendant(of: find.byKey(const Key('simple-mode')), matching: find.text('Plan')), findsOneWidget);
    // Plan opens on its sheet, centred; with nothing picked it asks for a target.
    expect(find.text('Search, or pick in the sky.'), findsOneWidget);
    // The chip reads the plan's total imaging time: 60 frames at the 5 s default.
    expect(find.descendant(of: find.byKey(const Key('simple-settings')), matching: find.text('5 min')), findsOneWidget);
    // A long night takes two lines in the 60 px chip, hours over minutes, with no icon (as P.A.'s).
    // (The test font's square glyphs are too wide to measure the real fit.)
    container.read(simpleRunPlanProvider.notifier)
      ..setExposure(0, 150)
      ..setFrames(0, 300);
    await tester.pump();
    final long = find.descendant(of: find.byKey(const Key('simple-settings')), matching: find.text('12 h\n30 min'));
    expect(tester.widget<Text>(long).maxLines, 2);
    expect(find.descendant(of: find.byKey(const Key('simple-settings')), matching: find.byType(Icon)), findsNothing);
  });

  testWidgets('Focus stays in Simple Mode: the rail reads Focus, its panel opens left of the rail; the chip is the Focus chip',
      (tester) async {
    final container = await _pump(tester, const Size(1024, 768));
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.text('Focus'));
    await tester.pump();
    expect(container.read(simpleModeProvider).value, isTrue, reason: 'no jump to the full UI');
    expect(find.descendant(of: find.byKey(const Key('simple-mode')), matching: find.text('Focus')), findsOneWidget);
    final rail = _panelOf(tester, find.byKey(const Key('simple-shutter')));
    final panel = _panelOf(tester, find.byKey(const Key('simple-autofocus')));
    expect(panel.right, rail.left - kSimpleInset, reason: 'left of the rail');
    expect(panel.top, rail.top, reason: 'level with its top');

    // The chip speaks for the mode: Focus's summary, not Preview's exposure.
    final chip = find.byKey(const Key('simple-settings'));
    expect(find.descendant(of: chip, matching: find.text(_summary(container))), findsNothing);
    expect(find.descendant(of: chip, matching: find.text('Manual')), findsOneWidget,
        reason: 'no focuser connected, so focus is by hand');
    await tester.tap(chip);
    await tester.pump();
    expect(find.byKey(const Key('simple-autofocus')), findsNothing, reason: 'the chip hides the Focus panel');
    expect(find.text('Exposure'), findsNothing, reason: "never Preview's settings in Focus");
    expect(_panelOf(tester, find.byKey(const Key('simple-shutter'))), rail, reason: 'the rail never moves');
    await tester.tap(chip);
    await tester.pump();
    expect(find.byKey(const Key('simple-autofocus')), findsOneWidget, reason: 'and shows it again');

    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.text('Preview').first);
    await tester.pump();
    expect(find.byKey(const Key('simple-autofocus')), findsNothing, reason: 'Preview has no panel');
    expect(find.descendant(of: chip, matching: find.text(_summary(container))), findsOneWidget);
  });

  testWidgets('Polar align stays in Simple Mode: the rail reads P.A., the chip its settings and toggles its panel', (tester) async {
    final container = await _pump(tester, const Size(1024, 768));
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.text('Polar align'));
    await tester.pump();
    expect(container.read(simpleModeProvider).value, isTrue, reason: 'no jump to the full UI');
    expect(find.descendant(of: find.byKey(const Key('simple-mode')), matching: find.text('P.A.')), findsOneWidget);
    final chip = find.byKey(const Key('simple-settings'));
    expect(find.descendant(of: chip, matching: find.text('1 s\nLoop')), findsOneWidget);
    final rail = _panelOf(tester, find.byKey(const Key('simple-shutter')));
    final panel = _panelOf(tester, find.byKey(const Key('simple-polar-exposure')));
    expect(panel.right, rail.left - kSimpleInset);
    expect(panel.top, rail.top);
    await tester.tap(chip);
    await tester.pump();
    expect(find.byKey(const Key('simple-polar-exposure')), findsNothing);
    await tester.tap(chip);
    await tester.pump();
    expect(find.byKey(const Key('simple-polar-exposure')), findsOneWidget);
  });

  testWidgets('the P.A. chip stacks exposure over Loop/Single, no icon, never cut', (tester) async {
    tester.platformDispatcher.textScaleFactorTestValue = 0.5;
    addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
    final container = await _pump(tester, const Size(1024, 768));
    await container.read(simplePolarSettingsProvider.future);
    await container.read(simplePolarSettingsProvider.notifier)
        .save(const PolarAlignSettings(exposureSeconds: 45, loopMode: PolarAlignLoopModes.single));
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.text('Polar align'));
    await tester.pump();
    final chip = find.byKey(const Key('simple-settings'));
    expect(find.descendant(of: chip, matching: find.byIcon(Icons.tune)), findsNothing);
    final p = tester.renderObject<RenderParagraph>(find.descendant(of: chip, matching: find.byType(RichText)));
    expect(p.text.toPlainText(), '45 s\nSingle');
    expect(p.didExceedMaxLines, isFalse, reason: 'cut');
  });

  testWidgets('an object picked in the sky offers Image this, which opens Run on it; Pick in Sky goes back',
      (tester) async {
    final container = await _pump(tester, const Size(1024, 768));
    await _frame(tester, container);
    expect(find.byKey(const Key('simple-sky-image-this')), findsNothing);
    await tester.tap(find.text('Sky'));
    await tester.pump();
    container.read(planetariumSelectionProvider.notifier).set(const PlanetariumSelection(
        name: 'M31', kind: 'Galaxy', raHours: 0.73, decDeg: 41.4, raJ2000Hours: 0.71, decJ2000Deg: 41.27,
        altDeg: 62.4, maxAltDeg: 74.2));
    await tester.pump();
    expect(find.text('Galaxy · 62° up · max 74°'), findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-sky-image-this')));
    await tester.pump();
    expect(find.descendant(of: find.byKey(const Key('simple-mode')), matching: find.text('Plan')), findsOneWidget);
    expect(find.byKey(const Key('sky')), findsNothing, reason: 'back on the frame');
    expect(find.text("Tonight's plan"), findsOneWidget);
    expect(find.byKey(const Key('simple-sky-image-this')), findsNothing, reason: 'the sheet, not the card');

    await tester.tap(find.byKey(const Key('simple-run-pick-in-sky')));
    await tester.pump();
    expect(find.text("Tonight's plan"), findsNothing);
    expect(find.byKey(const Key('sky')), findsOneWidget);
    expect(find.byKey(const Key('simple-sky-image-this')), findsOneWidget);
  });

  testWidgets('the settings chip opens Exposure and Gain side by side from lists, Filter and Loop/Single',
      (tester) async {
    final container = await _pump(tester, const Size(1024, 768));
    expect(find.text(_summary(container)), findsOneWidget, reason: 'the chip reads the settings');
    await tester.tap(find.byKey(const Key('simple-settings')));
    await tester.pump();
    expect(tester.takeException(), isNull);
    final exposure = tester.getRect(find.byKey(const Key('simple-exposure')));
    final gain = tester.getRect(find.byKey(const Key('simple-gain')));
    expect(exposure.top, gain.top, reason: 'side by side');
    expect(find.text('Filter'), findsOneWidget);
    expect(find.text('Loop'), findsOneWidget);

    await tester.tap(find.byKey(const Key('simple-exposure')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('10 s').last);
    await tester.pumpAndSettle();
    expect(container.read(exposureControllerProvider).exposure, const Duration(seconds: 10));
    await tester.tap(find.byKey(const Key('simple-gain')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('200').last);
    await tester.pumpAndSettle();
    expect(container.read(exposureControllerProvider).gain, 200);
    expect(find.text('10 s · 200'), findsOneWidget, reason: 'the chip follows');

    await tester.tapAt(const Offset(400, 400));
    await tester.pump();
    expect(find.text('Loop'), findsNothing, reason: 'a tap on the image closes it');
  });

  testWidgets('opening the modes or the settings never moves the rail', (tester) async {
    await _pump(tester, const Size(1024, 768));
    Rect rail() => _panelOf(tester, find.byKey(const Key('simple-shutter')));
    final before = rail();
    await tester.tap(find.byKey(const Key('simple-settings')));
    await tester.pump();
    expect(rail(), before, reason: 'settings open');
    final popover = tester.getRect(find.byType(SimpleSettingsPopover));
    expect(popover.top, before.top, reason: 'level with the rail top');
    expect(popover.right, before.left - kSimpleInset, reason: 'beside it');
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    expect(rail(), before, reason: 'modes open');
  });

  testWidgets('the plan square hugs the top-right corner, under the strip', (tester) async {
    await _pump(tester, const Size(1024, 768),
        plan: const SimpleRun(sequenceId: 'seq-1', targetName: 'M31', steps: [
          FilterPlanStep(filterName: 'L', exposureSeconds: 120, frameCount: 30),
        ]));
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.text('Plan').first);
    await tester.pump();
    final square = tester.getRect(find.byKey(const Key('simple-run-tracker')));
    expect(square.right, 1024 - kSimpleInset, reason: 'in the corner');
    expect(square.top, kSimpleInset * 2 + kSimpleBarHeight, reason: 'under the strip');
    expect(find.text('0 / 30'), findsOneWidget);
  });

  // Info and System: a tile per fact in the bottom-left corner, above the guiding and histogram row, growing
  // right; System's row sits above Info's.
  const tileLeft = kSimpleInset;
  const tileBottom = kSimpleInset * 2 + kSimpleBarHeight;

  testWidgets('Info shows the last frame facts as tiles in the bottom-left corner', (tester) async {
    final container = await _pump(tester, const Size(1024, 768));
    await _frame(tester, container);
    await tester.tap(find.byKey(const Key('simple-info')));
    await tester.pump();
    final row = tester.getRect(find.byType(SimpleInfoPanel));
    expect(row.left, tileLeft, reason: 'hugs the corner');
    expect(row.bottom, 768 - tileBottom, reason: 'just above the bottom row');
    expect(row.height, 56, reason: 'one row of tiles');
    for (final fact in ['HFR', 'Stars', 'Exposure', 'Gain · offset', 'Clipped']) {
      expect(find.descendant(of: find.byType(SimpleInfoPanel), matching: find.text(fact)), findsOneWidget, reason: fact);
    }
    // HFR nearest the corner.
    expect(tester.getRect(find.text('HFR')).center.dx, lessThan(tester.getRect(find.text('Clipped')).center.dx));
    await tester.tap(find.byKey(const Key('simple-info')));
    await tester.pump();
    expect(find.byType(SimpleInfoPanel), findsNothing);
  });

  testWidgets('System shows the server\'s figures as tiles in the bottom-left corner (above Info when it shows)', (tester) async {
    const size = Size(1470, 854);
    await _pump(tester, size,
        internal: const ServerInternalState(uptimeSeconds: 11520, cpuPercent: 23.4, workingSetBytes: 512 * 1048576),
        storage: const StorageSpace(saveDirectory: '/data', isFallback: false, freeBytes: 412 * 1073741824, totalBytes: 900 * 1073741824));
    expect(find.byKey(const Key('simple-system-line')), findsNothing);
    expect(_panelOf(tester, find.byKey(const Key('simple-system'))), _panelOf(tester, find.byKey(const Key('simple-sky'))),
        reason: 'on the left rail');
    await tester.tap(find.byKey(const Key('simple-system')));
    await tester.pump();
    await tester.pump(); // the storage read resolves
    final rect = tester.getRect(find.byKey(const Key('simple-system-line')));
    expect(rect.left, tileLeft);
    expect(rect.bottom, size.height - tileBottom, reason: 'alone, in the corner');
    final line = find.byKey(const Key('simple-system-line'));
    for (final t in ['Storage', '412 GB free', 'CPU', '23 %', 'Memory', '512 MB', 'Up', '3 h 12 min']) {
      expect(find.descendant(of: line, matching: find.text(t)), findsOneWidget, reason: t);
    }
    await tester.tap(find.byKey(const Key('simple-info')));
    await tester.pump();
    final info = tester.getRect(find.byType(SimpleInfoPanel));
    final system = tester.getRect(find.byKey(const Key('simple-system-line')));
    expect(info.bottom, size.height - tileBottom, reason: 'Info keeps the corner');
    expect(system.bottom, info.top - 8, reason: 'System on the row above');
    await tester.tap(find.byKey(const Key('simple-system')));
    await tester.pump();
    expect(find.byKey(const Key('simple-system-line')), findsNothing);
  });

  testWidgets('Sky shows the planetarium over a frame, and back', (tester) async {
    final container = await _pump(tester, const Size(1024, 768));
    await _frame(tester, container);
    expect(find.byKey(const Key('sky')), findsNothing);
    await tester.tap(find.byKey(const Key('simple-sky')));
    await tester.pump();
    expect(find.byKey(const Key('sky')), findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-sky')));
    await tester.pump();
    expect(find.byKey(const Key('sky')), findsNothing);
  });

  group('guiding and the histogram fill the bottom row, 16 px from the edge and from Stop', () {
    const size = Size(1024, 768);
    final histogram = FrameHistogram(
      bins: List<int>.generate(256, (i) => i < 40 ? 1000 - (i - 20).abs() * 40 : 0),
      minAdu: 0, minCount: 0, maxAdu: 65535, maxCount: 1000, meanAdu: 900, stdDev: 50, median: 880, mad: 30,
      lowClipFraction: 0, highClipFraction: 0, width: 100, height: 100, bitDepth: 16);
    Rect guiding(WidgetTester t) => _panelOf(t, find.byKey(const Key('simple-guide-trace')));
    Rect hist(WidgetTester t) => _panelOf(t, find.byKey(const Key('simple-histogram')));
    double end(WidgetTester t) => t.getRect(find.byType(EmergencyStopButton)).left - kSimpleInset;

    testWidgets('guiding alone fills the row', (tester) async {
      await _pump(tester, size, steps: _someSteps);
      expect(guiding(tester), Rect.fromLTRB(kSimpleInset, size.height - kSimpleInset - kSimpleBarHeight,
          end(tester), size.height - kSimpleInset));
    });

    testWidgets('the histogram alone fills the row', (tester) async {
      final c = await _pump(tester, size, histogram: histogram);
      await _frame(tester, c);
      await tester.pump();
      expect(hist(tester).left, kSimpleInset);
      expect(hist(tester).right, end(tester));
    });

    testWidgets('both split it equally, guiding on the left', (tester) async {
      final c = await _pump(tester, size, steps: _someSteps, histogram: histogram);
      await _frame(tester, c);
      await tester.pump();
      final g = guiding(tester), h = hist(tester);
      expect(g.left, kSimpleInset);
      expect(h.right, end(tester));
      expect(h.left - g.right, kSimpleInset);
      expect(g.width, closeTo(h.width, 0.5));
    });
  });

  testWidgets('a tap on the image hides everything (Stop too); the status stays and drops into the corner',
      (tester) async {
    final container = await _pump(tester, const Size(1024, 768));
    await _frame(tester, container);
    container.read(captureProgressProvider.notifier).beginExposing(const Duration(seconds: 5));
    await tester.pump();
    final status = _panelOf(tester, find.textContaining('Taking a frame'));
    expect(status.topLeft, const Offset(kSimpleInset, kSimpleInset * 2 + kSimpleBarHeight),
        reason: 'top-left, under the strip');

    await tester.tapAt(const Offset(400, 400));
    await tester.pump(const Duration(milliseconds: 300));
    await tester.pump(const Duration(milliseconds: 300));
    expect(find.byType(EmergencyStopButton).hitTestable(), findsNothing);
    expect(find.byKey(const Key('simple-shutter')).hitTestable(), findsNothing);
    final corner = _panelOf(tester, find.textContaining('Taking a frame'));
    expect(corner.topLeft, const Offset(kSimpleInset, kSimpleInset));

    await tester.tapAt(const Offset(400, 400));
    await tester.pump(const Duration(milliseconds: 300));
    expect(find.byType(EmergencyStopButton).hitTestable(), findsOneWidget);
    container.read(captureProgressProvider.notifier).reset();
    await tester.pump(const Duration(seconds: 1));
  });

  testWidgets('the shot status counts down in whole seconds with a smooth bar', (tester) async {
    final container = await _pump(tester, const Size(1024, 768));
    container.read(captureProgressProvider.notifier).beginExposing(const Duration(seconds: 5));
    await tester.pump();
    final first = tester.widget<LinearProgressIndicator>(find.byType(LinearProgressIndicator)).value!;
    String left() => RegExp(r'\d+ s left').firstMatch(
        tester.widget<Text>(find.textContaining('s left')).textSpan!.toPlainText())![0]!;
    final firstText = left();
    await tester.pump(const Duration(seconds: 2));
    expect(tester.widget<LinearProgressIndicator>(find.byType(LinearProgressIndicator)).value, greaterThan(first));
    final text = left();
    expect(text, isNot(firstText));
    expect(text, matches(RegExp(r'^\d+ s left$')));
    final before = tester.widget<LinearProgressIndicator>(find.byType(LinearProgressIndicator)).value!;
    await tester.pump(const Duration(milliseconds: 16));
    expect(tester.widget<LinearProgressIndicator>(find.byType(LinearProgressIndicator)).value, greaterThan(before));
    container.read(captureProgressProvider.notifier).reset();
    await tester.pump(const Duration(seconds: 1));
  });

  testWidgets('the status pill never cuts its label short while the whole line fits', (tester) async {
    // Half-size test glyphs: "Taking a frame  3 s left" fits the 300 px pill.
    tester.platformDispatcher.textScaleFactorTestValue = 0.5;
    addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
    final container = await _pump(tester, const Size(1024, 768));
    container.read(captureProgressProvider.notifier).beginExposing(const Duration(seconds: 5));
    await tester.pump();
    final texts = find.descendant(of: find.byType(SimpleStatusPill), matching: find.byType(RichText));
    expect(texts, findsWidgets);
    for (final e in texts.evaluate()) {
      final p = e.renderObject! as RenderParagraph;
      expect(p.didExceedMaxLines, isFalse, reason: '"${p.text.toPlainText()}" is cut');
    }
    container.read(captureProgressProvider.notifier).reset();
    await tester.pump(const Duration(seconds: 1));
  });

  testWidgets('stopping a shot answers on the tap, before the daemon replies', (tester) async {
    // Requests from the fake-async zone never reach a socket, so the abort
    // stays in flight: the slow-rig case.
    final container = await _pump(tester, const Size(1024, 768),
        server: const AraServer(hostname: '127.0.0.1', port: 9));
    container.read(captureProgressProvider.notifier).beginExposing(const Duration(seconds: 30));
    await tester.pump();
    expect(find.bySemanticsLabel('Stop'), findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-shutter')));
    await tester.pump();
    expect(find.text('Stopping…'), findsOneWidget);
    expect(find.bySemanticsLabel('Stop'), findsNothing);
    await tester.pump(const Duration(seconds: 5));
    container.read(captureProgressProvider.notifier).reset();
    await tester.pumpWidget(const SizedBox());
    container.dispose();
    await tester.pump(const Duration(seconds: 5));
  });

  testWidgets('Stop is the compact STOP, reading HOLD while held', (tester) async {
    await _pump(tester, const Size(1024, 768));
    expect(find.descendant(of: find.byType(EmergencyStopButton), matching: find.text('STOP')), findsOneWidget);
    final gesture = await tester.startGesture(tester.getCenter(find.byType(EmergencyStopButton)));
    await tester.pump();
    expect(find.text('HOLD'), findsOneWidget);
    await gesture.up();
    await tester.pump(const Duration(seconds: 3));
    expect(find.text('STOP'), findsOneWidget);
  });

  testWidgets('the strip has Search, Night mode, the full UI and Settings out, in the full UI\'s order', (tester) async {
    final container = await _pump(tester, const Size(1024, 768));
    expect(find.byKey(const Key('simple-more')), findsNothing, reason: 'nothing hides under ⋯');
    final order = ['Search settings', 'Night mode', 'Show all options', 'Settings'];
    final xs = [for (final t in order) tester.getCenter(find.byTooltip(t)).dx];
    for (var i = 1; i < xs.length; i++) {
      expect(xs[i], greaterThan(xs[i - 1]), reason: '${order[i]} after ${order[i - 1]}');
    }

    await tester.tap(find.byTooltip('Settings'));
    await tester.pumpAndSettle();
    expect(container.read(simpleModeProvider).value, isFalse);
    expect(container.read(selectedTabIndexProvider), kOptionsTabIndex);

    await container.read(simpleModeProvider.notifier).set(true);
    await tester.pumpAndSettle();
    await tester.tap(find.byTooltip('Show all options'));
    await tester.pumpAndSettle();
    expect(container.read(simpleModeProvider).value, isFalse);
    expect(container.read(selectedTabIndexProvider), kLiveTabIndex);
  });

  testWidgets('only connected gear shows in the strip', (tester) async {
    await _pump(tester, const Size(1024, 768));
    for (final device in ['Camera', 'Mount', 'Guider', 'Focuser', 'Switches', 'Dome']) {
      expect(find.byTooltip(device), findsNothing, reason: '$device is not connected');
    }
  });

  testWidgets('Simple Mode hides the system status bar and gives it back on leaving', (tester) async {
    final calls = <MethodCall>[];
    tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, (call) async {
      if (call.method.startsWith('SystemChrome.setEnabledSystemUIMode')) calls.add(call);
      return null;
    });
    addTearDown(() => tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, null));
    await _pump(tester, const Size(1024, 768));
    expect(calls.map((c) => c.arguments).toList(), ['SystemUiMode.immersiveSticky']);
    await tester.pumpWidget(const SizedBox());
    expect(calls.last.arguments, 'SystemUiMode.edgeToEdge');
  });

  testWidgets('the shell takes focus from the root NightHotkey, so ⌘, (Settings) reaches it', (tester) async {
    tester.view.physicalSize = const Size(1024, 768);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    final container = ProviderContainer(overrides: [
      simpleModePrefsProvider.overrideWithValue(_MemPrefs()),
      selectedTabIndexProvider.overrideWith(_StartOnLive.new),
    ]);
    addTearDown(container.dispose);
    await container.read(simpleModeProvider.future);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(
        home: NightHotkey(child: SimpleShell(planetarium: SizedBox())),
      ),
    ));
    await tester.pump();
    await tester.sendKeyDownEvent(LogicalKeyboardKey.metaLeft);
    await tester.sendKeyEvent(LogicalKeyboardKey.comma);
    await tester.sendKeyUpEvent(LogicalKeyboardKey.metaLeft);
    await tester.pump();
    expect(container.read(selectedTabIndexProvider), kOptionsTabIndex);
  });

  testWidgets('buttons use 10 px corners, not Material pills', (tester) async {
    await _pump(tester, const Size(1024, 768));
    final context = tester.element(find.byKey(const Key('simple-mode')));
    final shape = TextButtonTheme.of(context).style!.shape!.resolve({});
    expect((shape! as RoundedRectangleBorder).borderRadius, BorderRadius.circular(10));
  });

  test('the histogram plots only the range the frame uses', () {
    final bins = List<int>.filled(256, 0);
    for (var i = 20; i < 40; i++) {
      bins[i] = 1000 - (i - 30).abs() * 90;
    }
    final (lo, hi) = histogramRange(bins);
    expect(lo, inInclusiveRange(15, 20));
    expect(hi, inInclusiveRange(39, 45));
    expect(histogramRange(const []), (0, 0));
  });

  test('System figures read like the rest of Simple Mode', () {
    expect(simpleUptime(11520), '3 h 12 min');
    expect(simpleUptime(300), '5 min');
    expect(simpleUptime(2 * 86400 + 4 * 3600), '2 d 4 h');
    expect(simpleFree(412 * 1073741824), '412 GB free');
    expect(simpleFree(1536 * 1048576), '1.5 GB free');
    expect(simpleFree(null), 'Unplugged');
    expect(simpleMemory(512 * 1048576), '512 MB');
    expect(simpleMemory(1536 * 1048576), '1.5 GB');
  });

  test('preset lists stay inside what the camera reports', () {
    expect(exposurePresets(minSec: 1, maxSec: 60), allOf(contains(const Duration(seconds: 1)),
        contains(const Duration(seconds: 60)), isNot(contains(const Duration(milliseconds: 500))),
        isNot(contains(const Duration(seconds: 120)))));
    expect(gainPresets(min: 0, max: 100), [0, 50, 100]);
    expect(gainPresets(min: 0, max: 0), contains(100));
  });

  test('the router picks the shell from the pref, and both shells maximize the window', () {
    expect(shellFor(simple: true), isA<SimpleShell>());
    expect(shellFor(simple: false), isA<AppShell>());
    expect(isShell(const SimpleShell()), isTrue);
    expect(isShell(const AppShell()), isTrue);
    expect(isShell(const SizedBox()), isFalse);
  });
}
