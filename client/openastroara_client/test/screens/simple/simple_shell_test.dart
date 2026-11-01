import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/main.dart' show isShell, shellFor;
import 'package:openastroara/models/server.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/screens/app_shell.dart';
import 'package:openastroara/screens/simple/simple_preview_panel.dart';
import 'package:openastroara/screens/simple/simple_shell.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/state/guider/guide_step_state.dart';
import 'package:openastroara/state/imaging/capture_progress_state.dart';
import 'package:openastroara/state/imaging/last_frame_state.dart';
import 'package:openastroara/widgets/imaging/capture_progress_card.dart';
import 'package:openastroara/state/settings/settings_nav.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/widgets/emergency_stop_button.dart';
import 'package:openastroara/widgets/equipment/equipment_glyphs.dart';
import 'package:openastroara/widgets/settings_gear_button.dart';

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
    {List<GuideStep> steps = const [], AraServer? server}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final container = ProviderContainer(overrides: [
    simpleModePrefsProvider.overrideWithValue(_MemPrefs()),
    selectedTabIndexProvider.overrideWith(_StartOnLive.new),
    guideStepsProvider.overrideWith(() => _Steps(steps)),
    if (server != null) activeServerProvider.overrideWithValue(server),
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

double _panelOpacity(WidgetTester tester) => tester
    .widget<AnimatedOpacity>(find.ancestor(
      of: find.text('Show all options'),
      matching: find.byType(AnimatedOpacity),
    ))
    .opacity;

void main() {
  for (final size in const [Size(1470, 854), Size(1024, 768), Size(1100, 700)]) {
    testWidgets('lays out the strip, Preview column, switcher and Stop at $size',
        (tester) async {
      await _pump(tester, size);
      expect(tester.takeException(), isNull, reason: 'no overflow');
      for (final label in ['Show all options', 'Preview', 'Polar align', 'Focus', 'Target', 'Run', 'Loop', 'Single']) {
        expect(find.text(label), findsOneWidget, reason: label);
      }
      expect(find.byType(EmergencyStopButton), findsOneWidget);
      expect(find.byKey(const Key('simple-shutter')), findsOneWidget);
      final stop = tester.getRect(find.descendant(
          of: find.byType(EmergencyStopButton), matching: find.byType(FilledButton)));
      expect(stop.right, lessThanOrEqualTo(size.width - kSimpleInset + 0.5));
      expect(stop.bottom, lessThanOrEqualTo(size.height - kSimpleInset + 0.5));
    });
  }

  testWidgets('Emergency Stop is the heavier Simple Mode version: 56 px, 10 px corners, a red ring',
      (tester) async {
    await _pump(tester, const Size(1470, 854));
    final button = find.descendant(
        of: find.byType(EmergencyStopButton), matching: find.byType(FilledButton));
    final stop = tester.getRect(find.byType(EmergencyStopButton));
    final column = tester.getRect(find.byType(SimplePreviewPanel));
    expect(stop.size, const Size(kSimpleColumnWidth, kSimpleBarHeight),
        reason: 'ring included, the same box as the column and the bottom row');
    expect(stop.left, column.left);
    expect(stop.right, column.right);
    expect(tester.getSize(button).width, kSimpleColumnWidth - 6);
    final shape = tester.widget<FilledButton>(button).style!.shape!.resolve({})!;
    expect((shape as RoundedRectangleBorder).borderRadius, BorderRadius.circular(10));
    final ring = tester.widget<Container>(find.descendant(
        of: find.byType(EmergencyStopButton), matching: find.byType(Container)).first);
    expect((ring.decoration! as BoxDecoration).border, isNotNull);
  });

  testWidgets('buttons use 10 px corners, not Material pills', (tester) async {
    await _pump(tester, const Size(1470, 854));
    final context = tester.element(find.text('Show all options'));
    final shape = TextButtonTheme.of(context).style!.shape!.resolve({});
    expect(shape, isA<RoundedRectangleBorder>());
    expect((shape! as RoundedRectangleBorder).borderRadius, BorderRadius.circular(10));
    expect(find.byType(SegmentedButton<Object>), findsNothing);
  });

  testWidgets('a shot in flight shows one status line in the column, not the wide progress card',
      (tester) async {
    final container = await _pump(tester, const Size(1470, 854));
    container.read(captureProgressProvider.notifier).beginExposing(const Duration(seconds: 5));
    await tester.pump();
    expect(tester.takeException(), isNull, reason: 'no overflow');
    expect(find.byType(CaptureProgressCard), findsNothing);
    expect(find.textContaining(RegExp(r'Starting…|s left')), findsOneWidget);
    expect(find.bySemanticsLabel('Stop'), findsOneWidget, reason: 'the shutter cancels the shot');
    container.read(captureProgressProvider.notifier).reset();
    await tester.pump(const Duration(seconds: 1));
  });

  for (final size in const [Size(1470, 854), Size(1024, 768)]) {
    testWidgets('at $size the modes sit in the column above Emergency Stop and the bottom row is stats',
        (tester) async {
      final container = await _pump(tester, size, steps: _someSteps);
      container.read(lastCapturedFrameIdProvider.notifier).set('frame-1');
      await tester.pump();
      expect(tester.takeException(), isNull, reason: 'no overflow');
      Rect panelOf(Finder f) =>
          tester.getRect(find.ancestor(of: f, matching: find.byType(SimplePanel)).first);
      final modes = panelOf(find.text('Polar align'));
      final column = tester.getRect(find.byType(SimplePreviewPanel));
      final stop = tester.getRect(find.byType(EmergencyStopButton));
      expect(modes.left, column.left);
      expect(modes.right, column.right);
      expect(stop.top - modes.bottom, kSimpleInset);
      final guiding = panelOf(find.byKey(const Key('simple-guide-trace')));
      expect(guiding.bottom, stop.bottom, reason: 'stats share the bottom row');
      expect(guiding.right, lessThanOrEqualTo(column.left - kSimpleInset));
      expect(find.byKey(const Key('simple-shutter')).hitTestable(), findsOneWidget,
          reason: 'the capture controls stay reachable on a short window');
    });
  }

  testWidgets('with no frame yet the planetarium fills the image area, beside the column and under the strip',
      (tester) async {
    await _pump(tester, const Size(1470, 854));
    expect(find.text('No frame yet'), findsNothing);
    expect(tester.getRect(find.byKey(const Key('sky'))), const Rect.fromLTWH(0, 0, 1470, 854),
        reason: 'full bleed, like a frame, so the panels keep their places');
  });

  testWidgets('panels with nothing to show stay hidden: no guiding data, no frame yet', (tester) async {
    final container = await _pump(tester, const Size(1470, 854));
    expect(find.text('Guiding'), findsNothing);
    expect(find.text('Last frame'), findsNothing);
    expect(find.byKey(const Key('simple-shutter')), findsOneWidget, reason: 'the controls stay');

    final shutterBefore = tester.getRect(find.byKey(const Key('simple-shutter')));

    container.read(lastCapturedFrameIdProvider.notifier).set('frame-1');
    await tester.pump();
    expect(find.text('Last frame'), findsOneWidget);
    expect(tester.getRect(find.byKey(const Key('simple-shutter'))), shutterBefore,
        reason: 'the capture controls stay put when the facts panel appears');
    Rect panelOf(Finder f) =>
        tester.getRect(find.ancestor(of: f, matching: find.byType(SimplePanel)).first);
    final facts = panelOf(find.text('Last frame'));
    final controls = panelOf(find.byKey(const Key('simple-shutter')));
    final modes = panelOf(find.text('Polar align'));
    final stop = tester.getRect(find.byType(EmergencyStopButton));
    for (final gap in [
      controls.top - facts.bottom,
      modes.top - controls.bottom,
      stop.top - modes.bottom,
    ]) {
      expect(gap, kSimpleInset, reason: 'one 16 px gap all the way down the column');
    }
  });

  testWidgets('device dots use the agreed icons: focus corners, toggle, shield with check; guider, panel and dome glyphs',
      (tester) async {
    await _pump(tester, const Size(1470, 854));
    expect(tester.takeException(), isNull);
    for (final icon in [Icons.center_focus_strong, Icons.toggle_on, Icons.verified_user]) {
      expect(find.byIcon(icon), findsOneWidget, reason: '$icon');
    }
    expect(find.byType(EquipmentGlyph), findsNWidgets(5), reason: 'mount, guider, filter wheel, flat panel, dome');
  });

  testWidgets('the shot status counts down while exposing instead of freezing on its first value',
      (tester) async {
    final container = await _pump(tester, const Size(1470, 854));
    container.read(captureProgressProvider.notifier).beginExposing(const Duration(seconds: 5));
    await tester.pump();
    final first = tester.widget<LinearProgressIndicator>(find.byType(LinearProgressIndicator)).value!;
    final firstText = tester.widget<Text>(find.textContaining('s left')).data;

    await tester.pump(const Duration(seconds: 2));
    expect(tester.widget<LinearProgressIndicator>(find.byType(LinearProgressIndicator)).value,
        greaterThan(first), reason: 'the bar advances');
    expect(tester.widget<Text>(find.textContaining('s left')).data, isNot(firstText),
        reason: 'the remaining time counts down');
    expect(tester.widget<Text>(find.textContaining('s left')).data, matches(RegExp(r'^\d+ s left$')),
        reason: 'whole seconds only');

    final before = tester.widget<LinearProgressIndicator>(find.byType(LinearProgressIndicator)).value!;
    await tester.pump(const Duration(milliseconds: 16));
    expect(tester.widget<LinearProgressIndicator>(find.byType(LinearProgressIndicator)).value,
        greaterThan(before), reason: 'the bar moves every frame, not in steps');

    container.read(captureProgressProvider.notifier).reset();
    await tester.pump(const Duration(seconds: 1));
  });

  testWidgets('nothing in the strip moves when Ready changes to Taking a frame', (tester) async {
    final container = await _pump(tester, const Size(1470, 854));
    Rect at(Finder f) => tester.getRect(f);
    final ready = at(find.text('Ready'));
    final options = at(find.text('Show all options'));
    final camera = at(find.byTooltip('Camera'));

    container.read(captureProgressProvider.notifier).beginExposing(const Duration(seconds: 5));
    await tester.pump();
    expect(at(find.text('Taking a frame')).topLeft, ready.topLeft);
    expect(at(find.text('Show all options')), options);
    expect(at(find.byTooltip('Camera')), camera);
    final label = find.ancestor(of: find.text('Taking a frame'), matching: find.byKey(const Key('simple-activity')));
    final labelBefore = ready.width;
    expect(at(label).width, isNot(lessThan(labelBefore)), reason: 'a fixed slot, not sized to the text');

    container.read(captureProgressProvider.notifier).reset();
    await tester.pump(const Duration(seconds: 1));
  });

  testWidgets('stopping a shot answers on the tap, before the daemon replies', (tester) async {
    // Requests from the test's fake-async zone never reach a socket, so the
    // abort POST stays in flight: exactly the slow-rig case.
    final container = await _pump(tester, const Size(1470, 854),
        server: const AraServer(hostname: '127.0.0.1', port: 9));
    container.read(captureProgressProvider.notifier).beginExposing(const Duration(seconds: 30));
    await tester.pump();
    expect(find.bySemanticsLabel('Stop'), findsOneWidget);

    await tester.tap(find.byKey(const Key('simple-shutter')));
    await tester.pump();
    expect(find.text('Stopping…'), findsOneWidget, reason: 'feedback on the tap');
    expect(find.bySemanticsLabel('Stop'), findsNothing, reason: 'the square gives way at once');

    // Let the abort time out and every request drain before teardown.
    await tester.pump(const Duration(seconds: 5));
    container.read(captureProgressProvider.notifier).reset();
    await tester.pumpWidget(const SizedBox());
    // With a server set, root providers start their polls; dispose them here,
    // before the pending-timer check (the tearDown dispose is then a no-op).
    container.dispose();
    await tester.pump(const Duration(seconds: 5));
  });

  testWidgets('a tap on the frame hides every panel but Emergency Stop, a second brings them back',
      (tester) async {
    final container = await _pump(tester, const Size(1470, 854));
    container.read(lastCapturedFrameIdProvider.notifier).set('frame-1');
    await tester.pump();
    expect(find.byKey(const Key('sky')), findsNothing, reason: 'the frame replaces the sky');
    expect(_panelOpacity(tester), 1);

    await tester.tapAt(const Offset(500, 400));
    await tester.pump(const Duration(milliseconds: 300));
    expect(_panelOpacity(tester), 0);
    expect(find.byType(EmergencyStopButton).hitTestable(), findsOneWidget,
        reason: 'Emergency Stop stays in the clean view');
    expect(find.text('Show all options').hitTestable(), findsNothing,
        reason: 'hidden panels take no taps');

    await tester.tapAt(const Offset(500, 400));
    await tester.pump(const Duration(milliseconds: 300));
    expect(_panelOpacity(tester), 1);
  });

  testWidgets('the gear returns to the full UI on Settings', (tester) async {
    final container = await _pump(tester, const Size(1470, 854));
    await tester.tap(find.byType(SettingsGearButton));
    await tester.pump();
    expect(container.read(simpleModeProvider).value, isFalse);
    expect(container.read(selectedTabIndexProvider), kOptionsTabIndex);
  });

  testWidgets('Show all options opens Live; Target opens Planning', (tester) async {
    final container = await _pump(tester, const Size(1470, 854));
    await tester.tap(find.text('Show all options'));
    await tester.pump();
    expect(container.read(simpleModeProvider).value, isFalse);
    expect(container.read(selectedTabIndexProvider), kLiveTabIndex);

    await container.read(simpleModeProvider.notifier).set(true);
    await tester.tap(find.text('Target'));
    await tester.pump();
    expect(container.read(simpleModeProvider).value, isFalse);
    expect(container.read(selectedTabIndexProvider), kPlanningTabIndex);
  });

  test('the router picks the shell from the pref, and both shells maximize the window', () {
    expect(shellFor(simple: true), isA<SimpleShell>());
    expect(shellFor(simple: false), isA<AppShell>());
    expect(isShell(const SimpleShell()), isTrue);
    expect(isShell(const AppShell()), isTrue);
    expect(isShell(const SizedBox()), isFalse);
  });
}
