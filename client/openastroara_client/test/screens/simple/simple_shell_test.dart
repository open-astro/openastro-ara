import 'dart:math' as math;
import 'dart:ui' show ImageFilter;

import 'package:flutter/cupertino.dart';
import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart' show RenderParagraph;
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/album/sky_album_screen.dart';
import 'package:openastroara/l10n/l10n.dart';
import 'package:openastroara/models/sequence/imaging_run_body.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/state/alarm/safety_alarm_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/screens/simple/simple_plan_panel.dart';
import 'package:openastroara/state/sky_atlas/planetarium_selection_state.dart';
import 'package:openastroara/main.dart' show isShell, shellFor;
import 'package:openastroara/models/frame_histogram.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/state/imaging/live_view_state.dart';
import 'package:openastroara/state/imaging/live_view_frame_state.dart';
import 'package:openastroara/services/live_view_api.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/screens/simple/simple_search.dart';
import 'package:openastroara/screens/simple/simple_device_windows.dart' show simpleMountMotionProvider, simpleSwitchSettingProvider;
import 'package:openastroara/models/switch_device.dart';
import 'package:openastroara/state/equipment/switch_state.dart';
import 'package:openastroara/models/autofocus_run.dart';
import 'package:openastroara/state/focus/autofocus_live_state.dart';
import 'package:openastroara/state/focus/bahtinov_focus_state.dart';
import 'package:openastroara/models/bahtinov_focus.dart';
import 'package:openastroara/state/focus/guide_focus_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/widgets/status_indicator.dart' show StatusBlink, StatusDot, StatusLevel;
import 'package:openastroara/widgets/equipment/equipment_status_chip.dart' show TopEquipmentChips;
import 'package:openastroara/widgets/equipment/guider_chip.dart' show guiderStatusLevel;
import 'package:openastroara/state/app_locale_state.dart';
import 'package:openastroara/services/equipment_driver_api.dart';
import 'package:openastroara/services/guider_api.dart' show GuiderClient;
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/services/dso_catalog_service.dart';
import 'package:openastroara/state/sky_atlas/dso_catalog_state.dart';
import 'package:openastroara/services/time_sync_api.dart';
import 'package:openastroara/services/client_gps_prefs_service.dart';
import 'package:openastroara/state/client_gps_state.dart' show clientGpsPrefsServiceProvider;
import 'package:openastroara/state/time_sync_state.dart';
import 'package:openastroara/models/stats/server_internal_state.dart';
import 'package:openastroara/services/storage_space_api.dart';
import 'package:openastroara/state/stats/server_internal_state_state.dart';
import 'package:openastroara/screens/app_shell.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/screens/simple/simple_focus_panel.dart';
import 'package:openastroara/screens/simple/simple_guide_panel.dart';
import 'package:openastroara/screens/simple/simple_polar_panel.dart';
import 'package:openastroara/screens/simple/simple_preview_panel.dart';
import 'package:openastroara/screens/simple/simple_shell.dart';
import 'package:openastroara/state/simple_night_setup_state.dart';
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
import 'package:openastroara/models/filter_wheel_status.dart';
import 'package:openastroara/models/focuser_status.dart';
import 'package:openastroara/models/mount_status.dart';
import 'package:openastroara/state/equipment/camera_state.dart';
import 'package:openastroara/state/equipment/equipment_device_state.dart';
import 'package:openastroara/state/equipment/filter_wheel_state.dart';
import 'package:openastroara/state/equipment/rotator_state.dart';
import 'package:openastroara/models/rotator_status.dart';
import 'package:openastroara/models/flat_panel_status.dart';
import 'package:openastroara/state/equipment/flat_panel_state.dart';
import 'package:openastroara/models/weather_status.dart';
import 'package:openastroara/state/equipment/weather_state.dart';
import 'package:openastroara/state/equipment/focuser_state.dart';
import 'package:openastroara/state/equipment/mount_state.dart';
import 'package:openastroara/widgets/equipment/equipment_glyphs.dart';
import 'package:openastroara/state/settings/settings_nav.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/theme/ara_metrics.dart' show AraBlur;
import 'package:openastroara/screens/simple/simple_safety_alarm.dart';
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

/// A frame's histogram: the bottom row's right half.
final _histogram = FrameHistogram(
    bins: List<int>.generate(256, (i) => i < 40 ? 1000 - (i - 20).abs() * 40 : 0),
    minAdu: 0, minCount: 0, maxAdu: 65535, maxCount: 1000, meanAdu: 900, stdDev: 50, median: 880, mad: 30,
    lowClipFraction: 0, highClipFraction: 0, width: 100, height: 100, bitDepth: 16);

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
    // The app shows Simple Mode's alarm over every screen while Simple Mode is on.
    if (manualAlarm) simpleModeActiveProvider.overrideWithValue(true),
  ]);
  addTearDown(container.dispose);
  await container.read(simpleModeProvider.future);
  if (feature) await container.read(simpleModeFeatureProvider.future);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: container,
    child: MaterialApp(
      builder: (context, child) => SimpleSafetyAlarmLayer(child: child!),
      home: const SimpleShell(planetarium: SizedBox(key: Key('sky'))),
    ),
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

  /// What the focuser reports next ("moving", "idle").
  void report(String runtime) =>
      state = AsyncData(FocuserStatus.fromJson({..._wire('EAF', state0), 'runtime': {'state': runtime, 'position': 12000}}));
}

class _FilterWheel extends FilterWheelNotifier with _Calls<FilterWheelStatus> {
  _FilterWheel(this.state0, {this.slots = 0});
  final EquipmentConnectionState state0;
  final int slots;
  Map<String, dynamic> _json(Map<String, dynamic> runtime, {bool manual = false}) => {
        ..._wire('EFW', state0),
        'manual': manual,
        'slots': [for (var i = 0; i < slots; i++) {'position': i, 'name': 'F$i'}],
        'runtime': runtime,
      };
  @override
  Future<FilterWheelStatus?> build() async => FilterWheelStatus.fromJson(_json({'state': 'idle', 'current_slot': 0}));

  /// What the wheel reports next ("moving", "awaiting_user", "idle").
  void report(Map<String, dynamic> runtime, {bool manual = false}) =>
      state = AsyncData(FilterWheelStatus.fromJson(_json(runtime, manual: manual)));
}

class _Rotator extends RotatorNotifier with _Calls<RotatorStatus> {
  _Rotator(this.state0);
  final EquipmentConnectionState state0;
  Map<String, dynamic> _json(String runtime) => {
        ..._wire('Falcon', state0),
        'runtime': {'state': runtime, 'sky_angle_deg': 92, 'mechanical_angle_deg': 92},
      };
  @override
  Future<RotatorStatus?> build() async => RotatorStatus.fromJson(_json('idle'));

  /// What the rotator reports next ("moving", "idle").
  void report(String runtime) => state = AsyncData(RotatorStatus.fromJson(_json(runtime)));
}

/// The rig's switch devices, recording Connect and Disconnect.
class _Switches extends SwitchListNotifier {
  _Switches(this.devices);
  final List<SwitchDevice> devices;
  final calls = <String>[];
  @override
  Future<List<SwitchDevice>> build() async => devices;
  @override
  Future<void> refresh([client]) async {}
  @override
  Future<bool> reconnectDevice(String deviceId) async {
    calls.add('connect $deviceId');
    return true;
  }

  @override
  Future<bool> disconnect(String deviceId) async {
    calls.add('disconnect $deviceId');
    return true;
  }
}

SwitchDevice _switch(String id, String name, SwitchConnectionState state) => SwitchDevice(
    deviceId: id,
    alpacaDeviceNumber: 0,
    name: name,
    connectionState: state,
    ports: const [SwitchPort(id: 0, name: 'Dew 1', value: 0, min: 0, max: 100, canWrite: true)]);

class _Weather extends WeatherNotifier with _Calls<WeatherStatus> {
  _Weather(this.state0);
  final EquipmentConnectionState state0;
  @override
  Future<WeatherStatus?> build() async =>
      WeatherStatus.fromJson({..._wire('CloudWatcher', state0), 'temperature_c': 8.4});
}

class _FlatPanel extends FlatPanelNotifier with _Calls<FlatPanelStatus> {
  _FlatPanel(this.state0);
  final EquipmentConnectionState state0;
  Map<String, dynamic> _json(String runtime, {bool warming = false}) => {
        ..._wire('Flip-Flat', state0),
        'runtime': {'state': runtime, 'max_brightness': 255, 'light_warming': warming},
      };
  @override
  Future<FlatPanelStatus?> build() async => FlatPanelStatus.fromJson(_json('cover_closed'));

  /// What the panel reports next ("cover_moving", "cover_closed", "light_on").
  void report(String runtime, {bool warming = false}) =>
      state = AsyncData(FlatPanelStatus.fromJson(_json(runtime, warming: warming)));
}

class _Mount extends MountNotifier with _Calls<MountStatus> {
  _Mount(this.state0);
  final EquipmentConnectionState state0;
  @override
  Future<MountStatus?> build() async => MountStatus.fromJson(_wire('AM5', state0));
}

/// A mount whose connect the rig refuses (the device notifiers throw why).
class _RefusingMount extends _Mount {
  _RefusingMount() : super(EquipmentConnectionState.disconnected);
  @override
  Future<bool> reconnect() async => throw StateError('The mount did not answer');
}

class _GpsPrefs extends ClientGpsPrefsService {
  _GpsPrefs() : super(supportDir: () async => throw UnsupportedError('unused'));
  @override
  Future<ClientGpsPrefs> load() async => const ClientGpsPrefs();
  @override
  Future<void> save(ClientGpsPrefs prefs) async {}
}

class _Clock implements TimeSyncClient {
  final pushed = <DateTime>[];
  @override
  Future<TimeSyncState> getState() async =>
      const TimeSyncState(synced: true, location: TimeSyncLocation(lat: -37.2, lng: 174.88, alt: 71));
  @override
  Future<void> pushClientTime(DateTime utcNow) async => pushed.add(utcNow);
  @override
  dynamic noSuchMethod(Invocation i) => super.noSuchMethod(i);
}

class _GuiderUp extends GuiderStatusNotifier {
  @override
  Future<GuiderStatus?> build() async =>
      GuiderStatus.fromJson(const {'device_id': 'g', 'name': 'PHD2', 'state': 'connected', 'runtime': {'state': 'guiding'}});
  // No server here: Simple Mode's 2 s re-read keeps what the rig last said.
  @override
  Future<void> refresh([GuiderClient? client]) async {}
}

/// A polar alignment adjusting; [fail] ends it with why.
/// A daemon whose Live View loop is still running (the app died mid-Loop).
class _LoopOnRig implements LiveViewClient {
  final calls = <String>[];
  @override
  Future<bool> isActive() async => !calls.contains('stop');
  @override
  Future<void> start({required double exposureSec, int? gain, int binX = 2, int binY = 2}) async => calls.add('start');
  @override
  Future<void> stop() async => calls.add('stop');
  @override
  Future<LiveFrame?> fetchFrame() async => null;
  @override
  void close() {}
}

class _PolarRun extends PolarAlignLiveNotifier {
  @override
  PolarAlignLive build() => const PolarAlignLive(phase: PolarAlignStates.adjusting);
  void fail(String why) => state = PolarAlignLive(phase: PolarAlignStates.failed, errorMessage: why);
}

/// A connected guider in [runtime], with a calibration leg and an RMS; [at] moves it on.
class _GuiderAt extends GuiderStatusNotifier {
  _GuiderAt(this.runtime);
  final String runtime;
  static GuiderStatus _wire(String r) => GuiderStatus.fromJson({
        'device_id': 'g', 'name': 'PHD2', 'state': 'connected',
        'runtime': {'state': r, 'calibration_direction': 'West', 'calibration_step': 3, 'rms_total_arcsec': 0.62},
      });
  @override
  Future<GuiderStatus?> build() async => _wire(runtime);
  void at(String r) => state = AsyncData(_wire(r));

  /// What the rig answers on the next read.
  late String rig = runtime;
  int refreshes = 0;
  @override
  Future<void> refresh([GuiderClient? client]) async {
    refreshes++;
    state = AsyncData(_wire(rig));
  }
}

class _GuiderCalls extends _GuiderUp {
  final calls = <String>[];
  @override
  Future<void> disconnect() async => calls.add('disconnect');
  @override
  Future<void> connect({String? host, int? port}) async => calls.add('connect');
}

/// A guider mid-connect.
class _GuiderConnecting extends _GuiderCalls {
  @override
  Future<GuiderStatus?> build() async =>
      GuiderStatus.fromJson(const {'device_id': 'g', 'name': 'PHD2', 'state': 'connecting'});
}

/// A guider mid-connect that [up] brings to connected.
class _GuiderComingUp extends _GuiderConnecting {
  void up() => state = AsyncData(GuiderStatus.fromJson(const {'device_id': 'g', 'name': 'PHD2', 'state': 'connected'}));
}

/// The rig's guide optics: unset for the first [unsetReads] reads, then filled (the rig fills them a
/// few round trips after the guider connects).
class _GuideOpticsApi extends ProfileApi {
  _GuideOpticsApi(this.unsetReads) : super(const AraServer(hostname: 'test', port: 1));
  int unsetReads;
  int reads = 0;
  @override
  Future<Phd2Settings> getPhd2Settings() async => ++reads > unsetReads
      ? const Phd2Settings(guideFocalLength: 128, guidePixelSize: 3.75)
      : const Phd2Settings();
  @override
  Future<OpticsSettings> getOptics() async => const OpticsSettings();
}

/// The rig's guider section as a store: reads return it, saves replace it.
class _Phd2StoreApi extends ProfileApi {
  _Phd2StoreApi() : super(const AraServer(hostname: 'test', port: 1));
  Phd2Settings stored = const Phd2Settings();
  final saved = <Phd2Settings>[];
  @override
  Future<Phd2Settings> getPhd2Settings() async => stored;
  @override
  Future<Phd2Settings> putPhd2Settings(Phd2Settings s) async {
    saved.add(s);
    return stored = s;
  }
  @override
  Future<OpticsSettings> getOptics() async => const OpticsSettings();
}

/// The rig's answer once the guider is disconnected: no guider (404).
class _GuiderGone extends _GuiderCalls {
  @override
  Future<GuiderStatus?> build() async => null;
}

class _LiveFrame extends AutofocusLiveNotifier {
  @override
  AutofocusLive build() => AutofocusLive(
      run: const AutofocusRun(state: AutofocusRunStates.complete, finalHfr: 1.01), frame: Uint8List.fromList(_png));
}

/// The daemon's run record as the real notifier folds it: idle until the
/// first read, then each snapshot through [applyRunSnapshot].
class _LiveSnapshots extends AutofocusLiveNotifier {
  @override
  AutofocusLive build() => AutofocusLive.idle;
  @override
  Future<void> refresh() async {}
  void snapshot(AutofocusRun run) => state = applyRunSnapshot(state, run);
}

/// A finished Smart Focus with a weak fit and its frame: the tallest Auto panel.
class _LiveWeak extends AutofocusLiveNotifier {
  @override
  AutofocusLive build() => AutofocusLive(
      run: AutofocusRun(
        state: AutofocusRunStates.complete,
        finalHfr: 1.01,
        finalPosition: 29463,
        finalStars: 299,
        probes: [
          for (var i = 0; i < 7; i++)
            AutofocusProbe(
                index: i, phase: AutofocusProbePhases.fine, position: 29300 + i * 50,
                hfr: [4.2, 3.4, 2.8, 1.4, 2.6, 3.3, 4.8][i], stars: 40, kept: true),
        ],
        fit: const AutofocusFit(
            algorithm: 'hyperbolic', rSquared: 0.554, bestPosition: 29463, predictedHfr: 1.7,
            withinSampledRange: true, curve: []),
      ),
      frame: Uint8List.fromList(_png));
}

/// A Bahtinov readout running with a frame: the tallest Manual panel.
class _BahtinovReading extends BahtinovFocusNotifier {
  @override
  BahtinovFocusLive build() {
    final recent = [
      for (var i = 0; i < 6; i++)
        BahtinovSample(seq: i + 1, detected: true, offsetPx: i < 3 ? 3.0 : 2.0, withinZone: false),
    ];
    return BahtinovFocusLive(
      status: BahtinovFocusStatus(
          active: true, state: BahtinovFocusStates.running, exposureSec: 2, seq: 6, latest: recent.last,
          recent: recent, zonePx: 0.3, zoneFromOptics: false, hasFrame: true),
      frame: Uint8List.fromList(_png),
      frameSeq: 6,
    );
  }
}

/// A Bahtinov readout stopped with a measurement, not finished.
class _BahtinovMeasured extends BahtinovFocusNotifier {
  int finishes = 0;
  @override
  BahtinovFocusLive build() {
    const sample = BahtinovSample(seq: 1, detected: true, offsetPx: 1.0, withinZone: false);
    return const BahtinovFocusLive(
      status: BahtinovFocusStatus(
          active: false, state: BahtinovFocusStates.stopped, exposureSec: 2, seq: 1, latest: sample,
          recent: [sample], zonePx: 0.3, zoneFromOptics: false, hasFrame: false),
    );
  }
  @override
  Future<void> finish() async {
    finishes++;
    state = state.copyWith(finished: true);
  }
}

class _GuideFrame extends GuideFocusNotifier {
  @override
  GuideFocusLive build() => GuideFocusLive(frame: Uint8List.fromList(_png), frameSeq: 1);
}

class _ManualPick extends MainFocusMethodNotifier {
  @override
  MainFocusMethod? build() => MainFocusMethod.bahtinov;
}

class _AutoPick extends MainFocusMethodNotifier {
  @override
  MainFocusMethod? build() => MainFocusMethod.autofocus;
}

// A 1x1 PNG.
const _png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0, 31, 21, 196, 137, 0, 0, 0, 13, 73, 68, 65, 84, 120, 156, 99, 248, 15, 4, 0, 9, 251, 3, 253, 227, 85, 242, 156, 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130];

class _ParkingMount extends MountNotifier {
  static Map<String, dynamic> _json(bool parked, String runtime) => {
        ..._wire('AM5', EquipmentConnectionState.connected),
        'capabilities': {'can_park': true, 'can_unpark': true},
        'runtime': {'state': runtime, 'parked': parked},
      };
  @override
  Future<MountStatus?> build() async => MountStatus.fromJson(_json(false, 'tracking'));
  void set({required bool parked, required String runtime}) => state = AsyncData(MountStatus.fromJson(_json(parked, runtime)));
  @override
  Future<bool> park() async => true;
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
  Future<bool> create({bool first = false, DateTime? startsAt}) async {
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

const _queuedM31 = SimpleRun(sequenceId: 'seq-1', targets: [
  SimpleTarget(name: 'M31', steps: [FilterPlanStep(filterName: 'L', exposureSeconds: 120, frameCount: 30)]),
]);

/// Plan screen, queue panel, then the edit sheet on the queued M31.
Future<void> _openEditOfM31(WidgetTester tester) async {
  await tester.tap(find.byKey(const Key('simple-mode')));
  await tester.pump();
  await tester.tap(find.text('Plan').first);
  await tester.pump();
  await tester.tap(find.descendant(of: find.byKey(const Key('simple-run-tracker')), matching: find.text('M31')));
  await tester.pump();
  await tester.tap(find.byKey(const Key('simple-run-edit-0')));
  await tester.pump();
}

Future<void> _frame(WidgetTester tester, ProviderContainer c) async {
  c.read(lastCapturedFrameIdProvider.notifier).set('frame-1');
  await tester.pump();
}

/// Starts Guided Run from the modes menu (the Start banner went, user 2026-11-04).
Future<void> _startGuidedRun(WidgetTester tester) async {
  await tester.tap(find.byKey(const Key('simple-mode')));
  await tester.pump();
  await tester.tap(find.byKey(const Key('simple-mode-nightSetup')));
  await tester.pump();
}

Rect _panelOf(WidgetTester tester, Finder f) =>
    tester.getRect(find.ancestor(of: f, matching: find.byType(SimplePanel)).first);

/// What the settings chip should read for the current exposure parameters.
String _summary(ProviderContainer c) {
  final p = c.read(exposureControllerProvider);
  return '${formatExposure(p.exposure)} · ${p.gain}';
}


/// Tabs (at most [max] times) until keyboard focus is inside the widget keyed [k].
Future<bool> _tabTo(WidgetTester tester, Key k, {int max = 40}) async {
  bool inside() {
    var found = false;
    final ctx = FocusManager.instance.primaryFocus?.context;
    if (ctx == null) return false;
    if (ctx.widget.key == k) return true;
    ctx.visitAncestorElements((e) {
      if (e.widget.key == k) found = true;
      return !found;
    });
    return found;
  }
  for (var i = 0; i < max && !inside(); i++) {
    await tester.sendKeyEvent(LogicalKeyboardKey.tab);
    await tester.pump();
  }
  return inside();
}


/// Tabs (at most [max] times) until the focused node is the one labelled [label].
Future<bool> _tabToLabel(WidgetTester tester, String label, {int max = 60}) async {
  for (var i = 0; i < max && FocusManager.instance.primaryFocus?.debugLabel != label; i++) {
    await tester.sendKeyEvent(LogicalKeyboardKey.tab);
    await tester.pump();
  }
  return FocusManager.instance.primaryFocus?.debugLabel == label;
}

void main() {
  for (final size in const [Size(1024, 768), Size(1024, 744), Size(1470, 854)]) {
    testWidgets('at $size: compact right rail (mode, settings, shutter), Stop below, left rail (Sky, Info, Library, Sky Album)',
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
      for (final key in ['simple-info', 'simple-library', 'simple-album']) {
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
    expect(find.byKey(const Key('simple-wallpaper')), findsOneWidget, reason: 'the wallpaper until there is a frame (#1602)');
    // Sky shows tonight's sky over the wallpaper, so it is a working button.
    expect(tester.getSemantics(find.byKey(const Key('simple-sky'))), isSemantics(hasEnabledState: true, isEnabled: true));
  });

  testWidgets('the mode name opens the modes in order, Night Setup first; picking one closes them', (tester) async {
    final container = await _pump(tester, const Size(1024, 768));
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    final modes = ['Night Setup', 'Preview', 'Focus', 'Polar align', 'Plan'];
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

  testWidgets("Focus: Measured stars On fills the screen with the run's frame, circles and all", (tester) async {
    final container = await _pump(tester, const Size(1024, 768), gear: [
      autofocusLiveProvider.overrideWith(() => _LiveFrame()),
      mainFocusMethodProvider.overrideWith(_AutoPick.new),
    ]);
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.text('Focus'));
    await tester.pump();
    expect(find.byKey(const Key('simple-focus-frame')), findsNothing, reason: 'off to start');
    container.read(simpleFocusStarsShownProvider.notifier).set(true);
    await tester.pump();
    expect(find.byKey(const Key('simple-focus-frame')), findsOneWidget);
  });

  for (final (name, gear) in [
    ('Auto, a finished weak-fit run', <Override>[
      autofocusLiveProvider.overrideWith(_LiveWeak.new),
      focuserProvider.overrideWith(() => _Focuser(EquipmentConnectionState.connected)),
    ]),
    ('Manual, the Bahtinov readout with a frame', <Override>[
      bahtinovFocusProvider.overrideWith(_BahtinovReading.new),
      mainFocusMethodProvider.overrideWith(_ManualPick.new),
    ]),
  ]) {
    testWidgets('the Focus panel fits on an iPad without scrolling: $name', (tester) async {
      await _pump(tester, const Size(1024, 768), gear: gear);
      await tester.tap(find.byKey(const Key('simple-mode')));
      await tester.pump();
      await tester.tap(find.text('Focus'));
      await tester.pump();
      await tester.tap(find.byKey(const Key('simple-settings')));
      await tester.pump();
      final scroll = find.ancestor(of: find.byType(SimpleFocusPanel), matching: find.byType(Scrollable)).first;
      expect(tester.state<ScrollableState>(scroll).position.maxScrollExtent, 0, reason: 'everything in view');
    });
  }

  testWidgets("Focus on the guide camera fills the screen with the guide camera's frames", (tester) async {
    final container = await _pump(tester, const Size(1024, 768), gear: [
      guideFocusProvider.overrideWith(_GuideFrame.new),
    ]);
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.text('Focus'));
    await tester.pump();
    expect(find.byKey(const Key('simple-focus-frame')), findsNothing, reason: 'the main telescope to start');
    container.read(simpleFocusScopeProvider.notifier).set(SimpleFocusScope.guide);
    await tester.pump();
    expect(find.byKey(const Key('simple-focus-frame')), findsOneWidget);
  });

  testWidgets('leaving Focus with a Bahtinov measurement not finished asks for the mask in a pill (ALP-4982)',
      (tester) async {
    // #1486 review: DESIGN.md says leaving Manual before Finish asks; leaving the mode didn't.
    final b = _BahtinovMeasured();
    final container = await _pump(tester, const Size(1024, 768), gear: [
      mainFocusMethodProvider.overrideWith(_ManualPick.new),
      bahtinovFocusProvider.overrideWith(() => b),
    ]);
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.text('Focus'));
    await tester.pump();
    expect(find.text('Take the Bahtinov mask off'), findsNothing);
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.text('Preview').first);
    await tester.pump();
    await tester.pump();
    final pill = container.read(simpleMessagesProvider).single;
    expect((pill.kind, pill.title), (SimpleMessageKind.question, 'Take the Bahtinov mask off'));
    pill.actions.last.onPressed(); // Mask removed
    expect(b.finishes, 1);
  });

  testWidgets('with no device connected the top bar says so, and a tap opens Settings › Devices', (tester) async {
    // iPad, 2026-11-04: an empty top bar said nothing.
    await _pump(tester, const Size(1024, 768), linkUp: true);
    final note = find.byKey(const Key('simple-no-devices'));
    expect(find.descendant(of: note, matching: find.text('No devices connected')), findsOneWidget);
    await tester.tap(note);
    await tester.pump();
    expect(find.byKey(const Key('simple-gear')), findsOneWidget, reason: 'Settings opens on Devices');
  });

  // ALP-4998: Ara keeps its last run until it restarts, so a launch (or a
  // reconnect) reads an old failure as if it had just happened.
  testWidgets("an autofocus that failed before this launch posts no pill, so × and a restart clear it (ALP-4998)",
      (tester) async {
    final live = _LiveSnapshots();
    final container = await _pump(tester, const Size(1024, 768), gear: [autofocusLiveProvider.overrideWith(() => live)]);
    live.snapshot(const AutofocusRun(state: AutofocusRunStates.failed, reason: 'focuser error'));
    await tester.pump();
    expect(container.read(simpleMessagesProvider), isEmpty);
  });

  testWidgets('an autofocus that fails while the app watches still says so in a pill', (tester) async {
    final live = _LiveSnapshots();
    final container = await _pump(tester, const Size(1024, 768), gear: [autofocusLiveProvider.overrideWith(() => live)]);
    live.snapshot(const AutofocusRun(state: AutofocusRunStates.idle));
    await tester.pump();
    live.snapshot(const AutofocusRun(state: AutofocusRunStates.running));
    await tester.pump();
    live.snapshot(const AutofocusRun(state: AutofocusRunStates.failed, reason: 'focuser error'));
    await tester.pump();
    expect(container.read(simpleMessagesProvider).single.detail, 'Focuser error.');
  });

  testWidgets('the rig offline: the top bar says Rig not connected', (tester) async {
    await _pump(tester, const Size(1024, 768));
    expect(find.descendant(of: find.byKey(const Key('simple-no-devices')), matching: find.text('Rig not connected')),
        findsOneWidget);
    // Amber: it needs a look. Blue would read as connecting (user, 2026-11-06).
    final text = tester.renderObject<RenderParagraph>(find.text('Rig not connected'));
    expect(text.text.style!.color, AraColors.accentWarning);
  });

  testWidgets('a connected device hides the no-devices note', (tester) async {
    await _pump(tester, const Size(1024, 768), linkUp: true,
        gear: [cameraStatusProvider.overrideWith(() => _Camera(EquipmentConnectionState.connected))]);
    expect(find.byKey(const Key('simple-no-devices')), findsNothing);
  });

  testWidgets("the top bar names Main Camera and Guide Camera, the guide camera's icon a video camera",
      (tester) async {
    // iPad, 2026-11-04: the top bar still showed the guider's target icon after the rename.
    await _pump(tester, const Size(1024, 768), linkUp: true, gear: [
      cameraStatusProvider.overrideWith(() => _Camera(EquipmentConnectionState.connected)),
      guiderStatusProvider.overrideWith(_GuiderUp.new),
    ]);
    final strip = find.byType(TopEquipmentChips);
    expect(find.descendant(of: strip, matching: find.byIcon(Icons.videocam_outlined)), findsOneWidget);
    expect(find.descendant(of: strip, matching: find.byIcon(Icons.track_changes)), findsNothing,
        reason: 'the target is the Guide mode now');
    expect(find.descendant(of: strip, matching: find.byTooltip('Guide Camera')), findsOneWidget);
    expect(find.descendant(of: strip, matching: find.byTooltip('Main Camera')), findsOneWidget);
  });

  testWidgets("Plan's sheet has no title (user, 2026-11-04: the chip already says Plan)", (tester) async {
    await _pump(tester, const Size(1024, 768));
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.byKey(const Key('simple-mode-plan')));
    await tester.pump();
    await tester.tap(find.byKey(const Key('simple-settings')));
    await tester.pump();
    expect(find.byType(SimpleRunSheet), findsOneWidget);
    expect(find.text("Tonight's plan"), findsNothing);
  });

  testWidgets("Plan's sheet and Guided Run's step panels sit centred between the status pill and the bottom row; "
      'other panels stay level with the rail', (tester) async {
    // User, 2026-11-04: their spacing follows the pill's bottom and the histogram's top, equal above and below.
    const pillBottom = kSimpleInset * 2 + kSimpleBarHeight + kSimpleStatusHeight;
    const roomBottom = 768 - kSimpleInset; // no bottom row: the screen's inset
    const centre = (pillBottom + roomBottom) / 2;
    await _pump(tester, const Size(1024, 768));
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.byKey(const Key('simple-mode-nightSetup')));
    await tester.pump();
    await tester.pump(const Duration(seconds: 1));
    final plan = tester.getRect(find.byType(SimpleRunSheet));
    expect(plan.center.dy, closeTo(centre, 1), reason: "Guided Run's Plan step: centred");
    expect(plan.top, greaterThanOrEqualTo(pillBottom + 8));
    await tester.tap(find.byKey(const Key('night-setup-skip')));
    await tester.pump();
    await tester.pump(const Duration(seconds: 1));
    expect(_panelOf(tester, find.text('Fit the mask, then Start')).center.dy, closeTo(centre, 1),
        reason: "Guided Run's Focus step: centred");
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    final rail = _panelOf(tester, find.byKey(const Key('simple-shutter')));
    expect(tester.getRect(find.byKey(const Key('simple-mode-preview'))).top, greaterThan(pillBottom + 58),
        reason: 'the modes menu stays by the rail');
    await tester.tap(find.byKey(const Key('simple-mode-focus')));
    await tester.pump();
    await tester.tap(find.byKey(const Key('simple-settings')));
    await tester.pump();
    expect(_panelOf(tester, find.text('Fit the mask, then Start')).top, rail.top, reason: 'Focus on its own: by the rail');
  });

  testWidgets('with the histogram showing, a mode panel still fits without scrolling (iPad, 2026-11-04)', (tester) async {
    // It was cut off: it started level with the centred rail and stopped above the bottom row. The test font
    // draws the panel shorter than the iPad's, so 1.3x text stands in for it.
    tester.platformDispatcher.textScaleFactorTestValue = 1.3;
    addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
    final c = await _pump(tester, const Size(1024, 768), histogram: _histogram,
        gear: [guiderStatusProvider.overrideWith(_GuiderUp.new)]);
    await _frame(tester, c);
    await tester.pump();
    expect(find.byKey(const Key('simple-histogram')), findsOneWidget, reason: 'precondition: the bottom row shows');
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.byKey(const Key('simple-mode-guide')));
    await tester.pump();
    await tester.tap(find.byKey(const Key('simple-settings')));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 300));
    final panel = tester.getRect(find.byKey(const Key('simple-guide-panel')));
    final scroll = tester.state<ScrollableState>(
        find.ancestor(of: find.byKey(const Key('simple-guide-panel')), matching: find.byType(Scrollable)).first);
    expect(scroll.position.maxScrollExtent, 0, reason: 'everything fits: no scrolling');
    expect(panel.bottom, lessThanOrEqualTo(768 - kSimpleInset));
    expect(panel.top, greaterThanOrEqualTo(kSimpleInset * 2 + kSimpleBarHeight + kSimpleStatusHeight),
        reason: 'under the status pill');
  });

  testWidgets('Guide is a mode: its shutter starts guiding, its chip shows the RMS and opens the Guide panel',
      (tester) async {
    // User, 2026-11-04: guiding moves from the guider's window to a mode on the rail.
    await _pump(tester, const Size(1024, 768), gear: [guiderStatusProvider.overrideWith(_GuiderUp.new)]);
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.byKey(const Key('simple-mode-guide')));
    await tester.pump();
    // The mode holds the guider's settings, so it reads Guider (user, 2026-11-06).
    expect(find.descendant(of: find.byKey(const Key('simple-mode')), matching: find.text('Guider')), findsOneWidget);
    expect(find.byType(SimpleGuideShutter), findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-settings')));
    await tester.pump();
    expect(find.byType(SimpleGuidePanel), findsOneWidget);
    final rail = _panelOf(tester, find.byKey(const Key('simple-shutter')));
    expect(tester.getRect(find.byKey(const Key('simple-guide-panel'))).right, rail.left - kSimpleInset,
        reason: 'left of the rail, like every mode panel');
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
    expect(find.text('Fit the mask, then Start'), findsNothing, reason: 'picking a mode opens nothing');
    // Picking a mode only switches it; the chip opens its panel.
    await tester.tap(find.byKey(const Key('simple-settings')));
    await tester.pump();
    final rail = _panelOf(tester, find.byKey(const Key('simple-shutter')));
    final panel = _panelOf(tester, find.text('Fit the mask, then Start'));
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
    expect(find.descendant(of: chip, matching: find.text('1 s')), findsOneWidget);
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

  testWidgets('the P.A. chip shows the exposure under the icon, never cut', (tester) async {
    tester.platformDispatcher.textScaleFactorTestValue = 0.5;
    addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
    final container = await _pump(tester, const Size(1024, 768));
    await container.read(simplePolarSettingsProvider.future);
    await container.read(simplePolarSettingsProvider.notifier)
        .save(const PolarAlignSettings(exposureSeconds: 45));
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.text('Polar align'));
    await tester.pump();
    final chip = find.byKey(const Key('simple-settings'));
    expect(find.descendant(of: chip, matching: find.byIcon(Icons.tune)), findsOneWidget);
    final p = tester.renderObject<RenderParagraph>(
        find.descendant(of: find.descendant(of: chip, matching: find.text('45 s')), matching: find.byType(RichText)));
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
    expect(find.byType(SimpleRunSheet), findsOneWidget);
    expect(find.byKey(const Key('simple-sky-image-this')), findsNothing, reason: 'the sheet, not the card');

    await tester.tap(find.byKey(const Key('simple-run-pick-in-sky')));
    await tester.pump();
    expect(find.byType(SimpleRunSheet), findsNothing);
    expect(find.byKey(const Key('sky')), findsOneWidget);
    expect(find.byKey(const Key('simple-sky-image-this')), findsOneWidget);
  });

  testWidgets('Sky Album on the left rail; a card\'s action closes it on Plan\'s sheet with the card picked',
      (tester) async {
    final container = await _pump(tester, const Size(1024, 768));
    await _frame(tester, container);
    await tester.tap(find.byKey(const Key('simple-album')));
    await tester.pumpAndSettle();
    expect(find.byType(SkyAlbumScreen), findsOneWidget);

    const m31 = PlanningDso(
        id: 'NGC0224', name: 'Andromeda Galaxy', type: 'G', magnitude: 3.4, raDeg: 10.68, decDeg: 41.27,
        messierNum: 31, aliases: ['M 31']);
    tester.widget<SkyAlbumScreen>(find.byType(SkyAlbumScreen)).onImageTarget(m31);
    await tester.pumpAndSettle();

    expect(find.byType(SkyAlbumScreen), findsNothing, reason: 'the album hands over and closes');
    expect(find.descendant(of: find.byKey(const Key('simple-mode')), matching: find.text('Plan')), findsOneWidget);
    expect(find.byType(SimpleRunSheet), findsOneWidget);
    final sel = container.read(planetariumSelectionProvider)!;
    expect(sel.name, 'Andromeda Galaxy');
    expect(sel.decJ2000Deg, 41.27);
    expect(container.read(simpleRunProvider).targets, isEmpty, reason: 'nothing queued');
  });

  // Falsified by: simple_shell.dart _SkyPick onImage, pick(plan) unconditionally (the old `_mode = plan`).
  testWidgets("Image this over the sky in Night Setup's Plan step stays in Night Setup", (tester) async {
    final container = await _pump(tester, const Size(1024, 768));
    await _frame(tester, container);
    await _startGuidedRun(tester);
    expect(container.read(nightSetupProvider), NightSetupStep.plan);
    await tester.tap(find.byKey(const Key('simple-run-pick-in-sky')));
    await tester.pump();
    container.read(planetariumSelectionProvider.notifier).set(const PlanetariumSelection(
        name: 'M31', kind: 'Galaxy', raHours: 0.73, decDeg: 41.4, raJ2000Hours: 0.71, decJ2000Deg: 41.27,
        altDeg: 62.4, maxAltDeg: 74.2));
    await tester.pump();
    await tester.tap(find.byKey(const Key('simple-sky-image-this')));
    await tester.pump();
    expect(container.read(nightSetupProvider), NightSetupStep.plan, reason: 'Plan shows already: no pick');
    expect(find.byKey(const Key('night-setup-strip')), findsOneWidget);
    expect(find.byType(SimpleRunSheet), findsOneWidget, reason: 'back on the plan sheet');
    expect(find.byKey(const Key('sky')), findsNothing);
  });

  // Falsified by: simple_shell.dart _SkyPick onImage, guard on `pilot == null` instead of what shows.
  testWidgets("Image this over the sky in Night Setup's Focus step goes to Plan and leaves Night Setup, as before",
      (tester) async {
    final container = await _pump(tester, const Size(1024, 768));
    await _frame(tester, container);
    await _startGuidedRun(tester);
    await tester.tap(find.byKey(const Key('night-setup-skip')));
    await tester.pump();
    expect(container.read(nightSetupProvider), NightSetupStep.focus);
    await tester.tap(find.text('Sky'));
    await tester.pump();
    container.read(planetariumSelectionProvider.notifier).set(const PlanetariumSelection(
        name: 'M31', kind: 'Galaxy', raHours: 0.73, decDeg: 41.4, raJ2000Hours: 0.71, decJ2000Deg: 41.27,
        altDeg: 62.4, maxAltDeg: 74.2));
    await tester.pump();
    await tester.tap(find.byKey(const Key('simple-sky-image-this')));
    await tester.pump();
    expect(container.read(simpleShowingProvider), SimpleModeScreen.plan);
    expect(container.read(nightSetupProvider), isNull, reason: 'picking Plan leaves Night Setup');
    expect(find.byKey(const Key('night-setup-strip')), findsNothing);
    expect(find.byType(SimpleRunSheet), findsOneWidget, reason: "Plan's sheet, not Focus's panel");
    expect(find.byType(SimpleFocusPanel), findsNothing);
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

  testWidgets('no Start Night Setup banner (user, 2026-11-04); from the modes it walks Plan first, the rail reads Setup',
      (tester) async {
    await _pump(tester, const Size(1024, 768));
    expect(find.text('Start Night Setup'), findsNothing, reason: 'the banner went');
    await _startGuidedRun(tester);
    expect(find.byKey(const Key('night-setup-strip')), findsOneWidget);
    // "Night Setup" doesn't fit the 60 px rail: it reads Setup, as Polar align reads P.A.
    expect(find.descendant(of: find.byKey(const Key('simple-mode')), matching: find.text('Setup')), findsOneWidget);
    expect(find.bySemanticsLabel(RegExp(r'^Mode: Night Setup')), findsOneWidget, reason: 'the screen reader says it whole');
    expect(find.byType(SimpleRunSheet), findsOneWidget, reason: "the plan's sheet first");
    await tester.tap(find.byKey(const Key('night-setup-skip')));
    await tester.pump();
    expect(find.byType(SimpleFocusPanel), findsOneWidget, reason: "then Focus's panel");
    await tester.tap(find.byKey(const Key('night-setup-skip')));
    await tester.pump();
    expect(find.byType(SimplePolarPanel), findsOneWidget, reason: "then polar align's panel");
    // Picking a mode leaves Night Setup.
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.text('Preview').first);
    await tester.pump();
    expect(find.byKey(const Key('night-setup-strip')), findsNothing);
  });

  // Falsified by: simple_night_setup_state.dart SimpleShowingNotifier.build, drop the simpleModeProvider listen.
  testWidgets('Simple Mode opens on Preview again: a reopen ends a Night Setup left running',
      (tester) async {
    final container = await _pump(tester, const Size(1024, 768), feature: true);
    await _startGuidedRun(tester);
    expect(find.byKey(const Key('night-setup-strip')), findsOneWidget);
    expect(container.read(simpleShowingProvider), SimpleModeScreen.plan);
    // Leave for the full UI and come back: a new shell on the same providers.
    await container.read(simpleModeProvider.notifier).set(false);
    await tester.pumpWidget(UncontrolledProviderScope(container: container, child: const SizedBox()));
    await container.read(simpleModeProvider.notifier).set(true);
    await tester.pumpWidget(UncontrolledProviderScope(
        container: container,
        child: const MaterialApp(home: SimpleShell(planetarium: SizedBox(key: Key('sky'))))));
    await tester.pump();
    expect(container.read(simpleShowingProvider), SimpleModeScreen.preview);
    expect(container.read(nightSetupProvider), isNull);
    expect(find.byKey(const Key('night-setup-strip')), findsNothing);
  });

  for (final (size, scale, queued) in const [
    (Size(1024, 768), 1.0, 0),
    (Size(1024, 768), 1.0, 1),
    (Size(768, 1024), 1.0, 1),
    (Size(1024, 768), 1.3, 1),
    (Size(1024, 768), 1.0, 10),
    (Size(768, 1024), 1.0, 10),
  ]) {
    testWidgets('at $size, text ${scale}x, in Night Setup\'s Plan step ($queued queued) the settings '
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
      await _startGuidedRun(tester);
      final sheet = find.byType(SimpleRunSheet);
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
      expect(find.text('Night Setup'), findsOneWidget, reason: 'the modes are open');
      await tap(find.ancestor(of: find.text('Focus'), matching: find.byType(InkWell)).first);
      expect(find.byKey(const Key('night-setup-strip')), findsNothing, reason: 'Focus picked: Night Setup left');
      expect(find.byType(SimpleFocusPanel), findsNothing, reason: 'picking a mode opens nothing');
    });
  }

  testWidgets('in Night Setup\'s Plan step over the sky, after Add to queue ten times, the chip and the modes still '
      'put the sheet away', (tester) async {
    await _pump(tester, const Size(1024, 768), plan: const SimpleRun(), queueing: true);
    await _startGuidedRun(tester);
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
    final sheet = find.byType(SimpleRunSheet);
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
    expect(find.byKey(const Key('night-setup-strip')), findsNothing, reason: 'Focus picked: Night Setup left');
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

  testWidgets('Night Setup picked from the modes stays in Simple Mode and starts, as the offer does', (tester) async {
    final container = await _pump(tester, const Size(1024, 768));
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.text('Night Setup'));
    await tester.pump();
    expect(container.read(simpleModeProvider).value, isTrue, reason: 'no jump to the full UI');
    expect(find.byKey(const Key('night-setup-strip')), findsOneWidget);
    expect(find.byType(SimpleRunSheet), findsOneWidget, reason: "the plan's sheet, open: Plan comes first");
  });

  testWidgets("Night Setup's plan step only queues: its green button reads Next: Focus and goes there", (tester) async {
    await _pump(tester, const Size(1024, 768),
        plan: const SimpleRun(sequenceId: 'seq-1', targets: [
          SimpleTarget(name: 'M 42', steps: [FilterPlanStep(filterName: 'L', exposureSeconds: 120, frameCount: 30)]),
        ]));
    await _startGuidedRun(tester);
    final next = find.byKey(const Key('simple-run-start'));
    expect(find.descendant(of: next, matching: find.text('Next: Focus')), findsOneWidget);
    expect(find.text('Imaging starts after focus and polar alignment.'), findsOneWidget);
    await tester.tap(next);
    await tester.pump();
    expect(find.byType(SimpleFocusPanel), findsOneWidget);
  });

  testWidgets('in Night Setup the chip names the step whose settings it shows', (tester) async {
    await _pump(tester, const Size(1024, 768));
    await _startGuidedRun(tester);
    final chip = find.byKey(const Key('simple-settings'));
    expect(find.descendant(of: chip, matching: find.text('Plan\n5 min')), findsOneWidget);
    await tester.tap(find.byKey(const Key('night-setup-skip')));
    await tester.pump();
    expect(find.descendant(of: chip, matching: find.text('Focus\nManual')), findsOneWidget);
    await tester.tap(find.byKey(const Key('night-setup-skip')));
    await tester.pump();
    expect(find.descendant(of: chip, matching: find.text('P.A.\n1 s')), findsOneWidget);
  });

  for (final size in const [Size(1024, 768), Size(768, 1024), Size(507, 768)]) {
    testWidgets('at $size: message pills sit top-left, beside the status pill, above the left rail, '
        'clear of the status pills', (tester) async {
      final container = await _pump(tester, size);
      await _frame(tester, container);
      container.read(captureProgressProvider.notifier).beginExposing(const Duration(seconds: 5));
      for (var i = 0; i < 6; i++) {
        container.read(simpleMessagesProvider.notifier).show(SimpleMessageKind.error, 'Message $i');
      }
      await tester.pump();
      // The rail is measured after the first frame.
      await tester.pump(const Duration(milliseconds: 300));
      final pills = tester.getRect(find.byType(SimpleMessagePills));
      final status = _panelOf(tester, find.textContaining('Taking a frame'));
      final rail = _panelOf(tester, find.text('Library'));
      expect(pills.left, kSimpleInset, reason: 'the top-left corner');
      expect(pills.right, lessThanOrEqualTo(status.left), reason: 'beside the status pill, never under it');
      expect(pills.bottom, lessThanOrEqualTo(rail.top), reason: 'above the left rail');
      expect(find.byKey(const Key('simple-messages-more')), findsOneWidget, reason: 'six don\'t fit: the rest fold');
      container.read(captureProgressProvider.notifier).reset();
      await tester.pump(const Duration(seconds: 1));
    });
  }

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

  // #1571: the sheet reopened on the old target when Image this came after an edit left _editing set.
  testWidgets('Image this after an edit opens the sheet on the new pick, not the edited target (#1571)', (tester) async {
    final container = await _pump(tester, const Size(1024, 768), plan: _queuedM31);
    await _frame(tester, container);
    await _openEditOfM31(tester);
    expect(find.byKey(const Key('simple-run-save')), findsOneWidget, reason: 'the edit sheet');
    // The status pill (and Night Setup) switch screen without closing the sheet.
    container.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.preview);
    await tester.pump();
    await tester.tap(find.text('Sky'));
    await tester.pump();
    container.read(planetariumSelectionProvider.notifier).set(const PlanetariumSelection(
        name: 'M42', kind: 'Nebula', raHours: 5.58, decDeg: -5.4, raJ2000Hours: 5.58, decJ2000Deg: -5.4,
        altDeg: 50, maxAltDeg: 60));
    await tester.pump();
    await tester.tap(find.byKey(const Key('simple-sky-image-this')));
    await tester.pump();
    expect(find.byType(SimpleRunSheet), findsOneWidget);
    expect(find.byKey(const Key('simple-run-save')), findsNothing, reason: 'not the edit of M31');
    expect(find.text('Edit M31'), findsNothing);
  });

  group('#1572 a rail or image tap while editing a queued target', () {
    Future<ProviderContainer> edit(WidgetTester tester) async {
      final container = await _pump(tester, const Size(1024, 768), plan: _queuedM31);
      await _frame(tester, container);
      await _openEditOfM31(tester);
      return container;
    }

    testWidgets('with a changed row, the tap asks first and the sheet stays; Keep editing keeps the edited rows', (tester) async {
      final container = await edit(tester);
      container.read(simpleRunPlanProvider.notifier).setFrames(0, 5);
      await tester.pump();
      await tester.tap(find.byKey(const Key('simple-mode')));
      await tester.pump();
      expect(find.text('Discard changes to M31?'), findsOneWidget);
      expect(find.byType(SimpleRunSheet), findsOneWidget, reason: 'the sheet stays');
      await tester.tap(find.byKey(const Key('simple-mode')));
      await tester.pump();
      expect(container.read(simpleMessagesProvider).length, 1, reason: 'one pill at a time');
      await tester.tap(find.text('Keep editing'));
      await tester.pump();
      expect(find.text('Discard changes to M31?'), findsNothing);
      expect(find.byType(SimpleRunSheet), findsOneWidget);
      expect(container.read(simpleRunPlanProvider).single.frameCount, 5);
    });

    testWidgets('Discard closes the sheet, leaves the queued target as it was, then the tapped Screen shows', (tester) async {
      final container = await edit(tester);
      container.read(simpleRunPlanProvider.notifier).setFrames(0, 5);
      await tester.pump();
      await tester.tap(find.byKey(const Key('simple-mode')));
      await tester.pump();
      await tester.tap(find.text('Discard'));
      await tester.pump();
      expect(find.byType(SimpleRunSheet), findsNothing);
      expect(find.text('Discard changes to M31?'), findsNothing);
      expect(find.text('Polar align'), findsOneWidget, reason: 'the modes the tap opened');
      expect(container.read(simpleRunProvider).targets.single.steps.single.frameCount, 30);
    });

    testWidgets('a tap on the image asks too, and Discard then closes the sheet', (tester) async {
      final container = await edit(tester);
      container.read(simpleRunPlanProvider.notifier).setFrames(0, 5);
      await tester.pump();
      await tester.tapAt(Offset(500, tester.getRect(find.byType(SimpleRunSheet)).bottom + 6)); // the image, under the sheet
      await tester.pump();
      expect(find.text('Discard changes to M31?'), findsOneWidget);
      expect(find.byType(SimpleRunSheet), findsOneWidget);
      await tester.tap(find.text('Discard'));
      await tester.pump();
      expect(find.byType(SimpleRunSheet), findsNothing);
    });

    testWidgets('with no change, the tap closes the sheet as before and asks nothing', (tester) async {
      final container = await edit(tester);
      await tester.tap(find.byKey(const Key('simple-mode')));
      await tester.pump();
      expect(container.read(simpleMessagesProvider), isEmpty);
      expect(find.byType(SimpleRunSheet), findsNothing);
    });

    testWidgets('the safe answer carries the weight: Keep editing is the filled button, Discard a text button', (tester) async {
      final container = await edit(tester);
      container.read(simpleRunPlanProvider.notifier).setFrames(0, 5);
      await tester.pump();
      await tester.tap(find.byKey(const Key('simple-mode')));
      await tester.pump();
      expect(find.ancestor(of: find.text('Keep editing'), matching: find.byType(FilledButton)), findsOneWidget);
      expect(find.ancestor(of: find.text('Discard'), matching: find.byType(TextButton)), findsOneWidget);
    });

    testWidgets("the camera's gain changing while the sheet is open (another client) is not an edit", (tester) async {
      final container = await edit(tester);
      final camera = container.read(exposureControllerProvider).gain;
      container.read(exposureControllerProvider.notifier).setGain(camera + 40);
      await tester.pump();
      await tester.tap(find.byKey(const Key('simple-mode')));
      await tester.pump();
      expect(container.read(simpleMessagesProvider), isEmpty, reason: 'nothing the user changed');
      expect(find.byType(SimpleRunSheet), findsNothing);
    });

    testWidgets('a gain-only change asks too: Save would store it', (tester) async {
      final container = await edit(tester);
      final seeded = container.read(simpleRunGainProvider);
      container.read(simpleRunGainProvider.notifier).set((seeded ?? 100) + 50);
      await tester.pump();
      await tester.tap(find.byKey(const Key('simple-mode')));
      await tester.pump();
      expect(find.text('Discard changes to M31?'), findsOneWidget);
      expect(find.byType(SimpleRunSheet), findsOneWidget, reason: 'the sheet stays');
    });

    testWidgets('the question goes when the sheet closes another way (Cancel), so Discard cannot replay an old tap', (tester) async {
      final container = await edit(tester);
      container.read(simpleRunPlanProvider.notifier).setFrames(0, 5);
      await tester.pump();
      await tester.tap(find.byKey(const Key('simple-mode')));
      await tester.pump();
      expect(find.text('Discard changes to M31?'), findsOneWidget);
      final cancel = find.descendant(of: find.byType(SimpleRunSheet), matching: find.text('Cancel'));
      await tester.ensureVisible(cancel);
      await tester.tap(cancel);
      await tester.pump();
      expect(find.byType(SimpleRunSheet), findsNothing);
      expect(find.text('Discard changes to M31?'), findsNothing, reason: 'no question left over the closed sheet');
      expect(container.read(simpleMessagesProvider), isEmpty);
    });
  });

  testWidgets('the plan square hugs the bottom-right corner, above Stop (no bottom row)', (tester) async {
    await _pump(tester, const Size(1024, 768),
        plan: const SimpleRun(sequenceId: 'seq-1', targets: [
          SimpleTarget(name: 'M31', steps: [FilterPlanStep(filterName: 'L', exposureSeconds: 120, frameCount: 30)]),
        ]));
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.text('Plan').first);
    await tester.pump();
    final square = tester.getRect(find.byKey(const Key('simple-run-tracker')));
    expect(square.right, 1024 - kSimpleInset, reason: 'in the corner, from the first frame (no slide from the offer)');
    final stop = tester.getRect(find.byType(EmergencyStopButton));
    expect(square.bottom, stop.top - kSimpleInset, reason: 'the bottom row is empty: just above Stop, not under it');
    final rail = _panelOf(tester, find.byKey(const Key('simple-shutter')));
    expect(square.top, greaterThan(rail.bottom), reason: 'below the right rail');
    expect(find.descendant(of: find.byKey(const Key('simple-run-tracker')), matching: find.text('0 / 30')), findsOneWidget);
  });

  for (final size in const [Size(1024, 768), Size(768, 1024)]) {
    testWidgets('at $size: Plan\'s sheet runs from the status pill to the bottom row (the inset while it is empty) while the plan squares '
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
      expect(sheet.top, greaterThanOrEqualTo(kSimpleInset * 2 + kSimpleBarHeight + kSimpleStatusHeight + 8),
          reason: 'under the status pill');
      expect(sheet.bottom, lessThanOrEqualTo(size.height - kSimpleInset),
          reason: 'no guiding or histogram: down to the inset (ALP-4935)');
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
    testWidgets('at $size: in Night Setup the plan squares stay clear of the step strip', (tester) async {
      await _pump(tester, size,
          plan: SimpleRun(sequenceId: 'seq-1', targets: [
            for (var i = 1; i <= 6; i++)
              SimpleTarget(name: 'M$i', steps: const [FilterPlanStep(filterName: 'L', exposureSeconds: 120, frameCount: 30)]),
          ]));
      await _startGuidedRun(tester);
      await tester.tap(find.byKey(const Key('simple-settings')));
      await tester.pump();
      final strip = tester.getRect(find.byKey(const Key('night-setup-strip')));
      final squares = tester.getRect(find.byKey(const Key('simple-run-tracker')));
      expect(squares.overlaps(strip), isFalse, reason: 'strip $strip, squares $squares');
    });
  }

  // Info and System: a tile per fact in the bottom-left corner, above the guiding and histogram row (at the
  // inset while that row is empty), growing right; System's row sits above Info's.
  const tileLeft = kSimpleInset;
  const tileBottom = kSimpleInset;

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
    expect(row.bottom, 768 - tileBottom, reason: 'no guiding or histogram: down at the inset (ALP-4935)');
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
    // Under the gear, no wider than its content needs (user, 2026-11-04: the
    // window-wide sheet was mostly empty space).
    expect(rect.right, size.width - kSimpleInset, reason: 'hugs the right edge, under the gear');
    expect(rect.width, kSimpleSettingsMaxWidth);
    final stop = tester.getRect(find.byType(EmergencyStopButton));
    expect(rect.bottom, lessThanOrEqualTo(stop.top - kSimpleInset),
        reason: 'clear of Stop, which sits over the veil (iPad, 2026-11-04: the sheet ran under it)');
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
            {'host': '192.0.2.10', 'port': 6800, 'server_name': 'AlpacaHTTP', 'manufacturer': 'OpenAstro.net', 'version': '6.0.0~nightly'},
          ],
          'astap_version': 'CLI-2026.09.01',
        }),
        starDatabases: const ['d05', 'd80']);
    await _openSection(tester, 'Rig');
    // Every version on the rig, for a bug report or a support question.
    final versions = find.byKey(const Key('simple-versions'));
    for (final (name, value) in const [
      ('Ara server', '0.0.1-ara.7'),
      ('Guider', '2.6.13dev7'),
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
    final name = tester.getRect(find.text('Ara server'));
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
    expect(find.byKey(const Key('simple-rig-aperture-wheel-0')), findsOneWidget);
    expect(find.byKey(const Key('simple-rig-aperture-wheel-1')), findsNothing, reason: 'one wheel of millimetres');
    expect(tester.widget<CupertinoPicker>(find.byKey(const Key('simple-rig-aperture-wheel-0'))).useMagnifier, isTrue,
        reason: "iOS's own picker, the value under the band magnified");
    await roll('simple-rig-aperture-wheel-0', 2);
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

  testWidgets('Use this device\'s location and time sets the site, its time zone and the rig\'s clock', (tester) async {
    final clock = _Clock();
    final c = await _pump(tester, const Size(1024, 768), gear: [
      timeSyncApiProvider.overrideWithValue(clock),
      clientGpsPrefsServiceProvider.overrideWithValue(_GpsPrefs()),
    ]);
    c.read(siteSettingsProvider.notifier).setTimeZone('Europe/London');
    await _openSection(tester, 'Rig');
    final button = find.byKey(const Key('simple-rig-use-location'));
    expect(find.descendant(of: button, matching: find.textContaining('location and time')), findsOneWidget);
    await tester.ensureVisible(button);
    await tester.pumpAndSettle();
    await tester.tap(button);
    await tester.pump(const Duration(seconds: 4)); // the fill waits up to 3 s on this computer's GPS setting
    await tester.pumpAndSettle();
    final site = c.read(siteSettingsProvider);
    expect((site.latitudeDeg, site.longitudeDeg, site.elevationM), (-37.2, 174.88, 71.0));
    expect(site.timeZone, 'Pacific/Auckland', reason: 'from the coordinates, like the full UI\'s GPS fill');
    expect(clock.pushed, hasLength(1), reason: "this device's clock goes to the rig");
  });

  testWidgets('Search finds Simple Mode\'s settings, modes and sky objects and opens each inside Simple Mode',
      (tester) async {
    final c = await _pump(tester, const Size(1024, 768), gear: [
      bundledCatalogProvider.overrideWith((ref) async => const [
            PlanningDso(id: 'NGC0224', name: 'Andromeda Galaxy', type: 'G', magnitude: 3.4, raDeg: 10.7, decDeg: 41.3, aliases: ['M 31']),
          ]),
    ]);
    Future<void> search(String q) async {
      await tester.tap(find.byKey(const Key('simple-search-button')));
      await tester.pump();
      expect(find.byKey(const Key('simple-search')), findsOneWidget);
      await tester.enterText(find.byKey(const Key('simple-search-field')), q);
      await tester.pump();
      await tester.pump();
    }

    await search('eleva');
    expect(find.descendant(of: find.byKey(const Key('simple-search-setting-0')), matching: find.text('Elevation')),
        findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-search-setting-0')));
    await tester.pump();
    expect(find.byKey(const Key('simple-search')), findsNothing);
    expect(find.byKey(const Key('simple-rig-elevation-up')), findsOneWidget, reason: 'Settings, on Rig');
    await tester.tap(find.byKey(const Key('simple-menu-veil')));
    await tester.pump();

    await search('cool');
    expect(find.textContaining('Nothing in Simple Mode matches'), findsOneWidget, reason: 'no camera, no Cooler');
    await tester.tap(find.byKey(const Key('simple-search-button')));
    await tester.pump();

    await search('foc');
    expect(find.descendant(of: find.byKey(const Key('simple-search-mode-0')), matching: find.text('Focus')),
        findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-search-mode-0')));
    await tester.pump();
    expect(find.descendant(of: find.byKey(const Key('simple-mode')), matching: find.text('Focus')), findsOneWidget);

    await search('m3');
    await tester.tap(find.byKey(const Key('simple-search-sky-0')));
    await tester.pump();
    expect(c.read(planetariumSearchProvider), 'M 31');
    expect(find.byKey(const Key('simple-planetarium')), findsOneWidget, reason: 'the sky, pointed at it');
  });

  testWidgets('the Ara mark opens the OpenAstro Ara sheet: versions, rig and profile; the gear stays (#1602)',
      (tester) async {
    final c = await _pump(tester, const Size(1024, 768),
        server: const AraServer(hostname: '10.0.0.5', port: 5555, mdnsName: 'astro'),
        profiles: const ProfileList(activeId: 'p1', profiles: [ProfileMeta(id: 'p1', name: 'Backyard Rig')]),
        versions: DaemonVersions.fromJson({'daemon_version': '0.0.1-ara.7', 'guider_version': '2.6.13dev7'}));
    expect(find.byTooltip('Settings'), findsOneWidget, reason: 'the gear stays on the right');
    // Set apart from the device dots by a hairline (user, 2026-11-06).
    expect(tester.getRect(find.byKey(const Key('simple-ara-divider'))).left,
        greaterThanOrEqualTo(tester.getRect(find.byKey(const Key('simple-ara-mark'))).right));
    await tester.tap(find.byKey(const Key('simple-ara-mark')));
    await tester.pump();
    await tester.pump();
    final sheet = find.byKey(const Key('simple-ara-sheet'));
    for (final t in ['OpenAstro Ara', 'This app', 'Ara server', '0.0.1-ara.7', 'Guider', '2.6.13dev7', 'Rig', 'astro',
        'Profile', 'Backyard Rig']) {
      expect(find.descendant(of: sheet, matching: find.text(t)), findsOneWidget, reason: t);
    }
    expect(find.descendant(of: sheet, matching: find.text('Settings')), findsNothing, reason: 'no Settings row');
    await tester.tap(find.byKey(const Key('simple-ara-mark')));
    await tester.pump();
    expect(sheet, findsNothing, reason: 'the mark closes it again');
    await tester.pumpWidget(const SizedBox());
    c.dispose();
    await tester.pump(const Duration(seconds: 5));
  });

  testWidgets('the Ara sheet grows with a long rig name up to 420 px, then wraps it (#1602)', (tester) async {
    final c = await _pump(tester, const Size(1024, 768),
        server: const AraServer(
            hostname: '10.0.0.5', port: 5555, mdnsName: 'backyard-observatory-raspberry-pi-five-north-pier-astro-rig'));
    await tester.tap(find.byKey(const Key('simple-ara-mark')));
    await tester.pump();
    expect(tester.getSize(find.byKey(const Key('simple-ara-sheet'))).width, 420);
    await tester.pumpWidget(const SizedBox());
    c.dispose();
    await tester.pump(const Duration(seconds: 5));
  });

  testWidgets("no frame yet: the wallpaper, not the sky; the Sky button shows tonight's sky (#1602)", (tester) async {
    await _pump(tester, const Size(1024, 768));
    expect(find.byKey(const Key('simple-wallpaper')), findsOneWidget);
    expect(find.byKey(const Key('sky')), findsNothing);
    expect(find.text('Tap the shutter to take a frame'), findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-sky')));
    await tester.pump();
    expect(find.byKey(const Key('sky')), findsOneWidget);
    expect(find.byKey(const Key('simple-wallpaper')), findsNothing);
    await tester.tap(find.byKey(const Key('simple-sky')));
    await tester.pump();
    expect(find.byKey(const Key('simple-wallpaper')), findsOneWidget, reason: 'and back');
  });

  testWidgets('Sky turned on with no frame gives way to the first frame (#1602)', (tester) async {
    final c = await _pump(tester, const Size(1024, 768));
    await tester.tap(find.byKey(const Key('simple-sky')));
    await tester.pump();
    expect(find.byKey(const Key('sky')), findsOneWidget);
    await _frame(tester, c);
    expect(find.byKey(const Key('sky')), findsNothing, reason: 'the frame, not the sky');
    await tester.tap(find.byKey(const Key('simple-sky')));
    await tester.pump();
    expect(find.byKey(const Key('sky')), findsOneWidget, reason: 'Sky still opens it over a frame');
  });

  testWidgets('Search finds the Guide Camera section, and Dither in Guide mode where it lives (#1586)',
      (tester) async {
    await _pump(tester, const Size(1024, 768), gear: [guiderStatusProvider.overrideWith(() => _GuiderUp())]);
    Future<void> search(String q) async {
      await tester.tap(find.byKey(const Key('simple-search-button')));
      await tester.pump();
      await tester.enterText(find.byKey(const Key('simple-search-field')), q);
      await tester.pump();
      await tester.pump();
    }

    await search('phd2');
    expect(find.descendant(of: find.byKey(const Key('simple-search-setting-0')), matching: find.text('Guide Camera')),
        findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-search-button')));
    await tester.pump();

    await search('dither');
    final row = find.byKey(const Key('simple-search-setting-0'));
    expect(find.descendant(of: row, matching: find.text('Dither')), findsOneWidget);
    expect(find.descendant(of: row, matching: find.text('Guider')), findsOneWidget,
        reason: 'Dither is in the Guider mode, not the Guide Camera window (user, 2026-11-05)');
    await tester.tap(row);
    await tester.pump();
    expect(find.byKey(const Key('simple-search')), findsNothing);
    expect(find.descendant(of: find.byKey(const Key('simple-mode')), matching: find.text('Guider')), findsOneWidget);
  });

  testWidgets('Search opens on its own: no mode panel beside the rail', (tester) async {
    await _pump(tester, const Size(1024, 768));
    await tester.tap(find.byKey(const Key('simple-search-button')));
    await tester.pump();
    expect(find.byKey(const Key('simple-search')), findsOneWidget);
    expect(find.text('Preview settings'), findsNothing);
  });

  testWidgets('⌘K opens Search too', (tester) async {
    await _pump(tester, const Size(1024, 768));
    await tester.sendKeyDownEvent(LogicalKeyboardKey.metaLeft);
    await tester.sendKeyEvent(LogicalKeyboardKey.keyK);
    await tester.sendKeyUpEvent(LogicalKeyboardKey.metaLeft);
    await tester.pump();
    expect(find.byKey(const Key('simple-search')), findsOneWidget);
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

  testWidgets('Rig\'s Name and Profile read like its other rows, a 44 pt row each', (tester) async {
    await _pump(tester, const Size(1024, 768));
    await _openSection(tester, 'Rig');
    TextStyle? style(String t) => tester.widget<Text>(find.text(t)).style;
    for (final t in ['Name', 'Profile']) {
      expect(style(t), style('Main scope aperture'), reason: t);
    }
    // Their Change buttons are 44 pt targets (user, 2026-11-03), so each row is 44 pt.
    expect(tester.getCenter(find.text('Profile')).dy - tester.getCenter(find.text('Name')).dy, closeTo(44, 4));
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

  testWidgets('Rig: a language picker, System first, for this device only', (tester) async {
    final c = await _pump(tester, const Size(1024, 768));
    await _openSection(tester, 'Rig');
    final pt = find.byKey(const Key('simple-menu-language-pt'));
    await tester.ensureVisible(pt); // Rig scrolls
    expect(tester.widget<SimpleSegment>(find.byKey(const Key('simple-menu-language-system'))).selected, isTrue);
    await tester.tap(pt);
    await tester.pump();
    expect(c.read(appLocaleProvider), const Locale('pt', 'BR'));
    await tester.tap(find.byKey(const Key('simple-menu-language-system')));
    await tester.pump();
    expect(c.read(appLocaleProvider), isNull);
  });

  testWidgets("Rig names the main scope's optics and shows the guide scope's, kept in the profile", (tester) async {
    final c = await _pump(tester, const Size(1024, 768));
    c.read(phd2SettingsProvider.notifier)
      ..setGuideFocalLength(128)
      ..setGuidePixelSize(3.75);
    await _openSection(tester, 'Rig');
    for (final t in ['Main scope aperture', 'Main scope focal length', 'Guide scope focal length', 'Guide camera pixel size']) {
      await tester.ensureVisible(find.text(t));
      expect(find.text(t), findsOneWidget, reason: t);
    }
    expect(find.descendant(of: find.byKey(const Key('simple-rig-guidefocal')), matching: find.text('128 mm')), findsOneWidget);
    expect(find.text('3.75 µm'), findsOneWidget);
    await tester.ensureVisible(find.byKey(const Key('simple-rig-guidefocal')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('simple-rig-guidefocal')));
    await tester.pumpAndSettle();
    await tester.ensureVisible(find.byKey(const Key('simple-rig-guidefocal-wheel-1')));
    await tester.pumpAndSettle();
    await tester.drag(find.byKey(const Key('simple-rig-guidefocal-wheel-1')), const Offset(0, -44.0));
    await tester.pumpAndSettle();
    expect(c.read(phd2SettingsProvider).guideFocalLength, 228, reason: 'per digit, like the main scope');
  });

  testWidgets("Rig shows the guide optics the rig fills a few seconds after the guider connects", (tester) async {
    // PR #1486 review: one read on Connected usually lands before the rig's fill, leaving 0 / Not set.
    final guider = _GuiderComingUp();
    final api = _GuideOpticsApi(2);
    await _pump(tester, const Size(1024, 768), linkUp: true,
        profiles: const ProfileList(activeId: 'p1', profiles: [ProfileMeta(id: 'p1', name: 'Backyard Rig')]), gear: [
      guiderStatusProvider.overrideWith(() => guider),
      profileApiProvider.overrideWithValue(api),
    ]);
    await _openSection(tester, 'Rig');
    guider.up();
    for (var i = 0; i < 5; i++) {
      await tester.pump(const Duration(seconds: 1));
    }
    await tester.ensureVisible(find.byKey(const Key('simple-rig-guidefocal')));
    expect(find.descendant(of: find.byKey(const Key('simple-rig-guidefocal')), matching: find.text('128 mm')), findsOneWidget);
    expect(find.text('3.75 µm'), findsOneWidget);
    final settled = api.reads;
    await tester.pump(const Duration(seconds: 5));
    expect(api.reads, settled, reason: 'it stops reading once both are filled');
  });

  testWidgets("Rig reads the guide optics from the rig when it opens, the guider already connected", (tester) async {
    // On the iPad (2026-11-03): the rig held 128 mm / 3.75 µm but Rig showed Not set — nothing read the
    // profile's guider section unless the guider connected while Rig was open.
    final api = _GuideOpticsApi(0);
    await _pump(tester, const Size(1024, 768), linkUp: true,
        profiles: const ProfileList(activeId: 'p1', profiles: [ProfileMeta(id: 'p1', name: 'Backyard Rig')]), gear: [
      guiderStatusProvider.overrideWith(_GuiderUp.new),
      profileApiProvider.overrideWithValue(api),
    ]);
    await _openSection(tester, 'Rig');
    await tester.pump();
    await tester.ensureVisible(find.byKey(const Key('simple-rig-guidefocal')));
    expect(find.descendant(of: find.byKey(const Key('simple-rig-guidefocal')), matching: find.text('128 mm')), findsOneWidget);
    expect(find.text('3.75 µm'), findsOneWidget);
  });

  testWidgets("every Rig adjustment is one width: the wheels, the elevation stepper and the read-only tile",
      (tester) async {
    // User, 2026-11-04: the elevation stepper was wider than the rows above it.
    await _pump(tester, const Size(1024, 768));
    await _openSection(tester, 'Rig');
    final elevation = find.byKey(const Key('simple-rig-elevation'));
    await tester.ensureVisible(elevation);
    await tester.pumpAndSettle();
    final widths = {
      for (final id in ['aperture', 'focal', 'guidefocal', 'guidepixel', 'lat', 'lng', 'elevation'])
        id: tester.getSize(find.byKey(Key('simple-rig-$id'))).width,
    };
    expect(widths.values.toSet(), hasLength(1), reason: '$widths');
    final right = {
      for (final id in ['focal', 'elevation']) id: tester.getRect(find.byKey(Key('simple-rig-$id'))).right,
    };
    expect(right.values.toSet(), hasLength(1), reason: 'one right edge: $right');
  });

  testWidgets("Rig's two guide rows show Not set in the same tile, the same size and edge", (tester) async {
    await _pump(tester, const Size(1024, 768));
    await _openSection(tester, 'Rig');
    await tester.ensureVisible(find.byKey(const Key('simple-rig-guidepixel')));
    await tester.pumpAndSettle();
    final focal = tester.getRect(find.byKey(const Key('simple-rig-guidefocal')));
    final pixel = tester.getRect(find.byKey(const Key('simple-rig-guidepixel')));
    expect(find.descendant(of: find.byKey(const Key('simple-rig-guidefocal')), matching: find.text('Not set')), findsOneWidget);
    expect(find.descendant(of: find.byKey(const Key('simple-rig-guidepixel')), matching: find.text('Not set')), findsOneWidget);
    expect((pixel.width, pixel.height, pixel.right), (focal.width, focal.height, focal.right));
  });

  testWidgets('every panel on the image blurs what is behind it, as the Settings sheet does (AraBlur.panel)',
      (tester) async {
    // User, 2026-11-04: the Settings sheet's 90 % scrim + blur on every Simple Mode panel, for legibility.
    final c = await _pump(tester, const Size(1024, 768),
        plan: SimpleRun(sequenceId: 'seq-1', targets: [
          SimpleTarget(name: 'M31', tag: 'M31', steps: const [FilterPlanStep(filterName: 'L', exposureSeconds: 60, frameCount: 10)],
              raDeg: 10.7, decDeg: 41.3),
        ]));
    await _frame(tester, c);
    await tester.tap(find.byKey(const Key('simple-info'))); // the Info tiles
    await tester.pump();
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.text('Plan').first); // the plan squares
    await tester.pump();
    c.read(simpleMessagesProvider.notifier)
      ..show(SimpleMessageKind.error, "Couldn't start imaging")
      ..show(SimpleMessageKind.result, 'Frame saved');
    await tester.pump();
    expect(find.byKey(const Key('simple-run-tracker')), findsOneWidget, reason: 'precondition: a plan square');
    bool scrim(Widget w) =>
        (w is Container && w.decoration is BoxDecoration && (w.decoration! as BoxDecoration).color == AraColors.overlayScrim) ||
        (w is Material && w.color == AraColors.overlayScrim);
    // Each scrim's own blur sits right above it (inside its clip), not some outer panel's.
    final surfaces = find.byWidgetPredicate(scrim).evaluate().toList();
    expect(surfaces.length, greaterThanOrEqualTo(8), reason: 'strip, rails, status, Info tiles, pills, plan square');
    for (final e in surfaces) {
      BackdropFilter? blur;
      var up = 0;
      e.visitAncestorElements((a) {
        if (a.widget is BackdropFilter) blur = a.widget as BackdropFilter;
        return blur == null && ++up < 4; // its own, a few elements up
      });
      expect(blur?.filter, ImageFilter.blur(sigmaX: AraBlur.panel, sigmaY: AraBlur.panel),
          reason: '${e.widget.key ?? e.widget.runtimeType} has no blur of its own');
    }
    await tester.pump(kSimpleResultLife + const Duration(seconds: 1)); // the result fades
  });

  testWidgets("Night Setup's plan step hugs the rail like every mode's panel, with no title (the strip names the step)",
      (tester) async {
    await _pump(tester, const Size(1024, 768));
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.text('Night Setup').first);
    await tester.pump();
    await tester.pump(const Duration(seconds: 1));
    final sheet = tester.getRect(find.byType(SimpleRunSheet));
    final rail = tester.getRect(find.byKey(const Key('simple-mode')));
    expect(sheet.right, closeTo(rail.left - kSimpleInset, 8), reason: 'beside the rail, not centred');
    expect(find.text("Tonight's plan"), findsNothing);
  });

  testWidgets("Plan's sheet sits beside the rail too, not centred over the image", (tester) async {
    await _pump(tester, const Size(1024, 768));
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.text('Plan').first);
    await tester.pump();
    await tester.tap(find.byKey(const Key('simple-settings')));
    await tester.pump();
    await tester.pump(const Duration(seconds: 1));
    final sheet = tester.getRect(find.byType(SimpleRunSheet));
    final rail = tester.getRect(find.byKey(const Key('simple-mode')));
    expect(sheet.right, closeTo(rail.left - kSimpleInset, 8));
  });

  testWidgets("saving the guide focal length keeps the pixel size the rig filled since Rig's read (ALP-4982)",
      (tester) async {
    // #1486 review: the save sent the whole client-side section, so a pixel size of 0 read before the
    // rig's fill went back over the filled 3.75 µm.
    final api = _Phd2StoreApi();
    await _pump(tester, const Size(1024, 768), linkUp: true,
        profiles: const ProfileList(activeId: 'p1', profiles: [ProfileMeta(id: 'p1', name: 'Backyard Rig')]), gear: [
      guiderStatusProvider.overrideWith(_GuiderGone.new),
      profileApiProvider.overrideWithValue(api),
    ]);
    await _openSection(tester, 'Rig');
    await tester.pump();
    expect(find.text('Not set'), findsWidgets, reason: 'precondition: Rig read the section unset');
    api.stored = const Phd2Settings(guideFocalLength: 128, guidePixelSize: 3.75); // the rig's fill
    await tester.ensureVisible(find.byKey(const Key('simple-rig-guidefocal')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('simple-rig-guidefocal')));
    await tester.pumpAndSettle();
    await tester.ensureVisible(find.byKey(const Key('simple-rig-guidefocal-wheel-1')));
    await tester.pumpAndSettle();
    await tester.drag(find.byKey(const Key('simple-rig-guidefocal-wheel-1')), const Offset(0, -44.0));
    await tester.pumpAndSettle();
    expect(api.saved, isNotEmpty);
    expect(api.saved.last.guidePixelSize, 3.75, reason: 'the filled pixel size stays');
    expect(api.stored.guideFocalLength, isNot(128), reason: "the user's focal length is saved");
  });

  testWidgets('Preview settings opens with no title: the chip says what it is', (tester) async {
    await _pump(tester, const Size(1024, 768));
    await tester.tap(find.byKey(const Key('simple-settings')));
    await tester.pump();
    expect(find.byKey(const Key('simple-exposure')), findsOneWidget);
    expect(find.text('Preview settings'), findsNothing);
  });

  testWidgets("a Rig edit with no rig link says it wasn't saved instead of nothing", (tester) async {
    // Review: _save returned silently when there was no ProfileApi, and the value reverted at the next read.
    final c = await _pump(tester, const Size(1024, 768), feature: true);
    expect(c.read(profileApiProvider), isNull, reason: 'precondition: no rig');
    await _openSection(tester, 'Rig');
    await tester.ensureVisible(find.byKey(const Key('simple-rig-guidefocal')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('simple-rig-guidefocal')));
    await tester.pumpAndSettle();
    await tester.ensureVisible(find.byKey(const Key('simple-rig-guidefocal-wheel-1')));
    await tester.pumpAndSettle();
    await tester.drag(find.byKey(const Key('simple-rig-guidefocal-wheel-1')), const Offset(0, -44.0));
    await tester.pumpAndSettle();
    final m = c.read(simpleMessagesProvider).single;
    expect((m.kind, m.title, m.detail), (SimpleMessageKind.error, "Couldn't save that", 'Rig not connected'));
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

  testWidgets('a Connect the rig refuses says why in a pill instead of looking like a dead button', (tester) async {
    final c = await _pump(tester, const Size(1024, 768), linkUp: true, feature: true, gear: [
      mountProvider.overrideWith(_RefusingMount.new),
    ]);
    await _openSection(tester, 'Devices');
    await tester.tap(find.byKey(const Key('simple-gear-mount-connect')));
    await tester.pump();
    expect(tester.takeException(), isNull);
    final m = c.read(simpleMessagesProvider).single;
    expect(m.kind, SimpleMessageKind.error);
    expect(m.title, 'Connect failed · Mount · AM5');
    expect(m.detail, contains('The mount did not answer'));
  });

  testWidgets('Devices lists the guider, named by its camera like its window; Disconnect, and the row opens it',
      (tester) async {
    final guider = _GuiderCalls();
    await _pump(tester, const Size(1024, 768), linkUp: true, gear: [
      guiderStatusProvider.overrideWith(() => guider),
      deviceDriverProvider.overrideWith((ref, type) async =>
          type == 'guidecamera' ? const DeviceDriver(name: 'ZWO ASI120MM Mini') : null),
    ]);
    await _openSection(tester, 'Devices');
    final page = find.byKey(const Key('simple-gear'));
    expect(find.descendant(of: page, matching: find.text('Guide Camera · ZWO ASI120MM Mini')), findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-gear-guider-disconnect')));
    await tester.pump();
    expect(guider.calls, ['disconnect']);
    await tester.tap(find.descendant(of: page, matching: find.text('Guide Camera · ZWO ASI120MM Mini')));
    // A guiding guider's dot blinks (working): pump a while, never settles.
    for (var n = 0; n < 5; n++) {
      await tester.pump(const Duration(milliseconds: 200));
    }
    expect(find.byKey(const Key('simple-gear')), findsNothing, reason: 'the Guider section is open');
  });

  testWidgets('a keyboard opens a device from its Devices row', (tester) async {
    await _pump(tester, const Size(1024, 768), linkUp: true, gear: [
      cameraStatusProvider.overrideWith(() => _Camera(EquipmentConnectionState.connected)),
    ]);
    await _openSection(tester, 'Devices');
    expect(await _tabTo(tester, const Key('simple-gear-camera')), isTrue);
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('simple-gear')), findsNothing, reason: 'the Camera section is open');
  });

  testWidgets("a Devices row's dot is the top bar chip's: one status, one colour", (tester) async {
    final guider = _GuiderConnecting();
    await _pump(tester, const Size(1024, 768), linkUp: true, gear: [
      guiderStatusProvider.overrideWith(() => guider),
      cameraStatusProvider.overrideWith(() => _Camera(EquipmentConnectionState.connecting)),
    ]);
    await _openSection(tester, 'Devices');
    // The dot's status colour (a changing one draws as a ring, so read the dot, not its fill).
    Color dot(String type) => tester
        .widget<StatusDot>(find.descendant(
            of: find.ancestor(
                    of: find.descendant(of: find.byKey(const Key('simple-gear')), matching: find.textContaining(type)),
                    matching: find.byType(Row))
                .first,
            matching: find.byType(StatusDot)))
        .color;
    expect(dot('Guide Camera'), guiderStatusLevel(await guider.future).color);
    expect(dot('Camera'), StatusLevel.info.color, reason: 'connecting is info on the chips');
  });

  testWidgets('a disconnected guider stays in Devices with Connect (the rig forgets it until it connects)',
      (tester) async {
    final guider = _GuiderGone();
    await _pump(tester, const Size(1024, 768), linkUp: true, gear: [
      guiderStatusProvider.overrideWith(() => guider),
    ]);
    await _openSection(tester, 'Devices');
    final page = find.byKey(const Key('simple-gear'));
    expect(find.descendant(of: page, matching: find.text('Guide Camera')), findsOneWidget);
    expect(find.descendant(of: page, matching: find.text('Not connected')), findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-gear-guider-connect')));
    await tester.pump();
    expect(guider.calls, ['connect'], reason: "the profile's guider, no chooser");
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
    await tester.tapAt(const Offset(512, 400)); // the image closes it (the Guided Run offer sits lower down)
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
    // The planetarium as the background (it takes taps itself).
    await tester.tap(find.byKey(const Key('simple-sky')));
    await tester.pump();
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

  testWidgets('the focuser\'s dot opens the Focuser window; Settings lists it under Devices', (tester) async {
    final c = await _pump(tester, const Size(1024, 768), linkUp: true, gear: [
      focuserProvider.overrideWith(() => _Focuser(EquipmentConnectionState.connected)),
    ]);
    c.read(selectedSettingsPanelProvider.notifier).select('eq.focuser');
    c.read(selectedTabIndexProvider.notifier).select(kOptionsTabIndex);
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('simple-device-focuser')), findsOneWidget);
    expect(find.byType(SettingsShell), findsNothing);
    await _openSection(tester, 'Focuser');
    expect(find.descendant(of: find.byKey(const Key('simple-menu')), matching: find.byKey(const Key('simple-device-focuser'))),
        findsOneWidget);
  });

  testWidgets('the filter wheel\'s dot opens its window and closes it again; Settings lists it under Devices',
      (tester) async {
    final c = await _pump(tester, const Size(1024, 768), linkUp: true, gear: [
      filterWheelProvider.overrideWith(() => _FilterWheel(EquipmentConnectionState.connected)),
    ]);
    Future<void> dot() async {
      c.read(selectedSettingsPanelProvider.notifier).select('eq.filterwheel');
      c.read(selectedTabIndexProvider.notifier).select(kOptionsTabIndex);
      await tester.pumpAndSettle();
    }
    await dot();
    expect(find.byKey(const Key('simple-device-filterwheel')), findsOneWidget);
    expect(find.byType(SettingsShell), findsNothing, reason: 'never the full UI\'s Settings page');
    await dot();
    expect(find.byKey(const Key('simple-device-filterwheel')), findsNothing);
    await _openSection(tester, 'Devices');
    await tester.tap(find.textContaining('EFW'));
    await tester.pump();
    expect(find.descendant(of: find.byKey(const Key('simple-menu')), matching: find.byKey(const Key('simple-device-filterwheel'))),
        findsOneWidget, reason: 'its Devices row opens the same window');
    await _openSection(tester, 'Filter wheel');
    expect(find.descendant(of: find.byKey(const Key('simple-menu')), matching: find.byKey(const Key('simple-device-filterwheel'))),
        findsOneWidget);
  });

  testWidgets('on a short window a tall device window stops above the bottom and scrolls', (tester) async {
    // #1666 re-audit P2: an 8-slot wheel's list ran off a landscape phone with no way to reach it.
    final c = await _pump(tester, const Size(1024, 520), linkUp: true, gear: [
      filterWheelProvider.overrideWith(() => _FilterWheel(EquipmentConnectionState.connected, slots: 8)),
    ]);
    c.read(selectedSettingsPanelProvider.notifier).select('eq.filterwheel');
    c.read(selectedTabIndexProvider.notifier).select(kOptionsTabIndex);
    await tester.pumpAndSettle();
    final panel = find.ancestor(of: find.byKey(const Key('simple-device-filterwheel')), matching: find.byType(SimplePanel));
    expect(tester.getRect(panel).bottom, lessThanOrEqualTo(520));
    final last = find.byKey(const Key('simple-filterwheel-slot-7'));
    await tester.ensureVisible(last);
    await tester.pumpAndSettle();
    expect(tester.getRect(last).bottom, lessThanOrEqualTo(tester.getRect(panel).bottom));
  });

  testWidgets('the rotator\'s dot opens its window and closes it again; Settings lists it under Devices',
      (tester) async {
    final c = await _pump(tester, const Size(1024, 768), linkUp: true, gear: [
      rotatorProvider.overrideWith(() => _Rotator(EquipmentConnectionState.connected)),
    ]);
    Future<void> dot() async {
      c.read(selectedSettingsPanelProvider.notifier).select('eq.rotator');
      c.read(selectedTabIndexProvider.notifier).select(kOptionsTabIndex);
      await tester.pumpAndSettle();
    }
    await dot();
    expect(find.byKey(const Key('simple-device-rotator')), findsOneWidget);
    expect(find.byType(SettingsShell), findsNothing, reason: 'never the full UI\'s Settings page');
    await dot();
    expect(find.byKey(const Key('simple-device-rotator')), findsNothing);
    await _openSection(tester, 'Devices');
    await tester.tap(find.textContaining('Falcon'));
    await tester.pump();
    expect(find.descendant(of: find.byKey(const Key('simple-menu')), matching: find.byKey(const Key('simple-device-rotator'))),
        findsOneWidget, reason: 'its Devices row opens the same window');
    await _openSection(tester, 'Rotator');
    expect(find.descendant(of: find.byKey(const Key('simple-menu')), matching: find.byKey(const Key('simple-device-rotator'))),
        findsOneWidget);
  });

  testWidgets('the flat panel\'s dot opens its window and closes it again; Settings lists it under Devices',
      (tester) async {
    final c = await _pump(tester, const Size(1024, 768), linkUp: true, gear: [
      flatPanelProvider.overrideWith(() => _FlatPanel(EquipmentConnectionState.connected)),
    ]);
    Future<void> dot() async {
      c.read(selectedSettingsPanelProvider.notifier).select('eq.flat');
      c.read(selectedTabIndexProvider.notifier).select(kOptionsTabIndex);
      await tester.pumpAndSettle();
    }
    await dot();
    expect(find.byKey(const Key('simple-device-flat')), findsOneWidget);
    expect(find.byType(SettingsShell), findsNothing, reason: 'never the full UI\'s Settings page');
    await dot();
    expect(find.byKey(const Key('simple-device-flat')), findsNothing);
    await _openSection(tester, 'Devices');
    await tester.tap(find.textContaining('Flip-Flat'));
    await tester.pump();
    expect(find.descendant(of: find.byKey(const Key('simple-menu')), matching: find.byKey(const Key('simple-device-flat'))),
        findsOneWidget, reason: 'its Devices row opens the same window');
    await _openSection(tester, 'Flat panel');
    expect(find.descendant(of: find.byKey(const Key('simple-menu')), matching: find.byKey(const Key('simple-device-flat'))),
        findsOneWidget);
  });

  testWidgets('the weather station\'s dot opens its window and closes it again; Settings lists it under Devices',
      (tester) async {
    final c = await _pump(tester, const Size(1024, 768), linkUp: true, gear: [
      weatherProvider.overrideWith(() => _Weather(EquipmentConnectionState.connected)),
    ]);
    Future<void> dot() async {
      c.read(selectedSettingsPanelProvider.notifier).select('eq.weather');
      c.read(selectedTabIndexProvider.notifier).select(kOptionsTabIndex);
      await tester.pumpAndSettle();
    }
    await dot();
    expect(find.byKey(const Key('simple-device-weather')), findsOneWidget);
    expect(find.byType(SettingsShell), findsNothing, reason: 'never the full UI\'s Settings page');
    await dot();
    expect(find.byKey(const Key('simple-device-weather')), findsNothing);
    await _openSection(tester, 'Devices');
    await tester.tap(find.textContaining('CloudWatcher'));
    await tester.pump();
    expect(find.descendant(of: find.byKey(const Key('simple-menu')), matching: find.byKey(const Key('simple-device-weather'))),
        findsOneWidget, reason: 'its Devices row opens the same window');
    await _openSection(tester, 'Weather');
    expect(find.descendant(of: find.byKey(const Key('simple-menu')), matching: find.byKey(const Key('simple-device-weather'))),
        findsOneWidget);
  });

  testWidgets('Settings lists the weather station only while the rig knows one', (tester) async {
    await _pump(tester, const Size(1024, 768), linkUp: true);
    await tester.tap(find.byTooltip('Settings'));
    await tester.pump();
    expect(find.byKey(const Key('simple-menu-nav-Weather')), findsNothing);
  });

  testWidgets('Settings lists the flat panel only while the rig knows one', (tester) async {
    await _pump(tester, const Size(1024, 768), linkUp: true);
    await tester.tap(find.byTooltip('Settings'));
    await tester.pump();
    expect(find.byKey(const Key('simple-menu-nav-Flat panel')), findsNothing);
  });

  testWidgets('Settings lists the filter wheel only while the rig knows one', (tester) async {
    await _pump(tester, const Size(1024, 768), linkUp: true);
    await tester.tap(find.byTooltip('Settings'));
    await tester.pump();
    expect(find.byKey(const Key('simple-menu-nav-Filter wheel')), findsNothing);
  });

  testWidgets("the Switches dot opens the Switches window, not the full UI's page; Settings lists it under Devices (#1651)",
      (tester) async {
    final c = await _pump(tester, const Size(1024, 768), linkUp: true, gear: [
      switchListProvider.overrideWith(
          () => _Switches([_switch('box', 'Pegasus Powerbox', SwitchConnectionState.connected)])),
    ]);
    c.read(selectedSettingsPanelProvider.notifier).select('eq.switch');
    c.read(selectedTabIndexProvider.notifier).select(kOptionsTabIndex);
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('simple-device-switches')), findsOneWidget);
    expect(find.byType(SettingsShell), findsNothing, reason: 'never the full UI\'s Settings page');
    await _openSection(tester, 'Devices');
    await tester.tap(find.textContaining('Pegasus Powerbox'));
    await tester.pump();
    expect(find.descendant(of: find.byKey(const Key('simple-menu')), matching: find.byKey(const Key('simple-device-switches'))),
        findsOneWidget, reason: 'its Devices row opens the same window');
    await _openSection(tester, 'Switches');
    expect(find.descendant(of: find.byKey(const Key('simple-menu')), matching: find.byKey(const Key('simple-device-switches'))),
        findsOneWidget);
  });

  testWidgets('Devices lists each switch device: Disconnect, Connect, Reconnect after a failure', (tester) async {
    final switches = _Switches([
      _switch('a', 'Box A', SwitchConnectionState.connected),
      _switch('b', 'Box B', SwitchConnectionState.disconnected),
      _switch('c', 'Box C', SwitchConnectionState.error),
    ]);
    await _pump(tester, const Size(1024, 768), linkUp: true, gear: [switchListProvider.overrideWith(() => switches)]);
    await _openSection(tester, 'Devices');
    await tester.tap(find.byKey(const Key('simple-gear-switch-a-disconnect')));
    await tester.tap(find.byKey(const Key('simple-gear-switch-b-connect')));
    await tester.tap(find.byKey(const Key('simple-gear-switch-c-reconnect')));
    await tester.pump();
    expect(switches.calls, ['disconnect a', 'connect b', 'connect c']);
  });

  testWidgets("Settings › Switches lists each device's readings under Sensors; Rig no longer does", (tester) async {
    SwitchDevice box(String id, String name, List<SwitchPort> ports) => SwitchDevice(
        deviceId: id, alpacaDeviceNumber: 0, name: name, connectionState: SwitchConnectionState.connected, ports: ports);
    await _pump(tester, const Size(1024, 768), linkUp: true, gear: [
      switchListProvider.overrideWith(() => _Switches([
            box('box', 'Pegasus Powerbox', const [
              SwitchPort(id: 0, name: 'Dew 1', value: 60, min: 0, max: 100, canWrite: true),
              SwitchPort(id: 1, name: 'Input voltage', value: 12.4, min: 0, max: 20, canWrite: false),
              SwitchPort(id: 2, name: 'Raining', value: 0, min: 0, max: 1, canWrite: false),
            ]),
            box('hub', 'USB hub', const [SwitchPort(id: 0, name: 'Port 1', value: 1, min: 0, max: 1, canWrite: true)]),
          ])),
    ]);
    await _openSection(tester, 'Rig');
    expect(find.text('Input voltage'), findsNothing, reason: 'readings moved to Switches (user, 2026-11-07)');
    await _openSection(tester, 'Switches');
    final powerbox = find.byKey(const Key('simple-switch-device-box'));
    await tester.ensureVisible(find.descendant(of: powerbox, matching: find.text('Input voltage')));
    expect(find.descendant(of: powerbox, matching: find.text('Sensors')), findsOneWidget);
    expect(find.descendant(of: powerbox, matching: find.text('12.4 V')), findsOneWidget, reason: 'with its unit (#1683)');
    expect(find.descendant(of: powerbox, matching: find.text('Raining')), findsOneWidget);
    final hub = find.byKey(const Key('simple-switch-device-hub'));
    expect(find.descendant(of: hub, matching: find.text('Sensors')), findsNothing, reason: 'no readings, no table');
  });

  testWidgets('Settings lists Switches only while the rig knows a switch device', (tester) async {
    await _pump(tester, const Size(1024, 768), linkUp: true,
        gear: [switchListProvider.overrideWith(() => _Switches(const []))]);
    await tester.tap(find.byTooltip('Settings'));
    await tester.pump();
    expect(find.byKey(const Key('simple-menu-nav-Switches')), findsNothing);
  });

  testWidgets('the guider\'s dot opens the Guider window; Settings lists it under Devices', (tester) async {
    final c = await _pump(tester, const Size(1024, 768), linkUp: true, gear: [
      guiderStatusProvider.overrideWith(() => _GuiderUp()),
    ]);
    c.read(selectedSettingsPanelProvider.notifier).select('eq.guider');
    c.read(selectedTabIndexProvider.notifier).select(kOptionsTabIndex);
    // A guiding guider's dot blinks (working): pump a while, never settles.
    for (var i = 0; i < 5; i++) {
      await tester.pump(const Duration(milliseconds: 200));
    }
    expect(find.byKey(const Key('simple-device-guider')), findsOneWidget);
    expect(find.byType(SettingsShell), findsNothing);
    await _openSection(tester, 'Guider');
    expect(find.descendant(of: find.byKey(const Key('simple-menu')), matching: find.byKey(const Key('simple-device-guider'))),
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
    // (The top bar says it too.)
    expect(find.descendant(of: find.byKey(const Key('simple-gear')), matching: find.text('Rig not connected')),
        findsOneWidget);
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

    testWidgets('the graph fills its panel, not half of it (iPad, 2026-11-04)', (tester) async {
      await _pump(tester, size, steps: _someSteps, gear: [guiderStatusProvider.overrideWith(_GuiderUp.new)]);
      await tester.pump();
      final panel = guiding(tester);
      final trace = tester.getRect(find.byKey(const Key('simple-guide-trace')));
      expect(trace.width, greaterThan(panel.width * 0.6), reason: 'the labels take only what they need');
      expect(trace.height, greaterThanOrEqualTo(panel.height - 16), reason: 'nearly the whole height');
    });

    testWidgets('a stopped guider hides the guiding graph, old steps or not (user, 2026-11-04)', (tester) async {
      await _pump(tester, size, steps: _someSteps, gear: [guiderStatusProvider.overrideWith(() => _GuiderAt('stopped'))]);
      await tester.pump();
      expect(find.byKey(const Key('simple-guide-trace')), findsNothing);
    });

    testWidgets('guiding alone fills the row', (tester) async {
      await _pump(tester, size, steps: _someSteps, gear: [guiderStatusProvider.overrideWith(_GuiderUp.new)]);
      await tester.pump();
      // RA and Dec are named, not told apart by colour alone (WCAG 1.4.1).
      final trace = find.ancestor(of: find.byKey(const Key('simple-guide-trace')), matching: find.byType(Row)).first;
      expect(find.descendant(of: trace, matching: find.text('RA')), findsOneWidget);
      expect(find.descendant(of: trace, matching: find.text('Dec')), findsOneWidget);
      expect(find.descendant(of: trace, matching: find.text('Guider')), findsOneWidget,
          reason: 'Guider, not Guiding: it shows while looping or calibrating too (user, 2026-11-04)');
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
      final c = await _pump(tester, size, steps: _someSteps, histogram: histogram,
          gear: [guiderStatusProvider.overrideWith(_GuiderUp.new)]);
      await _frame(tester, c);
      await tester.pump();
      final g = guiding(tester), h = hist(tester);
      expect(g.left, kSimpleInset);
      expect(h.right, end(tester));
      expect(h.left - g.right, kSimpleInset);
      expect(g.width, closeTo(h.width, 0.5));
    });
  });

  testWidgets('the Info tiles sit above guiding while it shows, at the inset without it (ALP-4935)',
      (tester) async {
    final guider = _GuiderAt('guiding');
    final container = await _pump(tester, const Size(1024, 768), steps: _someSteps,
        gear: [guiderStatusProvider.overrideWith(() => guider)]);
    await _frame(tester, container);
    await tester.tap(find.byKey(const Key('simple-info')));
    await tester.pump(const Duration(milliseconds: 300));
    const above = 768 - kSimpleInset * 2 - kSimpleBarHeight;
    expect(tester.getRect(find.byType(SimpleInfoPanel)).bottom, above, reason: 'just above the guiding row');
    guider.at('stopped'); // the guider stopped: the guiding row goes
    guider.rig = 'stopped';
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 300));
    expect(tester.getRect(find.byType(SimpleInfoPanel)).bottom, 768 - kSimpleInset, reason: 'the row went: down');
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

  testWidgets('while the guider runs, Simple Mode re-reads it, so a stop that lands later shows (rig, 2026-11-04)',
      (tester) async {
    // Nothing else polls the guider in Simple Mode: Stop's one read came before the guider had stopped.
    final guider = _GuiderAt('star_lost');
    await _pump(tester, const Size(1024, 768), gear: [guiderStatusProvider.overrideWith(() => guider)]);
    await tester.pump();
    expect(find.descendant(of: find.byType(SimpleStatusPill), matching: find.textContaining('Star lost')), findsOneWidget);
    guider.rig = 'stopped'; // the guider stopped; nothing pushes it
    await tester.pump(const Duration(seconds: 3));
    expect(guider.refreshes, greaterThan(0));
    expect(find.descendant(of: find.byType(SimpleStatusPill), matching: find.textContaining('Star lost')), findsNothing);
    await tester.pump(const Duration(seconds: 5));
    final idle = guider.refreshes;
    await tester.pump(const Duration(seconds: 5));
    expect(guider.refreshes, idle, reason: 'idle: no more polling');
  });

  testWidgets('concurrent things each get a pill, stacked: a frame and the guider both show (user, 2026-11-04)',
      (tester) async {
    final guider = _GuiderAt('guiding');
    final c = await _pump(tester, const Size(1024, 768), gear: [guiderStatusProvider.overrideWith(() => guider)]);
    c.read(captureProgressProvider.notifier).beginExposing(const Duration(seconds: 5));
    await tester.pump();
    final pills = find.byKey(const Key('simple-status-pill'));
    expect(pills, findsNWidgets(2), reason: 'the frame and the guider, not one hiding the other');
    final frame = tester.getRect(find.ancestor(of: find.textContaining('Taking a frame'), matching: pills));
    final guiding = tester.getRect(find.ancestor(of: find.textContaining('Guiding'), matching: pills));
    expect(frame.bottom, lessThanOrEqualTo(guiding.top), reason: 'the frame first, the guider under it');
    expect(frame.right, guiding.right, reason: 'right-aligned under the strip');
    c.read(captureProgressProvider.notifier).reset();
    await tester.pump(const Duration(seconds: 1));
  });

  testWidgets("a Loop the daemon still runs is adopted on connect: the shutter shows its square and stops it "
      "(rig 2026-11-05: the app died mid-Loop and every shot was refused with nothing to stop)", (tester) async {
    final rig = _LoopOnRig();
    final c = await _pump(tester, const Size(1024, 768), server: const AraServer(hostname: 'h', port: 5555),
        gear: [liveViewApiFactoryProvider.overrideWithValue((_) => rig)]);
    await tester.pump();
    expect(c.read(liveViewControllerProvider), isTrue);
    expect(find.byKey(const Key('simple-stop-square')), findsOneWidget, reason: 'the Loop reads as running');
    expect(rig.calls, isEmpty, reason: 'adopted, not started again');
    await tester.tap(find.byKey(const Key('simple-shutter')));
    await tester.pump();
    expect(rig.calls, ['stop']);
    expect(c.read(liveViewControllerProvider), isFalse);
    await tester.pumpWidget(const SizedBox());
    c.dispose();
    await tester.pump(const Duration(seconds: 5));
  });

  testWidgets('a tap on a message opens what it came from, as the status pills do (user, 2026-11-04)', (tester) async {
    final polar = _PolarRun();
    await _pump(tester, const Size(1024, 768), gear: [polarAlignLiveProvider.overrideWith(() => polar)]);
    await tester.pump();
    polar.fail('no stars found');
    await tester.pump();
    await tester.tap(find.textContaining('Polar alignment failed'));
    await tester.pump();
    expect(find.byType(SimplePolarPanel), findsOneWidget, reason: "Polar align's panel");
    await tester.pump(kSimpleErrorLife + const Duration(seconds: 1));
  });

  testWidgets('a failed polar alignment says so, with why, as a message on the left that fades (user, 2026-11-04)',
      (tester) async {
    final polar = _PolarRun();
    final c = await _pump(tester, const Size(1024, 768), gear: [polarAlignLiveProvider.overrideWith(() => polar)]);
    await tester.pump();
    polar.fail('no stars found');
    await tester.pump();
    final m = c.read(simpleMessagesProvider).single;
    expect((m.kind, m.title, m.detail), (SimpleMessageKind.error, 'Polar alignment failed', 'no stars found'));
    expect(find.descendant(of: find.byType(SimpleStatusPill), matching: find.textContaining('no stars found')),
        findsNothing, reason: 'not a sticky status pill');
    await tester.pump(kSimpleErrorLife + const Duration(seconds: 1));
    expect(c.read(simpleMessagesProvider), isEmpty);
  });

  testWidgets('a tap on the status pill opens what it is about (user, 2026-11-04)', (tester) async {
    final guider = _GuiderAt('calibrating');
    final c = await _pump(tester, const Size(1024, 768), gear: [guiderStatusProvider.overrideWith(() => guider)]);
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pump();
    await tester.tap(find.byKey(const Key('simple-mode-focus')));
    await tester.pump();
    // The guider's routine: Guide mode, its panel open.
    await tester.tap(find.byKey(const Key('simple-status-pill')).first);
    await tester.pump();
    expect(find.byType(SimpleGuidePanel), findsOneWidget);
    // A frame: Preview, its panel open.
    c.read(captureProgressProvider.notifier).beginExposing(const Duration(seconds: 5));
    await tester.pump();
    // The frame's pill comes first, above the guider's.
    await tester.tap(find.byKey(const Key('simple-status-pill')).first);
    await tester.pump();
    expect(find.byKey(const Key('simple-exposure')), findsOneWidget, reason: "Preview's panel");
    expect(find.descendant(of: find.byKey(const Key('simple-mode')), matching: find.text('Preview')), findsOneWidget);
    c.read(captureProgressProvider.notifier).reset();
    await tester.pump(const Duration(seconds: 1));
  });

  testWidgets('past three pills, the "+n" fold: 44 pt tall, read as how many more, and Tab + Enter opens it '
      '(impeccable audit, 2026-11-04)', (tester) async {
    final mount = _ParkingMount();
    final c = await _pump(tester, const Size(1024, 768), gear: [
      mountProvider.overrideWith(() => mount),
      polarAlignLiveProvider.overrideWith(() => _PolarRun()),
      guiderStatusProvider.overrideWith(() => _GuiderAt('calibrating')),
    ]);
    await tester.pump();
    c.read(captureProgressProvider.notifier).beginExposing(const Duration(seconds: 5));
    await c.read(simpleMountMotionProvider.notifier).park(true);
    await tester.pump();
    final fold = find.text('+1');
    expect(fold, findsOneWidget, reason: 'frame, park, polar align shown; the guider folded');
    final box = find.ancestor(of: fold, matching: find.byType(SimplePanel)).first;
    expect(tester.getSize(box).height, greaterThanOrEqualTo(44), reason: 'DESIGN.md: 44 pt targets');
    expect(tester.getSemantics(fold), matchesSemantics(label: '1 more status', isButton: true, hasTapAction: true,
        isFocusable: true, hasFocusAction: true));
    FocusManager.instance.highlightStrategy = FocusHighlightStrategy.alwaysTraditional;
    addTearDown(() => FocusManager.instance.highlightStrategy = FocusHighlightStrategy.automatic);
    Focus.of(tester.element(fold)).requestFocus();
    await tester.pump();
    expect(find.ancestor(of: fold, matching: find.byWidgetPredicate((w) =>
        w is DecoratedBox && (w.decoration as BoxDecoration).border != null)), findsOneWidget,
        reason: 'a visible focus ring');
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();
    expect(find.byType(SimpleGuidePanel), findsOneWidget, reason: 'the first one folded: the guider');
    c.read(captureProgressProvider.notifier).reset();
    await tester.pump(const Duration(seconds: 1));
  });

  testWidgets('a status pill takes Tab and Enter like a tap (impeccable audit, 2026-11-04)', (tester) async {
    await _pump(tester, const Size(1024, 768), gear: [guiderStatusProvider.overrideWith(() => _GuiderAt('calibrating'))]);
    await tester.pump();
    Focus.of(tester.element(find.descendant(of: find.byKey(const Key('simple-status-pill')), matching: find.byType(Row)).first))
        .requestFocus();
    await tester.pump();
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();
    expect(find.byType(SimpleGuidePanel), findsOneWidget);
  });

  testWidgets('a message that opens its origin is a button and takes Tab and Enter (impeccable audit, 2026-11-04)',
      (tester) async {
    final polar = _PolarRun();
    await _pump(tester, const Size(1024, 768), gear: [polarAlignLiveProvider.overrideWith(() => polar)]);
    await tester.pump();
    polar.fail('no stars found');
    await tester.pump();
    final msg = find.textContaining('Polar alignment failed');
    expect(tester.getSemantics(find.ancestor(of: msg, matching: find.byType(SimpleTapTarget))).flagsCollection.isButton,
        isTrue);
    Focus.of(tester.element(msg)).requestFocus();
    await tester.pump();
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();
    expect(find.byType(SimplePolarPanel), findsOneWidget);
    await tester.pump(kSimpleErrorLife + const Duration(seconds: 1));
  });

  testWidgets('the status pill reports the guider: its routine, then Guiding with the RMS; a frame comes first',
      (tester) async {
    // User, 2026-11-04: nothing on screen said the guider was calibrating.
    final guider = _GuiderAt('calibrating');
    final container = await _pump(tester, const Size(1024, 768), gear: [guiderStatusProvider.overrideWith(() => guider)]);
    await tester.pump();
    String pill() => tester
        .widgetList<RichText>(find.descendant(of: find.byType(SimpleStatusPill), matching: find.byType(RichText)))
        .map((t) => t.text.toPlainText())
        .join(' ');
    expect(pill(), allOf(contains('Guider'), contains('Calibrating'), contains('West step 3')));
    expect(tester.getSemantics(find.byKey(const Key('simple-status-pill')).first), matchesSemantics(label: 'Guider, Calibrating', isLiveRegion: true, isButton: true, hasTapAction: true),
        reason: 'each step is not read out');
    guider.at('guiding');
    await tester.pump();
    expect(pill(), allOf(contains('Guiding'), contains('0.62″')));
    container.read(captureProgressProvider.notifier).beginExposing(const Duration(seconds: 5));
    await tester.pump();
    expect(pill(), contains('Taking a frame'), reason: 'the frame\'s pill first, the guider\'s under it (user, 2026-11-04)');
    container.read(captureProgressProvider.notifier).reset();
    await tester.pump(const Duration(seconds: 1));
  });

  testWidgets("the status pill keeps its width while its numbers change (ALP-4937)", (tester) async {
    // On the iPad (2026-11-03) the pill jittered narrower as '10 s left' became '9 s left'. The test font's
    // glyphs are a full em wide, so at 1x the pill sits at its 300 pt cap; at half size it hugs its words.
    tester.platformDispatcher.textScaleFactorTestValue = 0.5;
    addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
    final container = await _pump(tester, const Size(1024, 768));
    container.read(captureProgressProvider.notifier).beginExposing(const Duration(seconds: 12));
    await tester.pump();
    await tester.pump();
    double width() => tester.getSize(find.descendant(of: find.byType(SimpleStatusPill), matching: find.byType(SimplePanel))).width;
    final wide = width();
    await tester.pump(const Duration(seconds: 3)); // 12 s left → 9 s left: one digit fewer
    await tester.pump();
    expect(find.textContaining('9 s left'), findsOneWidget, reason: 'precondition: one digit');
    expect(width(), lessThan(kSimpleStatusMaxWidth), reason: 'precondition: below the cap');
    expect(width(), wide, reason: 'it never narrows within a state');
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

  testWidgets('#1596 the plan pill says a run is waiting at the meridian', (tester) async {
    String pill() => find
        .descendant(of: find.byType(SimpleStatusPill), matching: find.byType(RichText))
        .evaluate()
        .map((e) => (e.widget as RichText).text.toPlainText())
        .join();
    const plan = SimpleRun(sequenceId: 'seq-1', started: true, targets: [
      SimpleTarget(name: 'M31', steps: [FilterPlanStep(filterName: 'L', exposureSeconds: 120, frameCount: 30)]),
    ]);
    await _pump(tester, const Size(1024, 768), plan: plan,
        runState: const SequenceRunStateInfo(
            sequenceId: 'seq-1', state: SequenceRunState.running, currentInstructionDescription: 'Taking frames',
            reasonCode: 'meridian', reasonText: 'Passing meridian — 00:10:00'));
    await tester.pump();
    expect(pill(), contains('Passing meridian — 00:10:00'));
  });

  testWidgets('#1596 the plan pill keeps the reason a run skipped its only target', (tester) async {
    String pill() => find
        .descendant(of: find.byType(SimpleStatusPill), matching: find.byType(RichText))
        .evaluate()
        .map((e) => (e.widget as RichText).text.toPlainText())
        .join();
    const plan = SimpleRun(sequenceId: 'seq-1', started: true, targets: [
      SimpleTarget(name: 'M31', steps: [FilterPlanStep(filterName: 'L', exposureSeconds: 120, frameCount: 30)]),
    ]);
    await _pump(tester, const Size(1024, 768), plan: plan,
        runState: const SequenceRunStateInfo(
            sequenceId: 'seq-1', state: SequenceRunState.completed,
            reasonCode: 'target_skipped', reasonText: 'Skipped, nothing was imaged: M31 did not run'));
    await tester.pump();
    // Simple Mode's own words, keyed on the code, not the daemon's English text.
    expect(pill(), contains('Skipped: conditions not met'));
    expect(pill(), isNot(contains('M31 did not run')));
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
    expect(tester.getSemantics(find.byKey(const Key('simple-status-pill')).first), matchesSemantics(
        label: 'Plan, Cooling camera to -10 °C', isLiveRegion: true, isButton: true, hasTapAction: true),
        reason: 'and a tap opens Plan (user, 2026-11-04)');
    // A frame in hand: its own pill, first, the plan's step still under it (user, 2026-11-04).
    container.read(captureProgressProvider.notifier).beginExposing(const Duration(seconds: 5));
    await tester.pump();
    expect(pill(), contains('Taking a frame'));
    expect(pill(), contains('Cooling camera to -10 °C'));
    container.read(captureProgressProvider.notifier).reset();
    await tester.pump(const Duration(seconds: 1));
  });

  testWidgets('parking from Simple Mode: the status pill says Parking until the mount is parked', (tester) async {
    final mount = _ParkingMount();
    final c = await _pump(tester, const Size(1024, 768), gear: [mountProvider.overrideWith(() => mount)]);
    await tester.pump();
    String pill() => find
        .descendant(of: find.byType(SimpleStatusPill), matching: find.byType(RichText))
        .evaluate()
        .map((e) => (e.widget as RichText).text.toPlainText())
        .join();
    await c.read(simpleMountMotionProvider.notifier).park(true);
    await tester.pump();
    expect(pill(), contains('Parking'));
    mount.set(parked: true, runtime: 'parked');
    await tester.pump();
    expect(pill(), isNot(contains('Parking')));
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

  testWidgets('a filter change shows in the status pill until the wheel lands; a hand swap asks there too', (tester) async {
    final wheel = _FilterWheel(EquipmentConnectionState.connected, slots: 3);
    await _pump(tester, const Size(1024, 768), linkUp: true, gear: [filterWheelProvider.overrideWith(() => wheel)]);
    await tester.pump();
    final pill = find.byType(SimpleStatusPill);
    expect(find.descendant(of: pill, matching: find.textContaining('Filter wheel')), findsNothing);
    wheel.report({'state': 'moving', 'current_slot': -1});
    await tester.pump();
    expect(find.descendant(of: pill, matching: find.textContaining('Filter wheel')), findsOneWidget);
    expect(find.descendant(of: pill, matching: find.textContaining('Moving…')), findsOneWidget);
    // Re-audit 2: moving is working, blinking green, as the wheel's dot and window show it (DESIGN.md status rule).
    final dot = tester.widget<StatusDot>(find.descendant(of: pill, matching: find.byType(StatusDot)));
    expect((dot.color, dot.ring), (AraColors.accentConnected, false));
    expect(tester.widget<StatusBlink>(find.descendant(of: pill, matching: find.byType(StatusBlink))).on, isTrue);
    wheel.report({'state': 'idle', 'current_slot': 1});
    await tester.pump();
    expect(find.descendant(of: pill, matching: find.textContaining('Filter wheel')), findsNothing, reason: 'landed');
    wheel.report({'state': 'awaiting_user', 'current_slot': 1, 'pending_slot': 2}, manual: true);
    await tester.pump();
    expect(find.descendant(of: pill, matching: find.textContaining('Swap in F2, then confirm.')), findsOneWidget);
  });

  testWidgets('a switch port change shows in the status pill, naming the port, until the rig answers (#1668)',
      (tester) async {
    final c = await _pump(tester, const Size(1024, 768), linkUp: true, gear: [
      switchListProvider.overrideWith(
          () => _Switches([_switch('box', 'Pegasus Powerbox', SwitchConnectionState.connected)])),
    ]);
    await tester.pump();
    final pill = find.byType(SimpleStatusPill);
    expect(find.descendant(of: pill, matching: find.textContaining('Switches')), findsNothing);
    c.read(switchActingProvider.notifier).set(true);
    c.read(simpleSwitchSettingProvider.notifier).set('Dew 1');
    await tester.pump();
    expect(find.descendant(of: pill, matching: find.textContaining('Switches')), findsOneWidget);
    expect(find.descendant(of: pill, matching: find.textContaining('Setting Dew 1…')), findsOneWidget);
    // A change on its way is changing: a blue ring (DESIGN.md status rule).
    final dot = tester.widget<StatusDot>(find.descendant(of: pill, matching: find.byType(StatusDot)));
    expect((dot.color, dot.ring), (AraColors.accentInfo, true));
    await tester.tap(find.descendant(of: pill, matching: find.textContaining('Setting Dew 1…')));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('simple-device-switches')), findsOneWidget, reason: 'a tap opens the window');
    c.read(switchActingProvider.notifier).set(false);
    c.read(simpleSwitchSettingProvider.notifier).set(null);
    await tester.pump();
    expect(find.descendant(of: pill, matching: find.textContaining('Switches')), findsNothing, reason: 'answered');
  });

  testWidgets('the focuser moving shows in the status pill, blinking green, until it stops; a tap opens its window',
      (tester) async {
    final focuser = _Focuser(EquipmentConnectionState.connected);
    await _pump(tester, const Size(1024, 768), linkUp: true, gear: [focuserProvider.overrideWith(() => focuser)]);
    await tester.pump();
    final pill = find.byType(SimpleStatusPill);
    expect(find.descendant(of: pill, matching: find.textContaining('Focuser')), findsNothing);
    focuser.report('moving');
    await tester.pump();
    expect(find.descendant(of: pill, matching: find.textContaining('Focuser')), findsOneWidget);
    expect(find.descendant(of: pill, matching: find.textContaining('Moving…')), findsOneWidget);
    expect(tester.widget<StatusBlink>(find.descendant(of: pill, matching: find.byType(StatusBlink))).on, isTrue);
    await tester.tap(find.descendant(of: pill, matching: find.textContaining('Focuser')));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 300));
    expect(find.byKey(const Key('simple-device-focuser')), findsOneWidget, reason: 'the pill opens its window');
    focuser.report('idle');
    await tester.pump();
    expect(find.descendant(of: pill, matching: find.textContaining('Focuser')), findsNothing, reason: 'stopped');
  });

  testWidgets('the rotator turning shows in the status pill, blinking green, until it stops; a tap opens its window',
      (tester) async {
    final rotator = _Rotator(EquipmentConnectionState.connected);
    await _pump(tester, const Size(1024, 768), linkUp: true, gear: [rotatorProvider.overrideWith(() => rotator)]);
    await tester.pump();
    final pill = find.byType(SimpleStatusPill);
    expect(find.descendant(of: pill, matching: find.textContaining('Rotator')), findsNothing);
    rotator.report('moving');
    await tester.pump();
    expect(find.descendant(of: pill, matching: find.textContaining('Rotator')), findsOneWidget);
    expect(find.descendant(of: pill, matching: find.textContaining('Moving…')), findsOneWidget);
    final dot = tester.widget<StatusDot>(find.descendant(of: pill, matching: find.byType(StatusDot)));
    expect((dot.color, dot.ring), (AraColors.accentConnected, false));
    expect(tester.widget<StatusBlink>(find.descendant(of: pill, matching: find.byType(StatusBlink))).on, isTrue);
    await tester.tap(find.descendant(of: pill, matching: find.textContaining('Rotator')));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 300));
    expect(find.byKey(const Key('simple-device-rotator')), findsOneWidget, reason: 'the pill opens its window');
    rotator.report('idle');
    await tester.pump();
    expect(find.descendant(of: pill, matching: find.textContaining('Rotator')), findsNothing, reason: 'stopped');
  });

  testWidgets('the flat panel\'s cover moving and light warming show in the status pill; a tap opens its window',
      (tester) async {
    final flat = _FlatPanel(EquipmentConnectionState.connected);
    await _pump(tester, const Size(1024, 768), linkUp: true, gear: [flatPanelProvider.overrideWith(() => flat)]);
    await tester.pump();
    final pill = find.byType(SimpleStatusPill);
    expect(find.descendant(of: pill, matching: find.textContaining('Flat panel')), findsNothing);
    flat.report('cover_moving', warming: true);
    await tester.pump();
    expect(find.descendant(of: pill, matching: find.textContaining('Cover moving…')), findsOneWidget,
        reason: 'the cover moving wins over the light warming up');
    var dot = tester.widget<StatusDot>(find.descendant(of: pill, matching: find.byType(StatusDot)));
    expect((dot.color, dot.ring), (AraColors.accentConnected, false));
    expect(tester.widget<StatusBlink>(find.descendant(of: pill, matching: find.byType(StatusBlink))).on, isTrue);
    await tester.tap(find.descendant(of: pill, matching: find.textContaining('Flat panel')));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 300));
    expect(find.byKey(const Key('simple-device-flat')), findsOneWidget, reason: 'the pill opens its window');
    flat.report('light_on', warming: true);
    await tester.pump();
    expect(find.descendant(of: pill, matching: find.textContaining('Light warming up…')), findsOneWidget);
    dot = tester.widget<StatusDot>(find.descendant(of: pill, matching: find.byType(StatusDot)));
    expect((dot.color, dot.ring), (AraColors.accentInfo, true));
    flat.report('light_on');
    await tester.pump();
    expect(find.descendant(of: pill, matching: find.textContaining('Flat panel')), findsNothing, reason: 'ready');
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

  testWidgets('the safety alarm shows over a screen pushed over Simple Mode (the Image Library, full Settings)',
      (tester) async {
    final container = await _pump(tester, const Size(1024, 768), manualAlarm: true);
    Navigator.of(tester.element(find.byType(SimpleShell)))
        .push(MaterialPageRoute<void>(builder: (_) => const Scaffold(body: Text('Image Library'))));
    await tester.pumpAndSettle();
    expect(find.text('Image Library'), findsOneWidget);
    (container.read(safetyAlarmProvider.notifier) as _ManualAlarm).raise('Safety monitor: unsafe (rain)');
    await tester.pump();
    final alarm = find.byKey(const Key('simple-safety-alarm'));
    expect(alarm.hitTestable(), findsOneWidget, reason: 'on top, not under the pushed screen');
    await tester.tap(find.byKey(const Key('simple-safety-silence')));
    await tester.pump();
    expect(alarm, findsNothing);
  });

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
    final en = lookupAppLocalizations(const Locale('en'));
    expect(simpleUptime(11520), '3 h 12 min');
    expect(simpleUptime(300), '5 min');
    expect(simpleUptime(2 * 86400 + 4 * 3600), '2 d 4 h');
    expect(simpleFree(en, 412 * 1073741824), '412 GB free');
    expect(simpleFree(en, 1536 * 1048576), '1.5 GB free');
    expect(simpleFree(en, null), 'Unplugged');
    expect(simpleMemory(512 * 1048576), '512 MB');
    expect(simpleMemory(1536 * 1048576), '1.5 GB');
  });

  test('Simple search: settings only for the devices there, modes by name, each imaged target once', () {
    final en = lookupAppLocalizations(const Locale('en'));
    expect(simpleSearch(en, 'c'), isEmpty, reason: 'two characters at least');
    final cool = simpleSearch(en, 'cool', sections: {'Rig', 'Devices', 'Camera'});
    expect(cool.whereType<SimpleSettingHit>().map((h) => (h.title, h.section)),
        [('Cooler', 'Camera'), ('Cooler target', 'Camera')]);
    expect(simpleSearch(en, 'cool'), isEmpty, reason: 'no camera');
    expect(simpleSearch(en, 'polar').whereType<SimpleModeHit>().map((h) => h.mode), [SimpleModeScreen.polarAlign]);
    expect(simpleSearch(en, 'm 31', targets: ['M 31', 'M 31', 'M 33']).whereType<SimpleImageHit>().map((h) => h.target),
        ['M 31']);
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

  testWidgets('the selected Settings section reads at 4.5:1: link blue, not Info Blue on the tint', (tester) async {
    await _pump(tester, const Size(1024, 768));
    await _openSection(tester, 'Rig');
    final nav = find.byKey(const Key('simple-menu-nav-Rig'));
    // Info Blue on the selection tint is 4.3:1 over a white frame (WCAG 1.4.3).
    final title = tester.widget<Text>(find.descendant(of: nav, matching: find.byType(Text)));
    expect(title.style?.color, AraColors.linkText);
    final icon = tester.widget<Icon>(find.descendant(of: nav, matching: find.byType(Icon)));
    expect(icon.color, AraColors.linkText);
  });

  testWidgets('an interrupted hold on the elevation stepper stops, and sets nothing', (tester) async {
    final c = await _pump(tester, const Size(1024, 768));
    await _openSection(tester, 'Rig');
    final before = c.read(siteSettingsProvider).elevationM;
    final up = find.byKey(const Key('simple-rig-elevation-up'));
    await tester.ensureVisible(up);
    await tester.pump();
    final g = await tester.startGesture(tester.getCenter(up));
    await tester.pump(const Duration(milliseconds: 600)); // the hold starts
    await tester.pump(const Duration(milliseconds: 300));
    await g.cancel(); // iPadOS took the touch (Control Center, a call)
    await tester.pump();
    await tester.pump(const Duration(seconds: 2));
    expect(find.text('${before.round()} m'), findsOneWidget, reason: 'the run stopped and the held value was dropped');
    expect(c.read(siteSettingsProvider).elevationM, before);
  });

  testWidgets('Search tells screen readers what it found', (tester) async {
    await _pump(tester, const Size(1024, 768));
    Future<void> search(String q) async {
      if (find.byKey(const Key('simple-search')).evaluate().isEmpty) {
        await tester.tap(find.byKey(const Key('simple-search-button')));
        await tester.pump();
      }
      await tester.enterText(find.byKey(const Key('simple-search-field')), q);
      await tester.pump();
      await tester.pump();
    }
    final status = find.byKey(const Key('simple-search-status'));
    await search('eleva');
    expect(tester.getSemantics(status), isSemantics(label: '1 match', isLiveRegion: true));
    await search('cool');
    expect(tester.getSemantics(status),
        isSemantics(label: 'Nothing in Simple Mode matches "cool".', isLiveRegion: true));
  });

  testWidgets('a new camera reading rebuilds neither Settings nor Search; only the camera coming or going does',
      (tester) async {
    final cam = _CoolingCamera(-5);
    await _pump(tester, const Size(1024, 768), gear: [cameraStatusProvider.overrideWith(() => cam)]);
    await tester.pump();
    bool dirty(Finder f) => tester.element(f).dirty;
    await tester.tap(find.byTooltip('Settings'));
    await tester.pump();
    final sheet = find.ancestor(
        of: find.byKey(const Key('simple-menu')),
        matching: find.byWidgetPredicate((w) => w.runtimeType.toString() == '_SettingsSheet'));
    cam.set(-6); // the 1 s poll
    expect(dirty(sheet), isFalse, reason: 'its sections only change when a device appears or goes');
    await tester.pump();
    await tester.tap(find.byKey(const Key('simple-search-button')));
    await tester.pump();
    cam.set(-7);
    expect(dirty(find.byType(SimpleSearchPanel)), isFalse, reason: 'no catalog scan per camera poll');
  });

  testWidgets('the Change buttons are 44 pt, like every Simple Mode target', (tester) async {
    await _pump(tester, const Size(1024, 768));
    await _openSection(tester, 'Rig');
    for (final id in ['rig', 'profile']) {
      expect(tester.getSize(find.byKey(Key('simple-rig-change-$id'))).height, greaterThanOrEqualTo(44), reason: id);
    }
  });

  testWidgets('a Rig wheel turns with the arrow keys and sets the value', (tester) async {
    final c = await _pump(tester, const Size(1024, 768));
    await _openSection(tester, 'Rig');
    final before = c.read(siteSettingsProvider).latitudeDeg;
    final lat = find.byKey(const Key('simple-rig-lat'));
    await tester.ensureVisible(lat);
    await tester.pump();
    await tester.tap(lat);
    await tester.pump();
    // Wheel 1 is the tens of degrees.
    expect(await _tabToLabel(tester, 'simple-rig-lat-wheel-1'), isTrue, reason: 'Tab reaches each wheel');
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    await tester.pumpAndSettle();
    expect(c.read(siteSettingsProvider).latitudeDeg, closeTo(before.abs() + 10, 0.001));
  });
}
