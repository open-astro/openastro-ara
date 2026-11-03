import 'dart:math' as math;
import 'dart:ui' show ImageFilter;

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart' show RenderParagraph;
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/imaging_run_body.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/state/alarm/safety_alarm_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/screens/simple/simple_plan_panel.dart';
import 'package:openastroara/state/sky_atlas/planetarium_selection_state.dart';
import 'package:openastroara/main.dart' show isShell, shellFor;
import 'package:openastroara/models/frame_histogram.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/models/stats/server_internal_state.dart';
import 'package:openastroara/services/storage_space_api.dart';
import 'package:openastroara/state/stats/server_internal_state_state.dart';
import 'package:openastroara/screens/app_shell.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/screens/simple/simple_focus_panel.dart';
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
import 'package:openastroara/models/profile_list.dart';
import 'package:openastroara/models/profile_meta.dart';
import 'package:openastroara/screens/settings/settings_shell.dart';
import 'package:openastroara/state/launch_gate_state.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/services/server_maintenance_api.dart';
import 'package:openastroara/services/plate_solve_database_api.dart';
import 'package:openastroara/models/camera_status.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/focuser_status.dart';
import 'package:openastroara/models/mount_status.dart';
import 'package:openastroara/state/equipment/camera_state.dart';
import 'package:openastroara/state/equipment/equipment_device_state.dart';
import 'package:openastroara/state/equipment/focuser_state.dart';
import 'package:openastroara/state/equipment/mount_state.dart';
import 'package:openastroara/widgets/equipment/equipment_glyphs.dart';
import 'package:openastroara/state/settings/settings_nav.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/night_mode_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/services/night_mode_prefs_service.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/screens/simple/simple_message_pills.dart';
import 'package:openastroara/widgets/emergency_stop_button.dart';
import 'package:openastroara/widgets/imaging/histogram_strip.dart' show frameHistogramProvider;
import 'package:openastroara/widgets/night_hotkey.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/state/sky_atlas/tonight_sky_state.dart' show planningSettingsBootstrapProvider;

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
    ServerInternalState? internal, StorageSpace? storage, ProfileList? profiles, SimpleRun? plan, SequenceRunStateInfo? runState, bool queueing = false, bool manualAlarm = false, bool feature = false, bool pier = false, bool linkUp = false, List<Override> gear = const [], DaemonVersions? versions, List<String>? starDatabases}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final container = ProviderContainer(overrides: [
    simpleModePrefsProvider.overrideWithValue(_MemPrefs()),
    if (feature) simpleModeFeaturePrefsProvider.overrideWithValue(_MemPrefs()),
    if (pier) simplePermanentPierPrefsProvider.overrideWithValue(_MemPrefs()..value = false),
    if (linkUp) serverLinkUpProvider.overrideWithValue(true),
    ...gear,
    if (versions != null) daemonVersionsProvider.overrideWith((ref) async => versions),
    if (starDatabases != null)
      plateSolveDatabaseStatusProvider.overrideWith((ref) async => PlateSolveDatabaseStatus(
          configuredPath: '/var/lib/astap', effectivePath: '/var/lib/astap', fileCount: 9,
          databases: starDatabases, solverPath: '/opt/openastroara/astap_cli', solverFound: true)),
    selectedTabIndexProvider.overrideWith(_StartOnLive.new),
    guideStepsProvider.overrideWith(() => _Steps(steps)),
    if (server != null) activeServerProvider.overrideWithValue(server),
    // A server would otherwise dial a real event stream.
    if (server != null) wsEventStreamProvider.overrideWith((ref) => null),
    if (histogram != null) frameHistogramProvider.overrideWith((ref, id) async => histogram),
    if (internal != null) serverInternalStateProvider.overrideWith(() => _Internal(internal)),
    if (storage != null) storageSpaceProvider.overrideWith((ref) async => storage),
    if (profiles != null) profileManagementProvider.overrideWith(() => _Profiles(profiles)),
    if (plan != null) simpleRunProvider.overrideWith(() => queueing ? _Queueing(plan) : _Plan(plan)),
    if (runState != null) sequenceRunStateProvider.overrideWith(() => _RunState(runState)),
    if (manualAlarm) safetyAlarmProvider.overrideWith(_ManualAlarm.new),
  ]);
  addTearDown(container.dispose);
  await container.read(simpleModeProvider.future);
  if (feature) await container.read(simpleModeFeatureProvider.future);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: container,
    child: const MaterialApp(home: SimpleShell(planetarium: SizedBox(key: Key('sky')))),
  ));
  await tester.pump();
  return container;
}

Future<void> _openSection(WidgetTester tester, String section) async {
  if (find.byKey(const Key('simple-menu')).evaluate().isEmpty) {
    await tester.tap(find.byTooltip('Settings'));
    await tester.pump();
  }
  await tester.tap(find.byKey(Key('simple-menu-nav-$section')));
  await tester.pump();
  await tester.pump(); // the storage read resolves
}

mixin _Calls<T extends EquipmentDeviceStatus> on EquipmentDeviceNotifier<T> {
  final calls = <String>[];
  @override
  Future<bool> disconnect() async {
    calls.add('disconnect');
    return true;
  }

  @override
  Future<bool> reconnect() async {
    calls.add('reconnect');
    return true;
  }
}

Map<String, dynamic> _wire(String name, EquipmentConnectionState s) => {'device_id': 'd', 'name': name, 'state': s.name};

class _Camera extends CameraStatusNotifier with _Calls<CameraStatus> {
  _Camera(this.state0);
  final EquipmentConnectionState state0;
  @override
  Future<CameraStatus?> build() async => CameraStatus.fromJson(_wire('ASI2600MM', state0));
}

class _Focuser extends FocuserNotifier with _Calls<FocuserStatus> {
  _Focuser(this.state0);
  final EquipmentConnectionState state0;
  @override
  Future<FocuserStatus?> build() async => FocuserStatus.fromJson(_wire('EAF', state0));
}

class _Mount extends MountNotifier with _Calls<MountStatus> {
  _Mount(this.state0);
  final EquipmentConnectionState state0;
  @override
  Future<MountStatus?> build() async => MountStatus.fromJson(_wire('AM5', state0));
}

class _OpticsMount extends MountNotifier {
  _OpticsMount(this.caps);
  final Map<String, dynamic> caps;
  @override
  Future<MountStatus?> build() async =>
      MountStatus.fromJson({..._wire('AM5', EquipmentConnectionState.connected), 'capabilities': caps});
}

class _OpticsApi extends ProfileApi {
  _OpticsApi(this.optics) : super(const AraServer(hostname: 'test', port: 1));
  OpticsSettings optics;
  final puts = <OpticsSettings>[];
  @override
  Future<OpticsSettings> getOptics() async => optics;
  @override
  Future<OpticsSettings> putOptics(OpticsSettings value) async {
    puts.add(value);
    return optics = value;
  }
}

/// A camera cooling to −10 °C from [ccd]; [set] moves the reading.
class _CoolingCamera extends CameraStatusNotifier {
  _CoolingCamera(this.ccd);
  double ccd;
  static Map<String, dynamic> _wire(double t) => {
        'device_id': 'c',
        'name': 'ASI2600MM',
        'state': 'connected',
        'capabilities': {'has_cooler': true, 'can_set_temperature': true},
        'runtime': {'ccd_temperature': t, 'cooler_on': true, 'cooler_setpoint_c': -10},
      };
  @override
  Future<CameraStatus?> build() async => CameraStatus.fromJson(_wire(ccd));
  void set(double t) => state = AsyncData(CameraStatus.fromJson(_wire(t)));
}

class _NightPrefs extends NightModePrefsService {
  bool value = false;
  @override
  Future<bool> load() async => value;
  @override
  Future<void> save(bool enabled) async => value = enabled;
}

class _Profiles extends ProfileManagementNotifier {
  _Profiles(this.value);
  final ProfileList value;
  @override
  Future<ProfileList> build() async => value;
}

class _Plan extends SimpleRunNotifier {
  _Plan(this.value);
  final SimpleRun value;
  @override
  SimpleRun build() => value;

  var stops = 0;
  @override
  Future<void> stop() async => stops++;
}

/// Add to queue without a daemon: the picked target joins the queue.
class _Queueing extends _Plan {
  _Queueing(super.value);
  @override
  Future<bool> create(WidgetRef ref, {bool first = false}) async {
    final sel = ref.read(planetariumSelectionProvider)!;
    state = SimpleRun(sequenceId: 'seq-1', targets: [
      ...state.targets,
      SimpleTarget(name: sel.name, steps: const [FilterPlanStep(filterName: 'L', exposureSeconds: 120, frameCount: 30)]),
    ]);
    return true;
  }
}

/// Drives the safety alarm by hand: no WS, no player, no prefs.
class _ManualAlarm extends SafetyAlarmController {
  @override
  SafetyAlarmState build() => const SafetyAlarmState();

  void raise(String reason, {bool ringing = false}) =>
      state = state.copyWith(pending: !ringing, ringing: ringing, reason: reason);

  @override
  void silence() => state = state.copyWith(pending: false, ringing: false);
}

class _RunState extends SequenceRunStateNotifier {
  _RunState(this.value);
  final SequenceRunStateInfo value;
  @override
  Future<SequenceRunStateInfo?> build() async => value;
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

  testWidgets('Preview\'s shutter is the camera button: a filled circle, not play', (tester) async {
    await _pump(tester, const Size(1024, 768));
    expect(find.descendant(of: find.byKey(const Key('simple-shutter')), matching: find.byIcon(Icons.play_arrow)),
        findsNothing);
  });

  testWidgets('at rest only the essentials show: no mode list, no settings, no info', (tester) async {
    await _pump(tester, const Size(1024, 768));
    for (final hidden in ['Focus', 'Polar align', 'Plan', 'Exposure', 'Gain', 'Loop', 'HFR', 'Guiding']) {
      expect(find.text(hidden), findsNothing, reason: '$hidden is one tap away, not out');
    }
    expect(find.text('Tap the shutter to take a frame'), findsOneWidget);
    // No scrim behind it: it has to read at 4.5:1 on a dark sky by itself.
    final hint = tester.widget<Text>(find.text('Tap the shutter to take a frame')).style!.color!;
    double lum(Color c) => c.computeLuminance();
    final sky = Color.alphaBlend(hint, const Color(0xFF0A0A0A));
    expect((math.max(lum(sky), lum(const Color(0xFF0A0A0A))) + 0.05) / (math.min(lum(sky), lum(const Color(0xFF0A0A0A))) + 0.05),
        greaterThanOrEqualTo(4.5));
    expect(find.byKey(const Key('sky')), findsOneWidget, reason: 'the sky until there is a frame');
    // Sky opens the planetarium, which is already open: Sky shows lit (not dimmed), and
    // screen readers hear there's nothing to switch.
    expect(tester.widget<Icon>(find.descendant(of: find.byKey(const Key('simple-sky')), matching: find.byType(Icon))).color,
        AraColors.textPrimary);
    expect(tester.getSemantics(find.byKey(const Key('simple-sky'))), isSemantics(hasEnabledState: true, isEnabled: false));
  });

  testWidgets('the mode name opens the modes in order, Autopilot first; picking one closes them', (tester) async {
    final container = await _pump(tester, const Size(1024, 768));
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    final modes = ['Autopilot', 'Preview', 'Focus', 'Polar align', 'Plan'];
    // The grid's tile, not the rail's label (which also reads Preview): the leftmost match.
    // The tile's corner, not the text's: a name on two lines sits higher in its tile.
    Offset tile(String m) => (find.text(m).evaluate().map((e) => tester.getTopLeft(
                find.ancestor(of: find.byWidget(e.widget), matching: find.byType(InkWell)).first)).toList()
          ..sort((a, b) => a.dx.compareTo(b.dx)))
        .first;
    final order = [for (final m in modes) tile(m)];
    for (var i = 1; i < order.length; i++) {
      final before = order[i - 1], now = order[i];
      expect(now.dy > before.dy || (now.dy == before.dy && now.dx > before.dx), isTrue,
          reason: '${modes[i]} after ${modes[i - 1]}');
    }
    // The current mode's tile is blue: white words (4.6:1), and announced as selected.
    final current = tester.widget<Text>(find.text('Preview').evaluate()
        .map((e) => e.widget as Text)
        .map((t) => find.byWidget(t))
        .reduce((a, b) => tester.getTopLeft(a).dx < tester.getTopLeft(b).dx ? a : b));
    expect(current.style?.color, AraColors.selectionFg);
    expect(find.bySemanticsLabel(RegExp('^Preview\$')).evaluate().isNotEmpty, isTrue);
    expect(tester.getSemantics(find.bySemanticsLabel(RegExp('^Preview\$')).first), isSemantics(isSelected: true));
    await tester.tap(find.text('Preview').first);
    await tester.pump();
    expect(find.text('Focus'), findsNothing, reason: 'closed');

    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.text('Plan'));
    await tester.pump();
    expect(container.read(simpleModeProvider).value, isTrue, reason: 'Plan stays in Simple Mode');
    expect(find.descendant(of: find.byKey(const Key('simple-mode')), matching: find.text('Plan')), findsOneWidget);
    // Picking Plan opens nothing; the chip opens its sheet, which with nothing
    // picked asks for a target.
    expect(find.text('Search, or pick in the sky.'), findsNothing);
    await tester.tap(find.byKey(const Key('simple-settings')));
    await tester.pump();
    expect(find.text('Search, or pick in the sky.'), findsOneWidget);
    // The chip reads the plan's total imaging time: 60 frames at the 5 s default.
    expect(find.descendant(of: find.byKey(const Key('simple-settings')), matching: find.text('5 min')), findsOneWidget);
    // A long night takes two lines in the 60 px chip, hours over minutes, under the icon (as P.A.'s).
    // (The test font's square glyphs are too wide to measure the real fit.)
    container.read(simpleRunPlanProvider.notifier)
      ..setExposure(0, 150)
      ..setFrames(0, 300);
    await tester.pump();
    final long = find.descendant(of: find.byKey(const Key('simple-settings')), matching: find.text('12 h\n30 min'));
    expect(tester.widget<Text>(long).maxLines, 2);
    expect(find.descendant(of: find.byKey(const Key('simple-settings')), matching: find.byIcon(Icons.tune)), findsOneWidget);
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
    expect(find.text('No focuser connected'), findsNothing, reason: 'picking a mode opens nothing');
    // Picking a mode only switches it; the chip opens its panel.
    await tester.tap(find.byKey(const Key('simple-settings')));
    await tester.pump();
    final rail = _panelOf(tester, find.byKey(const Key('simple-shutter')));
    final panel = _panelOf(tester, find.text('No focuser connected'));
    expect(panel.right, rail.left - kSimpleInset, reason: 'left of the rail');
    expect(panel.top, rail.top, reason: 'level with its top');

    // The chip speaks for the mode: Focus's summary, not Preview's exposure.
    final chip = find.byKey(const Key('simple-settings'));
    expect(find.descendant(of: chip, matching: find.text(_summary(container))), findsNothing);
    expect(find.descendant(of: chip, matching: find.text('Manual')), findsOneWidget,
        reason: 'no focuser connected, so focus is by hand');
    await tester.tap(chip);
    await tester.pump();
    expect(find.byType(SimpleFocusPanel), findsNothing, reason: 'the chip hides the Focus panel');
    expect(find.text('Exposure'), findsNothing, reason: "never Preview's settings in Focus");
    expect(_panelOf(tester, find.byKey(const Key('simple-shutter'))), rail, reason: 'the rail never moves');
    await tester.tap(chip);
    await tester.pump();
    expect(find.byType(SimpleFocusPanel), findsOneWidget, reason: 'and shows it again');

    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.text('Preview').first);
    await tester.pump();
    expect(find.byType(SimpleFocusPanel), findsNothing, reason: 'Preview has no panel');
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
    expect(find.byKey(const Key('simple-polar-exposure')), findsNothing, reason: 'picking a mode opens nothing');
    await tester.tap(chip);
    await tester.pump();
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

  testWidgets('the P.A. chip stacks exposure over Loop/Single under the icon, never cut', (tester) async {
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
    expect(find.descendant(of: chip, matching: find.byIcon(Icons.tune)), findsOneWidget);
    final p = tester.renderObject<RenderParagraph>(
        find.descendant(of: find.descendant(of: chip, matching: find.text('45 s\nSingle')), matching: find.byType(RichText)));
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

  testWidgets('the settings chip opens Exposure from a list, the Gain slider under it, Filter and Loop/Single',
      (tester) async {
    final container = await _pump(tester, const Size(1024, 768));
    expect(find.text(_summary(container)), findsOneWidget, reason: 'the chip reads the settings');
    await tester.tap(find.byKey(const Key('simple-settings')));
    await tester.pump();
    expect(tester.takeException(), isNull);
    final exposure = tester.getRect(find.byKey(const Key('simple-exposure')));
    final gain = tester.getRect(find.byKey(const Key('simple-gain')));
    expect(gain.top, greaterThan(exposure.bottom), reason: 'the slider gets the full width');
    expect(find.byKey(const Key('simple-camera-gain')), findsNothing);
    expect(find.text('Filter'), findsOneWidget);
    expect(find.text('Loop'), findsOneWidget);

    await tester.tap(find.byKey(const Key('simple-exposure')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('10 s').last);
    await tester.pumpAndSettle();
    expect(container.read(exposureControllerProvider).exposure, const Duration(seconds: 10));
    tester.widget<Slider>(find.byKey(const Key('simple-gain'))).onChangeEnd!(200);
    await tester.pump();
    expect(container.read(exposureControllerProvider).gain, 200, reason: "the camera's gain, as in the Camera window");
    expect(find.text('10 s · 200'), findsOneWidget, reason: 'the chip follows');

    await tester.tapAt(const Offset(400, 400));
    await tester.pump();
    expect(find.text('Loop'), findsNothing, reason: 'a tap on the image closes it');
  });

  testWidgets('the chip shows the settings icon in every mode, at one height, so the rail never changes size',
      (tester) async {
    await _pump(tester, const Size(1024, 768));
    final chip = find.byKey(const Key('simple-settings'));
    Size? size;
    for (final m in ['Preview', 'Focus', 'Polar align', 'Plan']) {
      await tester.tap(find.byKey(const Key('simple-mode')));
      await tester.pump();
      await tester.tap(find.text(m).first);
      await tester.pump();
      expect(find.descendant(of: chip, matching: find.byIcon(Icons.tune)), findsOneWidget, reason: m);
      size ??= tester.getSize(chip);
      expect(tester.getSize(chip), size, reason: '$m: same chip height');
    }
  });

  testWidgets('the settings popover\'s segments (Loop / Single) are at least 44 px tall', (tester) async {
    await _pump(tester, const Size(1024, 768));
    await tester.tap(find.byKey(const Key('simple-settings')));
    await tester.pump();
    final segments = find.byType(SimpleSegment);
    expect(segments, findsWidgets);
    for (final e in segments.evaluate()) {
      expect(tester.getSize(find.byWidget(e.widget)).height, greaterThanOrEqualTo(44));
    }
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

  testWidgets('with nothing done tonight Preview offers Autopilot; it walks Focus first, the rail reads Auto',
      (tester) async {
    await _pump(tester, const Size(1024, 768));
    expect(find.byKey(const Key('autopilot-offer')), findsOneWidget);
    await tester.tap(find.byKey(const Key('autopilot-start')));
    await tester.pump();
    expect(find.byKey(const Key('autopilot-offer')), findsNothing);
    expect(find.byKey(const Key('autopilot-strip')), findsOneWidget);
    // "Autopilot" doesn't fit the 60 px rail: it reads Auto, as Polar align reads P.A.
    expect(find.descendant(of: find.byKey(const Key('simple-mode')), matching: find.text('Auto')), findsOneWidget);
    expect(find.bySemanticsLabel(RegExp(r'^Mode: Autopilot')), findsOneWidget, reason: 'the screen reader says it whole');
    expect(find.byType(SimpleFocusPanel), findsOneWidget, reason: "Focus's panel, open");
    await tester.tap(find.byKey(const Key('autopilot-skip')));
    await tester.pump();
    expect(find.byType(SimplePolarPanel), findsOneWidget, reason: "on to polar align's panel");
    await tester.tap(find.byKey(const Key('autopilot-skip')));
    await tester.pump();
    expect(find.text("Tonight's plan"), findsOneWidget, reason: "then the plan's sheet");
    // Picking a mode leaves Autopilot.
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.text('Preview').first);
    await tester.pump();
    expect(find.byKey(const Key('autopilot-strip')), findsNothing);
  });

  for (final (size, scale, queued) in const [
    (Size(1024, 768), 1.0, 0),
    (Size(1024, 768), 1.0, 1),
    (Size(768, 1024), 1.0, 1),
    (Size(1024, 768), 1.3, 1),
    (Size(1024, 768), 1.0, 10),
    (Size(768, 1024), 1.0, 10),
  ]) {
    testWidgets('at $size, text ${scale}x, in Autopilot\'s Plan step ($queued queued) the settings '
        'chip, a tap on the image and the mode button all put the sheet away', (tester) async {
      tester.platformDispatcher.textScaleFactorTestValue = scale;
      addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
      final container = await _pump(tester, size,
          plan: queued == 0
              ? null
              : SimpleRun(sequenceId: 'seq-1', targets: [
                  for (var i = 1; i <= queued; i++)
                    SimpleTarget(name: 'M$i', steps: const [FilterPlanStep(filterName: 'L', exposureSeconds: 120, frameCount: 30)]),
                ]));
      await _frame(tester, container);
      await tester.tap(find.byKey(const Key('autopilot-start')));
      await tester.pump();
      await tester.tap(find.byKey(const Key('autopilot-skip')));
      await tester.pump();
      await tester.tap(find.byKey(const Key('autopilot-skip')));
      await tester.pump();
      final sheet = find.text("Tonight's plan");
      expect(sheet, findsOneWidget);
      // Each tap must land on its control, not on whatever covers it.
      Future<void> tap(Finder f) async {
        final box = tester.renderObject(f);
        expect(tester.hitTestOnBinding(tester.getCenter(f)).path.any((e) => e.target == box), isTrue,
            reason: '$f is covered');
        await tester.tap(f);
        await tester.pump();
      }

      await tap(find.byKey(const Key('simple-settings')));
      expect(sheet, findsNothing, reason: 'the chip puts it away');
      await tap(find.byKey(const Key('simple-settings')));
      expect(sheet, findsOneWidget, reason: 'and brings it back');
      // Beside the sheet's top, above the left rail: the image.
      final sheetBox = tester.getRect(find.byType(SimpleRunSheet));
      await tester.tapAt(Offset((sheetBox.left + kSimpleInset) / 2, sheetBox.top + 8));
      await tester.pump();
      expect(sheet, findsNothing, reason: 'a tap on the image puts it away');
      await tap(find.byKey(const Key('simple-settings')));
      await tap(find.byKey(const Key('simple-mode')));
      expect(sheet, findsNothing, reason: 'the modes replace it');
      expect(find.text('Autopilot'), findsOneWidget, reason: 'the modes are open');
      await tap(find.ancestor(of: find.text('Focus'), matching: find.byType(InkWell)).first);
      expect(find.byKey(const Key('autopilot-strip')), findsNothing, reason: 'Focus picked: Autopilot left');
      expect(find.byType(SimpleFocusPanel), findsNothing, reason: 'picking a mode opens nothing');
    });
  }

  testWidgets('in Autopilot\'s Plan step over the sky, after Add to queue ten times, the chip and the modes still '
      'put the sheet away', (tester) async {
    await _pump(tester, const Size(1024, 768), plan: const SimpleRun(), queueing: true);
    await tester.tap(find.byKey(const Key('autopilot-start')));
    await tester.pump();
    await tester.tap(find.byKey(const Key('autopilot-skip')));
    await tester.pump();
    await tester.tap(find.byKey(const Key('autopilot-skip')));
    await tester.pump();
    final c = ProviderScope.containerOf(tester.element(find.byType(SimpleShell)));
    for (var i = 1; i <= 10; i++) {
      c.read(planetariumSelectionProvider.notifier).set(PlanetariumSelection(
          name: 'M$i', kind: 'Galaxy', raHours: 0.7, decDeg: 41, raJ2000Hours: 0.7, decJ2000Deg: 41, altDeg: 60,
          maxAltDeg: 70));
      await tester.pump();
      await tester.tap(find.byKey(const Key('simple-run-queue')));
      await tester.pumpAndSettle();
    }
    expect(c.read(simpleRunProvider).targets, hasLength(10));
    final sheet = find.text("Tonight's plan");
    expect(sheet, findsOneWidget, reason: 'the sheet stays up while queueing');
    Future<void> tap(Finder f) async {
      final box = tester.renderObject(f);
      expect(tester.hitTestOnBinding(tester.getCenter(f)).path.any((e) => e.target == box), isTrue,
          reason: '$f is covered');
      await tester.tap(f);
      await tester.pump();
    }

    await tap(find.byKey(const Key('simple-settings')));
    expect(sheet, findsNothing, reason: 'the chip puts it away');
    await tap(find.byKey(const Key('simple-settings')));
    await tap(find.byKey(const Key('simple-mode')));
    expect(sheet, findsNothing, reason: 'the modes replace it');
    await tap(find.ancestor(of: find.text('Focus'), matching: find.byType(InkWell)).first);
    expect(find.byKey(const Key('autopilot-strip')), findsNothing, reason: 'Focus picked: Autopilot left');
  });

  for (final size in const [Size(1024, 768), Size(768, 1024)]) {
    testWidgets('at $size: with two queued, Plan\'s sheet for the next pick fits without scrolling', (tester) async {
      await _pump(tester, size,
          plan: SimpleRun(sequenceId: 'seq-1', targets: [
            for (var i = 1; i <= 2; i++)
              SimpleTarget(name: 'M$i', steps: const [FilterPlanStep(filterName: 'L', exposureSeconds: 120, frameCount: 30)]),
          ]));
      final c = ProviderScope.containerOf(tester.element(find.byType(SimpleShell)));
      c.read(planetariumSelectionProvider.notifier).set(const PlanetariumSelection(
          name: 'M42', kind: 'Nebula', raHours: 5.6, decDeg: -5.4, raJ2000Hours: 5.6, decJ2000Deg: -5.4, altDeg: 40,
          maxAltDeg: 60));
      await tester.tap(find.byKey(const Key('simple-mode')));
      await tester.pump();
      await tester.tap(find.text('Plan').first);
      await tester.pump();
      await tester.tap(find.byKey(const Key('simple-settings')));
      await tester.pump();
      final scroll = find.descendant(of: find.byType(SimpleRunSheet), matching: find.byType(Scrollable)).first;
      expect(tester.state<ScrollableState>(scroll).position.maxScrollExtent, 0, reason: 'everything shows');
    });
  }

  testWidgets('Autopilot picked from the modes stays in Simple Mode and starts, as the offer does', (tester) async {
    final container = await _pump(tester, const Size(1024, 768));
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.text('Autopilot'));
    await tester.pump();
    expect(container.read(simpleModeProvider).value, isTrue, reason: 'no jump to the full UI');
    expect(find.byKey(const Key('autopilot-strip')), findsOneWidget);
    expect(find.byType(SimpleFocusPanel), findsOneWidget, reason: "Focus's panel, open");
  });

  testWidgets('in Autopilot the chip names the step whose settings it shows', (tester) async {
    await _pump(tester, const Size(1024, 768));
    await tester.tap(find.byKey(const Key('autopilot-start')));
    await tester.pump();
    final chip = find.byKey(const Key('simple-settings'));
    expect(find.descendant(of: chip, matching: find.text('Focus\nManual')), findsOneWidget);
    await tester.tap(find.byKey(const Key('autopilot-skip')));
    await tester.pump();
    expect(find.descendant(of: chip, matching: find.text('P.A.\n1 s')), findsOneWidget);
    await tester.tap(find.byKey(const Key('autopilot-skip')));
    await tester.pump();
    expect(find.descendant(of: chip, matching: find.text('Plan\n5 min')), findsOneWidget);
  });

  for (final size in const [Size(1024, 768), Size(768, 1024), Size(507, 768)]) {
    testWidgets('at $size: message pills sit top-left, beside the status pill, above the left rail, '
        'under the Autopilot offer', (tester) async {
      final container = await _pump(tester, size);
      await _frame(tester, container);
      container.read(captureProgressProvider.notifier).beginExposing(const Duration(seconds: 5));
      for (var i = 0; i < 6; i++) {
        container.read(simpleMessagesProvider.notifier).show(SimpleMessageKind.error, 'Message $i');
      }
      await tester.pump();
      // The rail and the offer are measured after the first frame.
      await tester.pump(const Duration(milliseconds: 300));
      final pills = tester.getRect(find.byType(SimpleMessagePills));
      final status = _panelOf(tester, find.textContaining('Taking a frame'));
      final rail = _panelOf(tester, find.text('Library'));
      final offer = tester.getRect(find.byKey(const Key('autopilot-offer')));
      expect(pills.left, kSimpleInset, reason: 'the top-left corner');
      expect(pills.top, greaterThanOrEqualTo(offer.bottom), reason: 'under the Autopilot offer');
      expect(pills.right, lessThanOrEqualTo(status.left), reason: 'beside the status pill, never under it');
      expect(pills.bottom, lessThanOrEqualTo(rail.top), reason: 'above the left rail');
      expect(find.byKey(const Key('simple-messages-more')), findsOneWidget, reason: 'six don\'t fit: the rest fold');
      container.read(captureProgressProvider.notifier).reset();
      await tester.pump(const Duration(seconds: 1));
    });
  }

  testWidgets('the Autopilot offer can be dismissed', (tester) async {
    await _pump(tester, const Size(1024, 768));
    await tester.tap(find.byTooltip('Not now'));
    await tester.pump();
    expect(find.byKey(const Key('autopilot-offer')), findsNothing);
  });

  testWidgets('⌘. stops a running plan only when pressed twice within 3 s, like the keyboard\'s Stop', (tester) async {
    final container = await _pump(tester, const Size(1024, 768),
        plan: const SimpleRun(sequenceId: 'seq-1', started: true, targets: [
          SimpleTarget(name: 'M31', steps: [FilterPlanStep(filterName: 'L', exposureSeconds: 120, frameCount: 30)]),
        ]),
        runState: const SequenceRunStateInfo(sequenceId: 'seq-1', state: SequenceRunState.running));
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.text('Plan'));
    await tester.pump();
    final plan = container.read(simpleRunProvider.notifier) as _Plan;
    Future<void> chord() async {
      await tester.sendKeyDownEvent(LogicalKeyboardKey.metaLeft);
      await tester.sendKeyEvent(LogicalKeyboardKey.period);
      await tester.sendKeyUpEvent(LogicalKeyboardKey.metaLeft);
      await tester.pump();
    }

    await chord();
    expect(plan.stops, 0, reason: 'the first press only asks');
    expect(find.textContaining('again to stop the run'), findsOneWidget);
    expect(container.read(simpleMessagesProvider).single.kind, SimpleMessageKind.question,
        reason: 'it asks for something: amber, not a green result');
    await tester.pump(const Duration(seconds: 4));
    await chord();
    expect(plan.stops, 0, reason: 'the window ran out: this press asks again');
    await chord();
    expect(plan.stops, 1);
    await tester.pump(const Duration(seconds: 10));
  });

  testWidgets('Add target from the queue panel never traps the sheet: the modes and the chip still work', (tester) async {
    // User, 2026-11-03: plan square -> queue panel -> Add target, and the sheet could no
    // longer be hidden or the mode changed.
    final queue = SimpleRun(sequenceId: 'seq-1', targets: const [
      SimpleTarget(name: 'M31', steps: [FilterPlanStep(filterName: 'L', exposureSeconds: 120, frameCount: 30)]),
    ]);
    await _pump(tester, const Size(1024, 768), plan: queue);
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.text('Plan').first);
    await tester.pump();
    await tester.tap(find.descendant(of: find.byKey(const Key('simple-run-tracker')), matching: find.text('M31')));
    await tester.pump();
    await tester.tap(find.text('Add target'));
    await tester.pump();
    expect(find.byType(SimpleRunSheet), findsOneWidget, reason: 'the sheet opens to add a target');

    // The mode name opens the modes over it.
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    expect(find.byType(SimpleRunSheet), findsNothing, reason: 'the modes replace the sheet');
    expect(find.text('Polar align'), findsOneWidget, reason: 'the mode grid shows');
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();

    // And from Add target again, the chip hides the sheet.
    await tester.tap(find.descendant(of: find.byKey(const Key('simple-run-tracker')), matching: find.text('M31')));
    await tester.pump();
    await tester.tap(find.text('Add target'));
    await tester.pump();
    expect(find.byType(SimpleRunSheet), findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-settings')));
    await tester.pump();
    expect(find.byType(SimpleRunSheet), findsNothing, reason: 'the chip closes the sheet it would open');
  });

  testWidgets('the plan square hugs the bottom-right corner, above the bottom row', (tester) async {
    await _pump(tester, const Size(1024, 768),
        plan: const SimpleRun(sequenceId: 'seq-1', targets: [
          SimpleTarget(name: 'M31', steps: [FilterPlanStep(filterName: 'L', exposureSeconds: 120, frameCount: 30)]),
        ]));
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.text('Plan').first);
    await tester.pump();
    final square = tester.getRect(find.byKey(const Key('simple-run-tracker')));
    expect(square.right, 1024 - kSimpleInset, reason: 'in the corner');
    expect(square.bottom, 768 - (kSimpleInset * 2 + kSimpleBarHeight), reason: 'just above the bottom row');
    final rail = _panelOf(tester, find.byKey(const Key('simple-shutter')));
    expect(square.top, greaterThan(rail.bottom), reason: 'below the right rail');
    expect(find.descendant(of: find.byKey(const Key('simple-run-tracker')), matching: find.text('0 / 30')), findsOneWidget);
  });

  for (final size in const [Size(1024, 768), Size(768, 1024)]) {
    testWidgets('at $size: Plan\'s sheet runs from the status pill to the bottom row while the plan squares '
        'step aside; a plan square opens the queue panel', (tester) async {
      // A long queue: taller than the room, so the sheet must scroll inside its slot.
      await _pump(tester, size,
          plan: SimpleRun(sequenceId: 'seq-1', targets: [
            for (var i = 1; i <= 10; i++)
              SimpleTarget(name: 'M$i', steps: const [FilterPlanStep(filterName: 'L', exposureSeconds: 120, frameCount: 30)]),
          ]));
      await tester.tap(find.byKey(const Key('simple-mode')));
      await tester.pump();
      await tester.tap(find.text('Plan').first);
      await tester.pump();
      await tester.tap(find.byKey(const Key('simple-settings')));
      await tester.pump();
      final sheet = tester.getRect(find.byType(SimpleRunSheet));
      expect(sheet.top, greaterThanOrEqualTo(kSimpleInset * 3 + kSimpleBarHeight + kSimpleStatusHeight),
          reason: 'under the status pill');
      expect(sheet.bottom, lessThanOrEqualTo(size.height - kSimpleInset * 2 - kSimpleBarHeight),
          reason: 'above the bottom row');
      expect(find.byKey(const Key('simple-run-tracker')), findsNothing, reason: 'the squares step aside');
      // The chip puts the sheet away: the squares come back.
      await tester.tap(find.byKey(const Key('simple-settings')));
      await tester.pump();
      final squares = tester.getRect(find.byKey(const Key('simple-run-tracker')));
      // Ten queued: the squares keep to the right half (the Info tiles have
      // the left) and the rest fold into one "+N" square.
      expect(squares.left, greaterThanOrEqualTo(size.width / 2), reason: 'the right half');
      expect(find.descendant(of: find.byKey(const Key('simple-run-tracker')), matching: find.textContaining('+')),
          findsOneWidget);
      // The queue isn't in the sheet: a plan square opens it in its own panel.
      expect(find.descendant(of: find.byType(SimpleRunSheet), matching: find.textContaining('QUEUE')),
          findsNothing);
      await tester.tap(find.descendant(of: find.byKey(const Key('simple-run-tracker')), matching: find.text('M1')));
      await tester.pump();
      expect(find.descendant(of: find.byType(SimpleRunPanel), matching: find.textContaining('QUEUE · 10 targets')),
          findsOneWidget);
      // The queue panel stops above the plan squares, scrolling inside.
      expect(tester.takeException(), isNull, reason: 'no overflow');
      final panel = tester.getRect(find.ancestor(of: find.byType(SimpleRunPanel), matching: find.byType(Scrollable)).first);
      expect(panel.bottom, lessThanOrEqualTo(squares.top - kSimpleInset), reason: 'panel $panel, squares $squares');
    });
  }

  for (final size in const [Size(1024, 768), Size(768, 1024)]) {
    testWidgets('at $size: in Autopilot the plan squares stay clear of the step strip', (tester) async {
      await _pump(tester, size,
          plan: SimpleRun(sequenceId: 'seq-1', targets: [
            for (var i = 1; i <= 6; i++)
              SimpleTarget(name: 'M$i', steps: const [FilterPlanStep(filterName: 'L', exposureSeconds: 120, frameCount: 30)]),
          ]));
      await tester.tap(find.byKey(const Key('autopilot-start')));
      await tester.pump();
      await tester.tap(find.byKey(const Key('autopilot-skip')));
      await tester.pump();
      await tester.tap(find.byKey(const Key('autopilot-skip')));
      await tester.pump();
      await tester.tap(find.byKey(const Key('simple-settings')));
      await tester.pump();
      final strip = tester.getRect(find.byKey(const Key('autopilot-strip')));
      final squares = tester.getRect(find.byKey(const Key('simple-run-tracker')));
      expect(squares.overlaps(strip), isFalse, reason: 'strip $strip, squares $squares');
    });
  }

  // Info and System: a tile per fact in the bottom-left corner, above the guiding and histogram row, growing
  // right; System's row sits above Info's.
  const tileLeft = kSimpleInset;
  const tileBottom = kSimpleInset * 2 + kSimpleBarHeight;

  testWidgets('at 2x text the rails widen with their labels and the facts turn into a list (WCAG 1.4.4)',
      (tester) async {
    tester.platformDispatcher.textScaleFactorTestValue = 2;
    addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
    final container = await _pump(tester, const Size(1024, 768));
    await _frame(tester, container);
    await tester.tap(find.byKey(const Key('simple-info')));
    await tester.pump();
    // Labels follow the user's size all the way; nothing caps them at 1.3x.
    final library = tester.element(find.text('Library'));
    expect(MediaQuery.textScalerOf(library).scale(1), 2);
    expect(tester.getSize(find.byKey(const Key('simple-info'))).width, greaterThan(60),
        reason: 'the rail button grows past its 1x width');
    // The facts read as one list, label and value per line, not 72 px tiles.
    expect(find.byKey(const Key('simple-facts-list')), findsOneWidget);
    final hfr = tester.getRect(find.text('HFR'));
    final clipped = tester.getRect(find.text('Clipped'));
    expect(clipped.top, greaterThan(hfr.top), reason: 'one fact per line');
    expect(tester.takeException(), isNull);
  });

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

  testWidgets('the gear opens Settings over the image: dimmed, blurred 5 px, 90 % panel, sections on the left; '
      'a tap outside closes it; the left rail has no System', (tester) async {
    const size = Size(1024, 768);
    await _pump(tester, size);
    expect(find.byKey(const Key('simple-system')), findsNothing, reason: 'System lives in Settings › Rig');
    await tester.tap(find.byTooltip('Settings'));
    await tester.pump();
    final sheet = find.byKey(const Key('simple-menu'));
    expect(sheet, findsOneWidget);
    final rect = tester.getRect(sheet);
    expect(rect.left, kSimpleInset);
    expect(rect.right, size.width - kSimpleInset, reason: 'the width of the window');
    final blur = tester.widget<BackdropFilter>(find.descendant(of: sheet, matching: find.byType(BackdropFilter)));
    expect(blur.filter, ImageFilter.blur(sigmaX: 5, sigmaY: 5));
    final box = tester.widget<Container>(find.descendant(of: sheet, matching: find.byKey(const Key('simple-frosted-surface'))));
    expect((box.decoration! as BoxDecoration).color, AraColors.overlayScrim);
    for (final t in ['Devices', 'Rig']) {
      expect(find.descendant(of: sheet, matching: find.text(t)), findsWidgets, reason: t);
    }
    expect(find.byKey(const Key('simple-versions')), findsOneWidget, reason: 'Settings opens on Rig');
    expect(find.text('All settings'), findsNothing, reason: 'no full settings form in Simple Mode');
    await tester.tapAt(Offset(size.width / 2, size.height - 4)); // the veil, below the sheet
    await tester.pump();
    expect(sheet, findsNothing);
  });

  testWidgets('Rig: the rig\'s figures, rig and profile and how to change them, Permanent pier, the Simple Mode '
      'switch, version and logs', (tester) async {
    final c = await _pump(tester, const Size(1470, 854),
        server: const AraServer(hostname: '10.0.0.5', port: 5555, mdnsName: 'astro'),
        internal: const ServerInternalState(uptimeSeconds: 11520, cpuPercent: 23.4, workingSetBytes: 512 * 1048576),
        storage: const StorageSpace(saveDirectory: '/data', isFallback: false, freeBytes: 412 * 1073741824, totalBytes: 900 * 1073741824),
        profiles: const ProfileList(activeId: 'p1', profiles: [ProfileMeta(id: 'p1', name: 'Backyard Rig')]),
        versions: DaemonVersions.fromJson({
          'daemon_version': '0.0.1-ara.7',
          'os_release': 'Debian GNU/Linux 13 (trixie)',
          'board_model': 'Raspberry Pi 5 Model B Rev 1.1',
          'guider_version': '2.6.13dev7',
          'alpaca_servers': [
            {'host': '10.0.30.223', 'port': 6800, 'server_name': 'AlpacaHTTP', 'manufacturer': 'OpenAstro.net', 'version': '6.0.0~nightly'},
          ],
          'astap_version': 'CLI-2026.09.01',
        }),
        starDatabases: const ['d05', 'd80']);
    await _openSection(tester, 'Rig');
    // Every version on the rig, for a bug report or a support question.
    final versions = find.byKey(const Key('simple-versions'));
    for (final (name, value) in const [
      ('OpenAstro Ara server', '0.0.1-ara.7'),
      ('OpenAstro Guider', '2.6.13dev7'),
      ('AlpacaBridge', '6.0.0~nightly'),
      ('ASTAP', 'CLI-2026.09.01'),
      ('Star database', 'D05, D80'),
      ('System', 'Debian GNU/Linux 13 (trixie) · Raspberry Pi 5 Model B Rev 1.1'),
    ]) {
      expect(find.descendant(of: versions, matching: find.text(name)), findsOneWidget, reason: name);
      expect(find.descendant(of: versions, matching: find.text(value)), findsOneWidget, reason: value);
    }
    expect(find.descendant(of: find.byKey(const Key('simple-versions')), matching: find.text('This app')), findsOneWidget);
    expect(find.text('0.0.1-ara.7'), findsOneWidget);
    // Beside its name and in readable type, not at the far edge in a caption.
    final name = tester.getRect(find.text('OpenAstro Ara server'));
    final value = tester.getRect(find.text('0.0.1-ara.7'));
    expect(value.left - name.right, lessThan(48), reason: 'the version sits next to its name');
    expect(tester.widget<Text>(find.text('Versions')).style!.fontSize, greaterThanOrEqualTo(14));
    final sheet = find.byKey(const Key('simple-menu'));
    final line = find.descendant(of: sheet, matching: find.byKey(const Key('simple-system-line')));
    for (final t in ['412 GB free', '23 %', '512 MB', '3 h 12 min']) {
      expect(find.descendant(of: line, matching: find.text(t)), findsOneWidget, reason: t);
    }
    // Spread across the page like the mock, not bunched on the left.
    final tiles = [for (final t in ['Storage', 'CPU', 'Memory', 'Up']) tester.getRect(find.descendant(of: line, matching: find.text(t)))];
    final row = tester.getRect(line);
    final page = tester.getRect(find.ancestor(of: line, matching: find.byType(Column)).first);
    expect(row.width, closeTo(page.width, 1), reason: 'the tiles fill the page width');
    expect(tiles.last.center.dx - tiles.first.center.dx, greaterThan(page.width * 0.6));
    expect(find.descendant(of: sheet, matching: find.text('Name')), findsOneWidget, reason: 'not "Rig" twice');
    expect(find.descendant(of: sheet, matching: find.text('Rig')), findsOneWidget, reason: 'the section list names the page; no title repeats it');
    for (final t in ['astro', 'Backyard Rig', 'Change', 'Permanent pier', 'Simple Mode', 'Logs & bug report']) {
      expect(find.descendant(of: sheet, matching: find.text(t)), findsWidgets, reason: t);
    }
    expect(c.read(simpleModeProvider).value, isTrue);
    await tester.pumpWidget(const SizedBox());
    c.dispose();
    await tester.pump(const Duration(seconds: 5));
  });

  testWidgets('Rig sets the telescope with digit wheels and the site with this device\'s location, adjust wheels and '
      'an elevation stepper, saved to the profile; no text fields', (tester) async {
    final c = await _pump(tester, const Size(1024, 768));
    c.read(opticsSettingsProvider.notifier)
      ..setApertureMm(80)
      ..setFocalLengthMm(480);
    c.read(siteSettingsProvider.notifier)
      ..setLatitudeDeg(-37.2037)
      ..setLongitudeDeg(174.8824)
      ..setElevationM(71);
    await _openSection(tester, 'Rig');
    expect(find.byType(TextField), findsNothing, reason: 'touch only');
    Finder value(String id, String text) => find.descendant(of: find.byKey(Key('simple-rig-$id')), matching: find.text(text));
    expect(value('aperture', '80 mm'), findsOneWidget);
    expect(value('lat', '37.2037° S'), findsOneWidget);
    expect(value('lng', '174.8824° E'), findsOneWidget);
    expect(find.text('71 m'), findsOneWidget);
    Future<void> roll(String wheel, int items) async {
      await tester.ensureVisible(find.byKey(Key(wheel)));
      await tester.pumpAndSettle();
      await tester.drag(find.byKey(Key(wheel)), Offset(0, -44.0 * items));
      await tester.pumpAndSettle();
    }

    // One wheel per digit; a wheel at rest sets the value.
    await tester.tap(find.byKey(const Key('simple-rig-aperture')));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('simple-rig-aperture-wheel-2')), findsOneWidget, reason: 'three digits');
    await roll('simple-rig-aperture-wheel-2', 2);
    expect(c.read(opticsSettingsProvider).apertureMm, 82);
    await tester.ensureVisible(find.byKey(const Key('simple-rig-aperture')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('simple-rig-aperture')));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('simple-rig-aperture-wheel-0')), findsNothing, reason: 'a second tap closes it');

    await tester.ensureVisible(find.byKey(const Key('simple-rig-focal')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('simple-rig-focal')));
    await tester.pumpAndSettle();
    await roll('simple-rig-focal-wheel-0', 1);
    expect(c.read(opticsSettingsProvider).focalLengthMm, 1480);

    // Latitude: hemisphere, degrees, four decimals.
    await tester.ensureVisible(find.byKey(const Key('simple-rig-focal')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('simple-rig-focal')));
    await tester.pumpAndSettle();
    await tester.ensureVisible(find.byKey(const Key('simple-rig-lat')));
    await tester.tap(find.byKey(const Key('simple-rig-lat')));
    await tester.pumpAndSettle();
    await roll('simple-rig-lat-wheel-0', -1);
    expect(c.read(siteSettingsProvider).latitudeDeg, closeTo(37.2037, 1e-9), reason: 'S to N');
    await roll('simple-rig-lat-wheel-6', 1);
    expect(c.read(siteSettingsProvider).latitudeDeg, closeTo(37.2038, 1e-9));

    // Elevation: a tap is a metre; a hold runs.
    await tester.ensureVisible(find.byKey(const Key('simple-rig-elevation-up')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('simple-rig-elevation-up')));
    await tester.pump();
    expect(c.read(siteSettingsProvider).elevationM, 72);
    final hold = await tester.startGesture(tester.getCenter(find.byKey(const Key('simple-rig-elevation-up'))));
    await tester.pump(const Duration(milliseconds: 600));
    for (var i = 0; i < 20; i++) {
      await tester.pump(const Duration(milliseconds: 100));
    }
    await hold.up();
    await tester.pump();
    expect(c.read(siteSettingsProvider).elevationM, greaterThan(90), reason: 'held: it runs, set on release');

    expect(find.byKey(const Key('simple-rig-use-location')), findsOneWidget, reason: 'fill from this device');
  });

  testWidgets('on launch Rig shows the profile\'s site and telescope, not empty fields', (tester) async {
    var loaded = 0;
    await _pump(tester, const Size(1024, 768), gear: [
      planningSettingsBootstrapProvider.overrideWith((ref) async {
        loaded++;
        await Future<void>.value(); // like the real fetch: no writes during the provider's build
        ref.read(siteSettingsProvider.notifier)
          ..setLatitudeDeg(-37.2)
          ..setLongitudeDeg(174.9)
          ..setElevationM(71);
        ref.read(opticsSettingsProvider.notifier).setApertureMm(80);
      }),
    ]);
    await tester.pump();
    expect(loaded, 1, reason: 'Simple Mode loads the profile itself; nothing else may have');
    await _openSection(tester, 'Rig');
    Finder value(String id, String text) => find.descendant(of: find.byKey(Key('simple-rig-$id')), matching: find.text(text));
    expect(value('lat', '37.2000° S'), findsOneWidget);
    expect(value('lng', '174.9000° E'), findsOneWidget);
    expect(find.text('71 m'), findsOneWidget);
    expect(value('aperture', '80 mm'), findsOneWidget);
  });

  testWidgets('Rig\'s Name and Profile read like its other rows, set close together', (tester) async {
    await _pump(tester, const Size(1024, 768));
    await _openSection(tester, 'Rig');
    TextStyle? style(String t) => tester.widget<Text>(find.text(t)).style;
    for (final t in ['Name', 'Profile']) {
      expect(style(t), style('Aperture'), reason: t);
    }
    expect(tester.getCenter(find.text('Profile')).dy - tester.getCenter(find.text('Name')).dy, lessThanOrEqualTo(30),
        reason: 'read-only facts sit close together, not 44 pt apart');
  });

  testWidgets('a telescope the profile doesn\'t know comes from the mount, saved to the profile; a known one stays',
      (tester) async {
    final api = _OpticsApi(const OpticsSettings(focalLengthMm: 600));
    final c = await _pump(tester, const Size(1024, 768), gear: [
      profileApiProvider.overrideWithValue(api),
      mountProvider.overrideWith(() => _OpticsMount({'focal_length_mm': 480, 'aperture_diameter_mm': 80})),
    ]);
    await tester.pump();
    await tester.pump();
    expect(api.puts.single.apertureMm, 80, reason: 'unknown: the mount\'s, saved to the profile');
    expect(api.puts.single.focalLengthMm, 600, reason: 'the profile\'s own value wins');
    expect(c.read(opticsSettingsProvider).apertureMm, 80);
  });

  testWidgets('a mount that reports no optics leaves the profile alone', (tester) async {
    final api = _OpticsApi(const OpticsSettings());
    await _pump(tester, const Size(1024, 768), gear: [
      profileApiProvider.overrideWithValue(api),
      mountProvider.overrideWith(() => _OpticsMount({})),
    ]);
    await tester.pump();
    await tester.pump();
    expect(api.puts, isEmpty);
  });

  testWidgets('Change profile goes to the profile box; Change rig to the rig list', (tester) async {
    final c = await _pump(tester, const Size(1024, 768));
    c.read(profileGatePassedProvider.notifier).pass();
    await _openSection(tester, 'Rig');
    await tester.tap(find.byKey(const Key('simple-rig-change-profile')));
    await tester.pump();
    expect(c.read(profileGatePassedProvider), isFalse);
    expect(c.read(serverChooserRequestedProvider), isFalse, reason: 'the same rig: straight to its profiles');

    c.read(profileGatePassedProvider.notifier).pass();
    await _openSection(tester, 'Rig');
    await tester.tap(find.byKey(const Key('simple-rig-change-rig')));
    await tester.pump();
    expect(c.read(profileGatePassedProvider), isFalse);
    expect(c.read(serverChooserRequestedProvider), isTrue, reason: 'the rig list, which never connects on its own');
  });

  testWidgets('while a plan runs, Rig says it keeps running', (tester) async {
    await _pump(tester, const Size(1024, 768),
        plan: const SimpleRun(sequenceId: 'seq-1', started: true, targets: [
          SimpleTarget(name: 'M 31', steps: [FilterPlanStep(filterName: 'L', exposureSeconds: 120, frameCount: 30)]),
        ]),
        runState: const SequenceRunStateInfo(sequenceId: 'seq-1', state: SequenceRunState.running));
    await _openSection(tester, 'Rig');
    expect(find.textContaining('keeps running on the rig'), findsOneWidget);
  });

  testWidgets('Permanent pier (the one thing Ara can\'t know) and the Simple Mode switch (the way out)', (tester) async {
    final c = await _pump(tester, const Size(1024, 768), pier: true);
    await _openSection(tester, 'Rig');
    // List-style settings use the platform's switch (segments stay for the
    // panels over the image).
    expect(find.byKey(const Key('simple-menu-pier-on')), findsNothing);
    final pier = find.byKey(const Key('simple-menu-pier'));
    expect(tester.widget<Switch>(pier).value, isFalse);
    await tester.ensureVisible(pier); // Rig scrolls
    await tester.tap(pier);
    await tester.pump();
    expect(c.read(simplePermanentPierProvider).value, isTrue);
    expect(tester.widget<Switch>(find.byKey(const Key('simple-menu-simple-mode'))).value, isTrue);
    await tester.ensureVisible(find.byKey(const Key('simple-menu-simple-mode')));
    await tester.tap(find.byKey(const Key('simple-menu-simple-mode')));
    await tester.pump();
    expect(c.read(simpleModeProvider).value, isFalse, reason: 'off: the full UI');
  });

  testWidgets('Logs & bug report opens over Simple Mode; Back comes back', (tester) async {
    final c = await _pump(tester, const Size(1024, 768));
    await _openSection(tester, 'Rig');
    await tester.ensureVisible(find.text('Logs & bug report')); // Rig scrolls
    await tester.tap(find.text('Logs & bug report'));
    await tester.pumpAndSettle();
    expect(find.byType(SettingsShell), findsOneWidget);
    expect(c.read(selectedSettingsPanelProvider), 'support.logs');
    expect(c.read(simpleModeProvider).value, isTrue);
    await tester.tap(find.byTooltip('Back'));
    await tester.pumpAndSettle();
    expect(find.byType(SettingsShell), findsNothing);
  });

  testWidgets('Devices lists the profile\'s devices with their state: Disconnect, Connect, Reconnect after a failure; '
      'devices never set up are not listed', (tester) async {
    final camera = _Camera(EquipmentConnectionState.connected);
    final focuser = _Focuser(EquipmentConnectionState.error);
    final mount = _Mount(EquipmentConnectionState.disconnected);
    await _pump(tester, const Size(1024, 768), linkUp: true, gear: [
      cameraStatusProvider.overrideWith(() => camera),
      focuserProvider.overrideWith(() => focuser),
      mountProvider.overrideWith(() => mount),
    ]);
    await _openSection(tester, 'Devices');
    final page = find.byKey(const Key('simple-gear'));
    expect(find.text('Devices'), findsOneWidget, reason: 'only in the section list, no page title');
    expect(find.descendant(of: page, matching: find.textContaining('ASI2600MM')), findsOneWidget);
    expect(find.descendant(of: page, matching: find.textContaining('Filter wheel')), findsNothing, reason: 'never set up');
    await tester.tap(find.byKey(const Key('simple-gear-camera-disconnect')));
    await tester.tap(find.byKey(const Key('simple-gear-focuser-reconnect')));
    await tester.tap(find.byKey(const Key('simple-gear-mount-connect')));
    await tester.pump();
    expect(camera.calls, ['disconnect']);
    expect(focuser.calls, ['reconnect']);
    expect(mount.calls, ['reconnect'], reason: 'Connect: the device the rig remembers, no chooser');
  });

  testWidgets('the camera\'s dot opens its window over Simple Mode, not the full Settings page', (tester) async {
    final c = await _pump(tester, const Size(1024, 768), linkUp: true, gear: [
      cameraStatusProvider.overrideWith(() => _Camera(EquipmentConnectionState.connected)),
    ]);
    // What the top bar's camera chip does (openSettingsPanel).
    c.read(selectedSettingsPanelProvider.notifier).select('eq.camera');
    c.read(selectedTabIndexProvider.notifier).select(kOptionsTabIndex);
    await tester.pumpAndSettle();
    expect(find.byType(SettingsShell), findsNothing);
    expect(find.byKey(const Key('simple-device-camera')), findsOneWidget);
    expect(c.read(simpleModeProvider).value, isTrue);
    // The same look as the Settings sheet: the 90 % panel over a 5 px blur.
    final window = find.ancestor(of: find.byKey(const Key('simple-device-camera')), matching: find.byType(BackdropFilter));
    expect(tester.widget<BackdropFilter>(window).filter, ImageFilter.blur(sigmaX: 5, sigmaY: 5));
    final surface = find.descendant(of: window, matching: find.byKey(const Key('simple-frosted-surface')));
    expect((tester.widget<Container>(surface).decoration! as BoxDecoration).color, AraColors.overlayScrim);
    await tester.tapAt(const Offset(512, 700)); // the image closes it
    await tester.pump();
    expect(find.byKey(const Key('simple-device-camera')), findsNothing);
  });

  testWidgets('one tap opens what was tapped; windows that don\'t overlap stay open together; the image or a '
      'second tap closes', (tester) async {
    final c = await _pump(tester, const Size(1024, 768), linkUp: true, gear: [
      cameraStatusProvider.overrideWith(() => _Camera(EquipmentConnectionState.connected)),
    ]);
    Future<void> cameraDot() async {
      c.read(selectedSettingsPanelProvider.notifier).select('eq.camera');
      c.read(selectedTabIndexProvider.notifier).select(kOptionsTabIndex);
      await tester.pumpAndSettle();
    }
    final window = find.byKey(const Key('simple-device-camera'));
    await cameraDot();
    expect(window, findsOneWidget);
    await cameraDot();
    expect(window, findsNothing, reason: 'the dot again closes it');

    await cameraDot();
    await tester.tap(find.byKey(const Key('simple-info')));
    await tester.pump();
    expect(find.byType(SimpleInfoPanel), findsOneWidget, reason: 'one tap opens Info');
    expect(window, findsOneWidget, reason: 'they don\'t overlap, so both stay');
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    expect(find.text('Polar align'), findsWidgets, reason: 'one tap opens the modes, beside the window');
    expect(window, findsOneWidget);

    await tester.tap(find.byKey(const Key('simple-frame')));
    await tester.pump();
    expect(window, findsNothing, reason: 'the image closes them');
    expect(find.text('Polar align'), findsNothing);
  });

  testWidgets('with Settings open, the top strip still works in one tap (the veil starts under it)', (tester) async {
    final c = await _pump(tester, const Size(1024, 768), gear: [nightModePrefsProvider.overrideWithValue(_NightPrefs())]);
    await tester.tap(find.byTooltip('Settings'));
    await tester.pump();
    await tester.tap(find.byTooltip('Night mode'));
    await tester.pump();
    await tester.pump();
    expect(c.read(nightModeProvider).value, isTrue, reason: 'Night mode toggled by the first tap');
    expect(find.byKey(const Key('simple-menu')), findsOneWidget, reason: 'Settings stays: they don\'t overlap');
    await tester.tap(find.byTooltip('Settings'));
    await tester.pump();
    expect(find.byKey(const Key('simple-menu')), findsNothing, reason: 'the gear again closes it');
  });

  testWidgets('a tap on the background closes every window, Info included, even over the sky', (tester) async {
    final c = await _pump(tester, const Size(1024, 768), linkUp: true, gear: [
      cameraStatusProvider.overrideWith(() => _Camera(EquipmentConnectionState.connected)),
    ]);
    // No frame yet: the planetarium is the background (it takes taps itself).
    expect(find.byKey(const Key('sky')), findsOneWidget);
    c.read(selectedSettingsPanelProvider.notifier).select('eq.camera');
    c.read(selectedTabIndexProvider.notifier).select(kOptionsTabIndex);
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('simple-info')));
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    expect(find.byKey(const Key('simple-device-camera')), findsOneWidget);
    expect(find.byType(SimpleInfoPanel), findsOneWidget);
    await tester.tapAt(const Offset(512, 420)); // the middle of the sky
    await tester.pump();
    expect(find.byKey(const Key('simple-device-camera')), findsNothing);
    expect(find.byType(SimpleInfoPanel), findsNothing);
    expect(find.text('Polar align'), findsNothing);
  });

  testWidgets('on a narrow window, opening a rail menu closes the camera window it would cover', (tester) async {
    final c = await _pump(tester, const Size(768, 1024), linkUp: true, gear: [
      cameraStatusProvider.overrideWith(() => _Camera(EquipmentConnectionState.connected)),
    ]);
    c.read(selectedSettingsPanelProvider.notifier).select('eq.camera');
    c.read(selectedTabIndexProvider.notifier).select(kOptionsTabIndex);
    await tester.pumpAndSettle();
    final window = find.byKey(const Key('simple-device-camera'));
    expect(window, findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    expect(find.text('Polar align'), findsWidgets, reason: 'one tap opens the modes');
    expect(window, findsNothing, reason: 'they would overlap');
  });

  testWidgets('the mount\'s dot opens the Mount window; a second device dot swaps the window', (tester) async {
    final c = await _pump(tester, const Size(1024, 768), linkUp: true, gear: [
      cameraStatusProvider.overrideWith(() => _Camera(EquipmentConnectionState.connected)),
      mountProvider.overrideWith(() => _Mount(EquipmentConnectionState.connected)),
    ]);
    Future<void> dot(String panel) async {
      c.read(selectedSettingsPanelProvider.notifier).select(panel);
      c.read(selectedTabIndexProvider.notifier).select(kOptionsTabIndex);
      await tester.pumpAndSettle();
    }
    await dot('eq.mount');
    expect(find.byKey(const Key('simple-device-mount')), findsOneWidget);
    expect(find.byType(SettingsShell), findsNothing);
    await dot('eq.camera');
    expect(find.byKey(const Key('simple-device-camera')), findsOneWidget);
    expect(find.byKey(const Key('simple-device-mount')), findsNothing, reason: 'one device window at a time (same spot)');
    await _openSection(tester, 'Mount');
    expect(find.descendant(of: find.byKey(const Key('simple-menu')), matching: find.byKey(const Key('simple-device-mount'))),
        findsOneWidget);
    // The same icons as the top bar's dots.
    final nav = find.byKey(const Key('simple-menu-nav-Mount'));
    expect(find.descendant(of: nav, matching: find.byType(EquipmentGlyph)), findsOneWidget, reason: 'the mount chip\'s telescope');
    expect(find.descendant(of: find.byKey(const Key('simple-menu-nav-Camera')), matching: find.byIcon(Icons.camera_alt)),
        findsOneWidget);
  });

  testWidgets('Settings lists the camera under Devices; it and its Devices row open the same window', (tester) async {
    await _pump(tester, const Size(1024, 768), linkUp: true, gear: [
      cameraStatusProvider.overrideWith(() => _Camera(EquipmentConnectionState.connected)),
    ]);
    await _openSection(tester, 'Camera');
    final sheet = find.byKey(const Key('simple-menu'));
    expect(find.descendant(of: sheet, matching: find.byKey(const Key('simple-device-camera'))), findsOneWidget);
    await _openSection(tester, 'Devices');
    await tester.tap(find.textContaining('ASI2600MM'));
    await tester.pump();
    expect(find.descendant(of: sheet, matching: find.byKey(const Key('simple-device-camera'))), findsOneWidget);
  });

  testWidgets('Devices with no rig link says so instead of showing stale states', (tester) async {
    await _pump(tester, const Size(1024, 768), gear: [
      cameraStatusProvider.overrideWith(() => _Camera(EquipmentConnectionState.connected)),
    ]);
    await _openSection(tester, 'Devices');
    expect(find.text('Rig not connected'), findsOneWidget);
    expect(find.byKey(const Key('simple-gear-camera-disconnect')), findsNothing);
  });

  testWidgets('a jump to a settings panel (gear chips, Open Storage) opens it over Simple Mode', (tester) async {
    // The test font is wider than the real one and overflows the Storage panel.
    tester.platformDispatcher.textScaleFactorTestValue = 0.5;
    addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
    final c = await _pump(tester, const Size(1024, 768));
    c.read(selectedSettingsPanelProvider.notifier).select('session.storage');
    c.read(selectedTabIndexProvider.notifier).select(kOptionsTabIndex);
    await tester.pumpAndSettle();
    expect(find.byType(SettingsShell), findsOneWidget);
    expect(c.read(selectedSettingsPanelProvider), 'session.storage');
    expect(c.read(simpleModeProvider).value, isTrue);
    await tester.tap(find.byTooltip('Back'));
    await tester.pumpAndSettle();
    // A second jump opens it again.
    c.read(selectedTabIndexProvider.notifier).select(kOptionsTabIndex);
    await tester.pumpAndSettle();
    expect(find.byType(SettingsShell), findsOneWidget);
  });

  testWidgets('turning Simple Mode off in Settings closes Settings onto the full UI', (tester) async {
    final c = await _pump(tester, const Size(1024, 768), feature: true);
    c.read(selectedTabIndexProvider.notifier).select(kOptionsTabIndex);
    await tester.pumpAndSettle();
    expect(find.byType(SettingsShell), findsOneWidget);
    await c.read(simpleModeProvider.notifier).set(false);
    await tester.pumpAndSettle();
    expect(find.byType(SettingsShell), findsNothing);
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
      // RA and Dec are named, not told apart by colour alone (WCAG 1.4.1).
      final trace = find.ancestor(of: find.byKey(const Key('simple-guide-trace')), matching: find.byType(Row)).first;
      expect(find.descendant(of: trace, matching: find.text('RA')), findsOneWidget);
      expect(find.descendant(of: trace, matching: find.text('Dec')), findsOneWidget);
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

  testWidgets('screen readers can hide and show the controls over a frame', (tester) async {
    final container = await _pump(tester, const Size(1024, 768));
    // The label leads; the frame's own words may follow it.
    expect(find.bySemanticsLabel(RegExp('^Hide controls')), findsNothing, reason: 'no frame yet: nothing to clear');
    await _frame(tester, container);
    expect(find.bySemanticsLabel(RegExp('^Hide controls')), findsOneWidget);
    await tester.tapAt(const Offset(400, 400));
    await tester.pump(const Duration(milliseconds: 300));
    expect(find.bySemanticsLabel(RegExp('^Show controls')), findsOneWidget);
  });

  testWidgets('a tap on the image hides everything (Stop too); the status stays and drops into the corner',
      (tester) async {
    final container = await _pump(tester, const Size(1024, 768));
    await _frame(tester, container);
    container.read(captureProgressProvider.notifier).beginExposing(const Duration(seconds: 5));
    await tester.pump();
    final status = _panelOf(tester, find.textContaining('Taking a frame'));
    expect(status.topRight, const Offset(1024 - kSimpleInset, kSimpleInset * 2 + kSimpleBarHeight),
        reason: 'top-right, under the strip (messages have the top-left)');

    await tester.tapAt(const Offset(400, 400));
    await tester.pump(const Duration(milliseconds: 300));
    await tester.pump(const Duration(milliseconds: 300));
    expect(find.byType(EmergencyStopButton).hitTestable(), findsNothing);
    expect(find.byKey(const Key('simple-shutter')).hitTestable(), findsNothing);
    final corner = _panelOf(tester, find.textContaining('Taking a frame'));
    expect(corner.topRight, const Offset(1024 - kSimpleInset, kSimpleInset));

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

  testWidgets('while a plan runs and the camera is idle, the status pill says which step is running', (tester) async {
    final container = await _pump(tester, const Size(1024, 768),
        plan: const SimpleRun(sequenceId: 'seq-1', started: true, targets: [
          SimpleTarget(name: 'M31', steps: [FilterPlanStep(filterName: 'L', exposureSeconds: 120, frameCount: 30)]),
        ]),
        runState: const SequenceRunStateInfo(
            sequenceId: 'seq-1', state: SequenceRunState.running, currentInstructionDescription: 'Cooling camera to -10 °C'));
    await tester.pump();
    String pill() => find
        .descendant(of: find.byType(SimpleStatusPill), matching: find.byType(RichText))
        .evaluate()
        .map((e) => (e.widget as RichText).text.toPlainText())
        .join();
    expect(pill(), contains('Plan'));
    expect(pill(), contains('Cooling camera to -10 °C'));
    // Screen readers hear the step when it changes.
    expect(tester.getSemantics(find.byType(SimpleStatusPill)), matchesSemantics(
        label: 'Plan, Cooling camera to -10 °C', isLiveRegion: true));
    // A frame in hand: the camera's own countdown wins.
    container.read(captureProgressProvider.notifier).beginExposing(const Duration(seconds: 5));
    await tester.pump();
    expect(pill(), contains('Taking a frame'));
    container.read(captureProgressProvider.notifier).reset();
    await tester.pump(const Duration(seconds: 1));
  });

  testWidgets('a running step that reports how far it has got shows a bar along the pill\'s foot', (tester) async {
    await _pump(tester, const Size(1024, 768),
        plan: const SimpleRun(sequenceId: 'seq-1', started: true, targets: [
          SimpleTarget(name: 'M31', steps: [FilterPlanStep(filterName: 'L', exposureSeconds: 120, frameCount: 30)]),
        ]),
        runState: const SequenceRunStateInfo(
            sequenceId: 'seq-1',
            state: SequenceRunState.running,
            currentInstructionDescription: 'Cooling camera to -10 °C',
            currentInstructionProgress: 0.4));
    await tester.pump();
    final bar = find.descendant(of: find.byType(SimpleStatusPill), matching: find.byType(LinearProgressIndicator));
    expect(bar, findsOneWidget);
    expect(tester.widget<LinearProgressIndicator>(bar).value, 0.4);
    final pill = tester.getRect(find.descendant(of: find.byType(SimpleStatusPill), matching: find.byType(SimplePanel)));
    expect(pill.height, kSimpleStatusHeight, reason: 'the bar sits inside the pill, which keeps its height');
    expect(tester.getRect(bar).bottom, lessThanOrEqualTo(pill.bottom));
    expect(tester.getRect(bar).top, greaterThan(pill.center.dy), reason: 'along its foot');
    // The words and the bar sit together in the middle: no empty band above the words.
    final words = tester.getRect(find.descendant(of: find.byType(SimpleStatusPill), matching: find.textContaining('Cooling camera')));
    expect((words.top - pill.top) - (pill.bottom - tester.getRect(bar).bottom), lessThan(3),
        reason: 'words $words, bar ${tester.getRect(bar)}, pill $pill');
  });

  testWidgets('a cooler turned on by hand shows Cooling in the pill, with a bar for how far it has got', (tester) async {
    final camera = _CoolingCamera(-2);
    await _pump(tester, const Size(1024, 768), linkUp: true, gear: [cameraStatusProvider.overrideWith(() => camera)]);
    await tester.pump();
    final pill = find.byType(SimpleStatusPill);
    expect(find.descendant(of: pill, matching: find.textContaining('Cooling')), findsOneWidget);
    expect(find.descendant(of: pill, matching: find.textContaining('−2 °C → −10 °C')), findsOneWidget);
    final bar = find.descendant(of: pill, matching: find.byType(LinearProgressIndicator));
    expect(tester.widget<LinearProgressIndicator>(bar).value, 0, reason: 'starts where it was when seen first');
    camera.set(-6);
    await tester.pump();
    expect(tester.widget<LinearProgressIndicator>(bar).value, closeTo(0.5, 0.01), reason: 'halfway from −2 to −10');
    camera.set(-9.8);
    await tester.pump();
    expect(find.descendant(of: pill, matching: find.textContaining('Cooling')), findsNothing,
        reason: 'at the target (within half a degree) the pill has nothing to say');
  });

  testWidgets('a scheduled plan waiting on the rig: the pill says when it starts', (tester) async {
    final at = DateTime.now().add(const Duration(hours: 2));
    await _pump(tester, const Size(1024, 768),
        plan: SimpleRun(sequenceId: 'seq-1', started: true, startAt: at, targets: const [
          SimpleTarget(name: 'M31', steps: [FilterPlanStep(filterName: 'L', exposureSeconds: 120, frameCount: 30)]),
        ]),
        runState: const SequenceRunStateInfo(
            sequenceId: 'seq-1', state: SequenceRunState.running, currentInstructionDescription: 'Wait for time'));
    await tester.pump();
    final hhmm = '${at.hour.toString().padLeft(2, '0')}:${at.minute.toString().padLeft(2, '0')}';
    expect(
        find.descendant(of: find.byType(SimpleStatusPill), matching: find.textContaining('Starts at $hhmm')),
        findsOneWidget);
  });

  for (final size in const [Size(1024, 768), Size(768, 1024)]) {
    testWidgets('at $size: the safety alarm covers the whole screen, no dialog, a red circled "!"; Silence closes it',
        (tester) async {
      final container = await _pump(tester, size, manualAlarm: true);
      await _frame(tester, container);
      // Even from the immersive view.
      await tester.tapAt(Offset(size.width / 2, size.height / 2));
      await tester.pump();
      (container.read(safetyAlarmProvider.notifier) as _ManualAlarm).raise('EMERGENCY STOP triggered');
      await tester.pump();
      final alarm = find.byKey(const Key('simple-safety-alarm'));
      expect(alarm, findsOneWidget);
      expect(find.byType(AlertDialog), findsNothing, reason: 'no popup in Simple Mode');
      expect(tester.getRect(alarm), Offset.zero & size, reason: 'full screen');
      expect(find.descendant(of: alarm, matching: find.text('SAFETY ALERT')), findsOneWidget);
      expect(find.descendant(of: alarm, matching: find.text('EMERGENCY STOP triggered')), findsOneWidget);
      expect(find.descendant(of: alarm, matching: find.byIcon(Icons.error)), findsOneWidget);
      expect(find.descendant(of: alarm, matching: find.byIcon(Icons.notifications_active)), findsNothing,
          reason: 'not the bell');
      // The keyboard lands on Silence; screen readers read only the alarm.
      final focused = FocusManager.instance.primaryFocus?.context;
      expect(focused, isNotNull);
      expect(find.descendant(of: find.byKey(const Key('simple-safety-silence')), matching: find.byElementPredicate((e) => e == focused)),
          findsOneWidget, reason: 'Silence has the focus');
      expect(find.byType(BlockSemantics), findsWidgets);
      // Tab and Shift+Tab stay on the alarm; nothing behind the veil takes the focus.
      for (final back in [false, false, false, true, true]) {
        if (back) await tester.sendKeyDownEvent(LogicalKeyboardKey.shiftLeft);
        await tester.sendKeyEvent(LogicalKeyboardKey.tab);
        if (back) await tester.sendKeyUpEvent(LogicalKeyboardKey.shiftLeft);
        await tester.pump();
        final now = FocusManager.instance.primaryFocus?.context;
        expect(now, isNotNull);
        expect(find.descendant(of: alarm, matching: find.byElementPredicate((e) => e == now)), findsOneWidget,
            reason: 'focus left the alarm for ${now?.widget}');
      }
      await tester.tap(find.byKey(const Key('simple-safety-silence')));
      await tester.pump();
      expect(alarm, findsNothing);
    });
  }

  testWidgets('an emergency stop pressed on this device: no full screen and no tone; other alarms still show',
      (tester) async {
    final container = await _pump(tester, const Size(1024, 768), manualAlarm: true);
    container.read(localEmergencyStopProvider.notifier).pressed();
    final alarm = container.read(safetyAlarmProvider.notifier) as _ManualAlarm;
    alarm.raise('EMERGENCY STOP triggered');
    await tester.pump();
    expect(find.byKey(const Key('simple-safety-alarm')), findsNothing, reason: 'you pressed it here');
    expect(container.read(safetyAlarmProvider).pending, isFalse, reason: 'silenced before the tone');
    alarm.raise('Safety monitor: unsafe (rain)');
    await tester.pump();
    expect(find.byKey(const Key('simple-safety-alarm')), findsOneWidget, reason: 'weather still alarms');
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

  testWidgets('the strip has Night mode and Settings, and nothing that leads to the full UI', (tester) async {
    final c = await _pump(tester, const Size(1024, 768));
    expect(find.byKey(const Key('simple-more')), findsNothing, reason: 'nothing hides under ⋯');
    expect(find.byTooltip('Search settings'), findsNothing);
    expect(find.byTooltip('Show all options'), findsNothing);
    expect(tester.getCenter(find.byTooltip('Settings')).dx, greaterThan(tester.getCenter(find.byTooltip('Night mode')).dx));
    // ⌘K (the full UI's search) does nothing here.
    await tester.sendKeyDownEvent(LogicalKeyboardKey.metaLeft);
    await tester.sendKeyEvent(LogicalKeyboardKey.keyK);
    await tester.sendKeyUpEvent(LogicalKeyboardKey.metaLeft);
    await tester.pumpAndSettle();
    expect(find.byType(Dialog), findsNothing);
    expect(c.read(simpleModeProvider).value, isTrue);
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
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('simple-menu')), findsOneWidget, reason: 'Simple Mode\'s Settings');
    expect(container.read(simpleModeProvider).value, isTrue);
  });

  testWidgets('text and outlined button labels read at 4.5:1 or better on a panel and on a tile (WCAG 2.2 AA)',
      (tester) async {
    await _pump(tester, const Size(1024, 768));
    final context = tester.element(find.byKey(const Key('simple-mode')));
    double contrast(Color a, Color b) {
      final (l1, l2) = (a.computeLuminance(), b.computeLuminance());
      return (math.max(l1, l2) + .05) / (math.min(l1, l2) + .05);
    }
    // The panel scrim over a black sky, and a tile on it.
    final panel = Color.alphaBlend(AraColors.overlayScrim, Colors.black);
    for (final (name, style) in [
      ('TextButton', TextButtonTheme.of(context).style!),
      ('OutlinedButton', OutlinedButtonTheme.of(context).style!),
    ]) {
      final fg = style.foregroundColor!.resolve({})!;
      expect(contrast(fg, panel), greaterThanOrEqualTo(4.5), reason: '$name on a panel');
      expect(contrast(fg, AraColors.tileFill), greaterThanOrEqualTo(4.5), reason: '$name on a tile');
    }
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

  test('the exposure list stays inside what the camera reports', () {
    expect(exposurePresets(minSec: 1, maxSec: 60), allOf(contains(const Duration(seconds: 1)),
        contains(const Duration(seconds: 60)), isNot(contains(const Duration(milliseconds: 500))),
        isNot(contains(const Duration(seconds: 120)))));
  });

  test('the router picks the shell from the pref, and both shells maximize the window', () {
    expect(shellFor(simple: true), isA<SimpleShell>());
    expect(shellFor(simple: false), isA<AppShell>());
    expect(isShell(const SimpleShell()), isTrue);
    expect(isShell(const AppShell()), isTrue);
    expect(isShell(const SizedBox()), isFalse);
  });
}
