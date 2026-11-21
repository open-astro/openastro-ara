import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter/widgets.dart' show Locale;
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/autofocus_run.dart';
import 'package:openastroara/models/camera_status.dart';
import 'package:openastroara/models/focuser_status.dart';
import 'package:openastroara/models/frame_histogram.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/models/mount_status.dart';
import 'package:openastroara/models/safety_monitor_status.dart';
import 'package:openastroara/models/sequence/imaging_run_body.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/models/stats/guiding_rms.dart';
import 'package:openastroara/models/weather_status.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/screens/simple/simple_monitor_screen.dart' show monitorVerdictText;
import 'package:openastroara/services/storage_space_api.dart';
import 'package:openastroara/services/tonight_sky_api.dart';
import 'package:openastroara/state/equipment/camera_state.dart';
import 'package:openastroara/state/focus/autofocus_live_state.dart';
import 'package:openastroara/state/equipment/focuser_state.dart';
import 'package:openastroara/state/equipment/mount_state.dart';
import 'package:openastroara/state/equipment/safety_monitor_state.dart';
import 'package:openastroara/state/equipment/weather_state.dart';
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';
import 'package:openastroara/state/settings/loaded_section.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_monitor_state.dart';
import 'package:openastroara/state/simple_run_state.dart';
import 'package:openastroara/state/sky_atlas/tonight_sky_state.dart';
import 'package:openastroara/state/stats/guiding_rms_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/widgets/imaging/histogram_strip.dart';

// The Monitor state module (#1724): rig, run and frame set through fakes, and
// the snapshot read as the screen would.

Map<String, dynamic> _wire(String state, [Map<String, dynamic> runtime = const {}]) =>
    {'device_id': 'd', 'name': 'x', 'state': state, 'runtime': runtime};

class _Cam extends CameraStatusNotifier {
  _Cam(this.v);
  final CameraStatus? v;
  @override
  Future<CameraStatus?> build() async => v;
  void set(CameraStatus s) => state = AsyncData(s);
}

class _Mount extends MountNotifier {
  _Mount(this.v);
  final MountStatus? v;
  @override
  Future<MountStatus?> build() async => v;
}

class _Focuser extends FocuserNotifier {
  _Focuser(this.v);
  final FocuserStatus? v;
  @override
  Future<FocuserStatus?> build() async => v;
}

class _AfLive extends AutofocusLiveNotifier {
  _AfLive(this.run);
  final AutofocusRun run;
  @override
  AutofocusLive build() => AutofocusLive(run: run);
}

class _Guider extends GuiderStatusNotifier {
  _Guider(this.v);
  final GuiderStatus? v;
  @override
  Future<GuiderStatus?> build() async => v;
  void set(GuiderStatus s) => state = AsyncData(s);
}

class _Safety extends SafetyMonitorNotifier {
  _Safety(this.v);
  final SafetyMonitorStatus? v;
  @override
  Future<SafetyMonitorStatus?> build() async => v;
}

class _Weather extends WeatherNotifier {
  _Weather(this.v);
  final WeatherStatus? v;
  @override
  Future<WeatherStatus?> build() async => v;
}

class _Run extends SequenceRunStateNotifier {
  _Run(this.v);
  final SequenceRunStateInfo? v;
  @override
  Future<SequenceRunStateInfo?> build() async => v;
  void set(SequenceRunStateInfo s) => state = AsyncData(s);
}

class _Plan extends SimpleRunNotifier {
  _Plan(this.v);
  final SimpleRun v;
  @override
  SimpleRun build() => v;
}

class _Rms extends GuidingRmsNotifier {
  _Rms(this.v);
  final GuidingRmsSeries? v;
  @override
  Future<GuidingRmsSeries?> build() async => v;
  var refreshes = 0;
  @override
  Future<void> refresh() async => refreshes++;
}

class _Clock extends MonitorClock {
  @override
  DateTime build() => ref.watch(clockProvider)();
}

class _Policies extends SafetyPoliciesNotifier {
  @override
  LoadedSection<SafetyPolicies> build() => const LoadedSection(
    value: SafetyPolicies(weatherTriggersEnabled: true),
    source: SectionSource.rig,
  );
}

class _Link extends Notifier<bool> {
  @override
  bool build() => true;
  void set(bool v) => state = v;
}

final _link = NotifierProvider<_Link, bool>(_Link.new);

final _t0 = DateTime(2026, 11, 11, 23, 52);

final _histogram = FrameHistogram(
    bins: const [], minAdu: 0, minCount: 0, maxAdu: 65535, maxCount: 0, meanAdu: 900, stdDev: 50, median: 880, mad: 30,
    lowClipFraction: 0, highClipFraction: 0, width: 100, height: 100, bitDepth: 16);

const _m42 = SimpleTarget(name: 'Orion Nebula', tag: 'M 42', steps: [
  FilterPlanStep(filterName: 'L', exposureSeconds: 180, frameCount: 2),
  FilterPlanStep(filterName: 'R', exposureSeconds: 180, frameCount: 2),
]);
const _ic434 = SimpleTarget(name: 'IC 434', steps: [FilterPlanStep(filterName: 'Ha', exposureSeconds: 300, frameCount: 3)]);
const _plan = SimpleRun(sequenceId: 'seq-1', started: true, targets: [_m42, _ic434]);

SequenceRunStateInfo _running({
  int frames = 1,
  SequenceRunState state = SequenceRunState.running,
  MeridianFlipInfo? flip,
  String runId = 'r1',
  double? remaining = 3600,
  String? reasonCode,
  int? waitLeft,
  DateTime? started,
}) =>
    SequenceRunStateInfo(
      sequenceId: 'seq-1',
      runId: runId,
      startedUtc: started,
      state: state,
      framesCaptured: frames,
      meridianFlip: flip,
      estimatedRemainingSeconds: remaining,
      currentInstructionDescription: 'Taking frames',
      reasonCode: reasonCode,
      meridianWaitSecondsLeft: waitLeft,
    );

CameraStatus _cam({String runtime = 'idle', double? pct, bool cooler = false, double? ccd, double? target}) =>
    CameraStatus.fromJson(_wire('connected', {
      'state': runtime,
      'exposure_progress_pct': ?pct,
      'cooler_on': cooler,
      'ccd_temperature': ?ccd,
      'cooler_setpoint_c': ?target,
    }));

GuiderStatus _guider({String state = 'connected', String runtime = 'guiding'}) => GuiderStatus.fromJson({
      'name': 'Guider',
      'state': state,
      'runtime': {'state': runtime, 'rms_total_arcsec': 0.62, 'rms_ra_arcsec': 0.41, 'rms_dec_arcsec': 0.46},
    });

class _Rig {
  _Rig(this.c, this.cam, this.guider, this.run);
  final ProviderContainer c;
  final _Cam cam;
  final _Guider guider;
  final _Run run;
  var now = _t0;

  MonitorSnapshot read() => c.read(monitorProvider);
  void advance(Duration d) {
    now = now.add(d);
    c.invalidate(monitorClockProvider);
  }
}

Future<_Rig> _rig({
  SequenceRunStateInfo? run,
  SimpleRun plan = _plan,
  CameraStatus? cam,
  bool camera = true,
  MountStatus? mount,
  bool guider = true,
  GuiderStatus? guiderStatus,
  bool safety = true,
  bool safe = true,
  bool weather = true,
  Map<String, dynamic> weatherReadings = const {'cloud_cover_pct': 4, 'temperature_c': 8.4, 'dew_point_c': 1.2},
  FocuserStatus? focuser,
  int? freeBytes,
  List<TonightSkyObject> sky = const [],
  GuidingRmsSeries? rms,
  AutofocusRun? autofocus,
  Stream<WsEvent> events = const Stream.empty(),
}) async {
  late _Rig rig;
  final camN = _Cam(camera ? cam ?? _cam() : null);
  final guiderN = _Guider(guider ? guiderStatus ?? _guider() : null);
  final runN = _Run(run);
  final c = ProviderContainer(
    overrides: [
      clockProvider.overrideWithValue(() => rig.now),
      monitorClockProvider.overrideWith(_Clock.new),
      serverLinkUpProvider.overrideWith((ref) => ref.watch(_link)),
      skyClockProvider.overrideWith((ref) => const Stream<int>.empty()),
      wsEventsProvider.overrideWith((ref) => events),
      frameHistogramProvider.overrideWith((ref, id) async => _histogram),
      sequenceRunStateProvider.overrideWith(() => runN),
      simpleRunProvider.overrideWith(() => _Plan(plan)),
      cameraStatusProvider.overrideWith(() => camN),
      mountProvider.overrideWith(
        () => _Mount(
          mount ?? MountStatus.fromJson(_wire('connected', {'tracking': true})),
        ),
      ),
      focuserProvider.overrideWith(() => _Focuser(focuser)),
      guiderStatusProvider.overrideWith(() => guiderN),
      safetyMonitorProvider.overrideWith(
        () => _Safety(
          safety
              ? SafetyMonitorStatus.fromJson({
                  ..._wire('connected'),
                  'safe': safe,
                  'last_transition_at': '2026-11-11T07:14:00Z',
                })
              : null,
        ),
      ),
      weatherProvider.overrideWith(
        () => _Weather(
          weather
              ? WeatherStatus.fromJson({
                  ..._wire('connected'),
                  ...weatherReadings,
                })
              : null,
        ),
      ),
      storageSpaceProvider.overrideWith(
        (ref) async => StorageSpace(
          saveDirectory: '/d',
          isFallback: false,
          freeBytes: freeBytes,
        ),
      ),
      tonightSkyProvider.overrideWith((ref) async => sky),
      safetyPoliciesProvider.overrideWith(_Policies.new),
      guidingRmsProvider.overrideWith(() => _Rms(rms)),
      if (autofocus != null)
        autofocusLiveProvider.overrideWith(() => _AfLive(autofocus)),
    ],
  );
  addTearDown(c.dispose);
  rig = _Rig(c, camN, guiderN, runN);
  final sub = c.listen(monitorProvider, (_, _) {});
  addTearDown(sub.close);
  for (final p in [
    sequenceRunStateProvider.future,
    cameraStatusProvider.future,
    mountProvider.future,
    focuserProvider.future,
    guiderStatusProvider.future,
    safetyMonitorProvider.future,
    weatherProvider.future,
    storageSpaceProvider.future,
    tonightSkyProvider.future,
    guidingRmsProvider.future,
  ]) {
    await c.read(p);
  }
  return rig;
}

void main() {
  group('the top line', () {
    test('all good, imaging the Target in hand', () async {
      final r = await _rig(run: _running());
      expect(r.read().verdict.kind, MonitorVerdictKind.allGood);
      expect(r.read().verdict.target, 'M 42');
      expect(r.read().verdict.status, MonitorStatus.normal);
    });

    test('no plan', () async {
      final r = await _rig(plan: const SimpleRun());
      expect(r.read().verdict.kind, MonitorVerdictKind.noPlan);
    });

    test('a profile device that dropped names it, in red, above everything else', () async {
      final r = await _rig(run: _running(), guiderStatus: _guider(state: 'error'), safe: false);
      final v = r.read().verdict;
      expect((v.kind, v.device, v.status), (MonitorVerdictKind.deviceDropped, MonitorDevice.guider, MonitorStatus.fault));
    });

    test('a device not connected yet tonight has not dropped; one that was connected and is lost has', () async {
      final r = await _rig(run: _running(), guiderStatus: _guider(state: 'disconnected', runtime: 'stopped'));
      expect(r.read().verdict.kind, MonitorVerdictKind.allGood, reason: 'never connected this session');
      expect(r.read().guider!.status, MonitorStatus.off, reason: 'its tile stays, grey');
      r.guider.set(_guider());
      await Future<void>.delayed(Duration.zero);
      r.read();
      r.guider.set(_guider(state: 'disconnected', runtime: 'stopped'));
      await Future<void>.delayed(Duration.zero);
      expect(r.read().verdict.device, MonitorDevice.guider);
      final err = await _rig(run: _running(), guiderStatus: _guider(state: 'error'));
      expect(err.read().verdict.device, MonitorDevice.guider, reason: 'an error is a fault however it began');
    });

    test('a Target with no rows reads without failing', () async {
      final r = await _rig(run: _running(), plan: const SimpleRun(sequenceId: 'seq-1', started: true, targets: [SimpleTarget(name: 'M 1')]));
      expect(r.read().now.target, 'M 1');
      expect(r.read().now.filter, isNull);
    });

    test(
      'Unsafe beats a question; the safety monitor or weather past its limit',
      () async {
        final r = await _rig(run: _running(), safe: false);
        r.c
            .read(simpleMessagesProvider.notifier)
            .show(
              SimpleMessageKind.question,
              'Swap in Ha',
              actions: [SimpleMessageAction('Confirm', () {})],
            );
        expect(r.read().verdict.kind, MonitorVerdictKind.unsafe);
        final windy = await _rig(
          run: _running(),
          weatherReadings: const {'wind_speed_ms': 12.0},
        );
        final v = windy.read().verdict;
        expect(
          (v.kind, v.breach?.kind),
          (MonitorVerdictKind.unsafe, WeatherBreach.wind),
        );
        expect(
          windy.read().conditions!.weather!.cells.single.overLimit,
          36,
          reason: 'the wind cell marked with its limit',
        );
      },
    );

    test('a question waits on the top line with its answers, above a lost guide star', () async {
      final r = await _rig(run: _running(), guiderStatus: _guider(runtime: 'star_lost'));
      expect(r.read().verdict.kind, MonitorVerdictKind.starLost);
      r.c.read(simpleMessagesProvider.notifier).show(SimpleMessageKind.question, 'Swap in Ha',
          actions: [SimpleMessageAction('Confirm', () {})]);
      final v = r.read().verdict;
      expect((v.kind, v.question?.title, v.question?.actions.single.label),
          (MonitorVerdictKind.question, 'Swap in Ha', 'Confirm'));
    });

    test("Emergency Stop's outcome and an error take the top line, above a question (re-audit)", () async {
      final r = await _rig(run: _running());
      final m = r.c.read(simpleMessagesProvider.notifier);
      m.show(SimpleMessageKind.question, 'Swap in Ha', actions: [SimpleMessageAction('Confirm', () {})]);
      m.show(SimpleMessageKind.warning, 'Emergency Stop: done', detail: 'Check the rig', sticky: true);
      final v = r.read().verdict;
      expect((v.kind, v.message?.title, v.status), (MonitorVerdictKind.message, 'Emergency Stop: done', MonitorStatus.warning));
      m.show(SimpleMessageKind.error, "Couldn't stop: no rig", sticky: true);
      expect((r.read().verdict.message?.title, r.read().verdict.status), ("Couldn't stop: no rig", MonitorStatus.fault),
          reason: 'the newest');
      m.dismissQuestions();
      for (final x in [...r.c.read(simpleMessagesProvider)]) {
        m.dismiss(x.id);
      }
      expect(r.read().verdict.kind, MonitorVerdictKind.allGood);
    });

    test('a plain error ranks below Unsafe and a dropped device; a sticky one stays above them (re-audit 3)', () async {
      final r = await _rig(run: _running(), safe: false);
      final m = r.c.read(simpleMessagesProvider.notifier);
      m.show(SimpleMessageKind.error, "Autofocus didn't converge");
      expect(r.read().verdict.kind, MonitorVerdictKind.unsafe, reason: 'Unsafe outranks a passing error');
      m.show(SimpleMessageKind.error, 'Emergency Stop did NOT happen', sticky: true);
      expect(r.read().verdict.message?.title, 'Emergency Stop did NOT happen');
    });

    test('with the link lost, a failed Stop still takes the top line (re-audit 3)', () async {
      final r = await _rig(run: _running());
      r.read();
      r.c.read(_link.notifier).set(false);
      r.read();
      expect(r.read().verdict.kind, MonitorVerdictKind.linkLost);
      r.c.read(simpleMessagesProvider.notifier).show(SimpleMessageKind.error, 'Emergency Stop did NOT happen', sticky: true);
      final v = r.read().verdict;
      expect((v.kind, v.message?.title), (MonitorVerdictKind.message, 'Emergency Stop did NOT happen'));
      expect(r.read().frozen, isTrue, reason: 'the numbers stay frozen');
    });

    test("a Stop that went through (amber, sticky) waits behind link lost, a dropped device and Unsafe (re-audit 4)", () async {
      final r = await _rig(run: _running(), safe: false);
      final m = r.c.read(simpleMessagesProvider.notifier);
      m.show(SimpleMessageKind.warning, 'Emergency Stop: done', detail: 'Check the rig', sticky: true);
      expect(r.read().verdict.kind, MonitorVerdictKind.unsafe, reason: 'a done summary left up all night must not hide Unsafe');
      final calm = await _rig(run: _running());
      calm.c.read(simpleMessagesProvider.notifier).show(SimpleMessageKind.warning, 'Emergency Stop: done', sticky: true);
      expect(calm.read().verdict.kind, MonitorVerdictKind.message, reason: 'with nothing worse it still shows');
      calm.c.read(_link.notifier).set(false);
      calm.read();
      expect(calm.read().verdict.kind, MonitorVerdictKind.linkLost, reason: 'link lost keeps its age on the top line');
    });

    test('the pass-meridian wait inside a flipping trigger is the wait, not the flip (#1897)', () async {
      final r = await _rig(run: _running(flip: const MeridianFlipInfo(state: 'flipping'), reasonCode: 'meridian', waitLeft: 182));
      expect(r.read().verdict.kind, MonitorVerdictKind.waitingForFlip);
      expect(r.read().now.word, MonitorNowWord.waitingForFlip);
      expect(r.read().flip.step, isNull, reason: 'no step text under a wait');
      expect(r.read().flip.state, 'waiting_for_meridian');
      expect(r.read().flip.at, _t0.add(const Duration(seconds: 182)));
    });

    test('once the countdown clears, a flipping state is the flip again (#1897)', () async {
      final r = await _rig(run: _running(flip: const MeridianFlipInfo(state: 'flipping'), reasonCode: 'meridian'));
      expect(r.read().verdict.kind, MonitorVerdictKind.flipping);
      expect(r.read().now.word, MonitorNowWord.flipping);
      expect(r.read().flip.state, 'flipping');
      expect(r.read().flip.step, 'Taking frames');
    });

    test('a meridian flip in progress, the wait before it, a pause and the wait for dark', () async {
      final flipping = await _rig(run: _running(flip: const MeridianFlipInfo(state: 'flipping')));
      expect(flipping.read().verdict.kind, MonitorVerdictKind.flipping);
      final wait = await _rig(run: _running(reasonCode: 'meridian', waitLeft: 272));
      expect(wait.read().verdict.kind, MonitorVerdictKind.waitingForFlip);
      expect(wait.read().verdict.at, _t0.add(const Duration(seconds: 272)));
      final paused = await _rig(run: _running(state: SequenceRunState.paused));
      expect((paused.read().verdict.kind, paused.read().verdict.status), (MonitorVerdictKind.paused, MonitorStatus.changing));
      // The run state does not say who paused it, and a person's own pause never resumes on its own.
      expect(monitorVerdictText(lookupAppLocalizations(const Locale('en')), paused.read(), _t0), ('Paused', null));
      final dusk = await _rig(plan: SimpleRun(sequenceId: 'seq-1', started: true, targets: const [_m42], startAt: _t0.add(const Duration(hours: 1))));
      expect(dusk.read().verdict.kind, MonitorVerdictKind.waitingForDark);
      expect(dusk.read().now.countdownTo, _t0.add(const Duration(hours: 1)));
    });
  });

  group('low disk and the cooler', () {
    test('low disk: free space under the frames left at two bytes a pixel, and none before a first frame', () async {
      // 6 frames left at 1000 × 1000 × 2 bytes = 12 MB.
      final low = await _rig(run: _running(), freeBytes: 11999999);
      expect(low.read().verdict.kind, MonitorVerdictKind.allGood, reason: 'no frame yet, so no size to go by');
      low.c.read(monitorTrendsProvider.notifier).add(const MonitorFrame(id: 'f1', width: 1000, height: 1000));
      expect(low.read().verdict.kind, MonitorVerdictKind.lowDisk);
      final room = await _rig(run: _running(), freeBytes: 12000000);
      room.c.read(monitorTrendsProvider.notifier).add(const MonitorFrame(id: 'f1', width: 1000, height: 1000));
      expect(room.read().verdict.kind, MonitorVerdictKind.allGood);
    });

    test('the cooler warns once off its target by more than 1 °C for 5 minutes in a row', () async {
      final r = await _rig(run: _running(), cam: _cam(cooler: true, ccd: -9.5, target: -10));
      r.c.read(monitorCoolerOffProvider);
      r.advance(const Duration(minutes: 10));
      expect(r.read().verdict.kind, MonitorVerdictKind.allGood, reason: 'within 1 °C');
      r.cam.set(_cam(cooler: true, ccd: -8.5, target: -10));
      await Future<void>.delayed(Duration.zero);
      r.advance(const Duration(minutes: 4, seconds: 59));
      expect(r.read().verdict.kind, MonitorVerdictKind.allGood);
      r.advance(const Duration(seconds: 1));
      expect(r.read().verdict.kind, MonitorVerdictKind.cooler);
      r.cam.set(_cam(cooler: true, ccd: -9.0, target: -10));
      await Future<void>.delayed(Duration.zero);
      expect(r.read().verdict.kind, MonitorVerdictKind.allGood, reason: 'back within 1 °C');
    });
  });

  group('Conditions (#2014)', () {
    FocuserStatus focuser(double? temp) =>
        FocuserStatus.fromJson(_wire('connected', {'temperature': ?temp}));

    test('the camera and focuser group holds sensor, target, cooler power and focuser temperature', () async {
      final r = await _rig(
        run: _running(),
        weather: false,
        cam: CameraStatus.fromJson(
          _wire('connected', {
            'cooler_on': true,
            'ccd_temperature': -9.6,
            'cooler_setpoint_c': -10,
            'cooler_power_pct': 62,
          }),
        ),
        focuser: focuser(7.5),
      );
      final c = r.read().conditions!;
      expect(c.weather, isNull, reason: 'no weather station');
      expect(
        (
          c.rig!.sensorC,
          c.rig!.sensorTargetC,
          c.rig!.coolerPowerPct,
          c.rig!.focuserC,
        ),
        (-9.6, -10.0, 62.0, 7.5),
      );
    });

    test('a reading the device does not report is absent, not zero', () async {
      final r = await _rig(
        run: _running(),
        weather: false,
        cam: _cam(cooler: true, ccd: -9.6),
        focuser: focuser(null),
      );
      final rig = r.read().conditions!.rig!;
      expect(rig.sensorC, -9.6);
      expect(
        (rig.sensorTargetC, rig.coolerPowerPct, rig.focuserC),
        (null, null, null),
      );
    });

    test('the change since the last autofocus is in Conditions', () async {
      final r = await _rig(
        run: _running(),
        weather: false,
        focuser: focuser(6.7),
        autofocus: AutofocusRun(
          state: AutofocusRunStates.complete,
          completedUtc: DateTime.utc(2026, 11, 11, 22, 50),
          focuserTemperatureC: 7.5,
        ),
      );
      final rig = r.read().conditions!.rig!;
      expect(rig.tempSinceAutofocus, closeTo(-0.8, 1e-9));
      expect(rig.autofocusAt, isNotNull);
    });

    test('a focuser with no probe still shows the last autofocus', () async {
      final r = await _rig(
        run: _running(),
        weather: false,
        focuser: focuser(null),
        autofocus: AutofocusRun(
          state: AutofocusRunStates.complete,
          completedUtc: DateTime.utc(2026, 11, 11, 22, 50),
          focuserTemperatureC: 7.5,
        ),
      );
      expect(r.read().conditions?.rig?.autofocusAt, isNotNull);
    });

    test(
      'no weather station and nothing the rig reports: no Conditions at all',
      () async {
        final r = await _rig(run: _running(), weather: false);
        expect(r.read().conditions, isNull);
      },
    );

    test('weather alone: its cells, no camera and focuser group', () async {
      final r = await _rig(run: _running());
      final c = r.read().conditions!;
      expect(c.weather, isNotNull);
      expect(c.rig, isNull);
    });
  });

  group('tiles follow the profile', () {
    for (final (name, guider, safety, weather) in const [
      ('full rig', true, true, true),
      ('no guider', false, true, true),
      ('no safety monitor', true, false, true),
      ('no weather station', true, true, false),
      ('none of the three', false, false, false),
    ]) {
      test(name, () async {
        final r = await _rig(run: _running(), guider: guider, safety: safety, weather: weather);
        final s = r.read();
        expect(
          (s.guider != null, s.safety != null, s.conditions?.weather != null),
          (guider, safety, weather),
        );
      });
    }

    test('a profile device that disconnects keeps its tile, in red', () async {
      final r = await _rig(run: _running());
      r.read();
      r.guider.set(_guider(state: 'disconnected'));
      await Future<void>.delayed(Duration.zero);
      final g = r.read().guider!;
      expect((g.status, g.word), (MonitorStatus.fault, MonitorGuiderWord.disconnected));
    });
  });

  test('link lost: the numbers freeze, the age counts, and they go live again after', () async {
    final r = await _rig(run: _running(frames: 1));
    final live = r.read();
    r.c.read(_link.notifier).set(false);
    r.read();
    r.advance(const Duration(seconds: 42));
    r.run.set(_running(frames: 3));
    await Future<void>.delayed(Duration.zero);
    final frozen = r.read();
    expect(frozen.frozen, isTrue);
    expect(frozen.verdict.kind, MonitorVerdictKind.linkLost);
    expect(frozen.verdict.at, _t0);
    expect(frozen.asOf, live.asOf, reason: 'countdowns stop');
    expect(frozen.now.frame, live.now.frame, reason: 'values kept');
    r.c.read(_link.notifier).set(true);
    await Future<void>.delayed(Duration.zero);
    final back = r.read();
    expect(back.frozen, isFalse);
    expect(back.now.frame, 4);
  });

  group('Up next', () {
    test('the next frame, the flip, the next Target and the park, in time order', () async {
      final r = await _rig(
        run: _running(flip: MeridianFlipInfo(state: 'due_at', expectedUtc: _t0.add(const Duration(minutes: 5)).toUtc())),
        cam: _cam(runtime: 'exposing', pct: 50),
      );
      final up = r.read().upNext;
      expect(up.map((u) => u.kind), [MonitorUpKind.nextFrame, MonitorUpKind.meridianFlip, MonitorUpKind.nextTarget, MonitorUpKind.park]);
      expect((up.first.frame, up.first.name, up.first.at), (3, 'R', _t0.add(const Duration(seconds: 90))));
      expect(up[2].name, 'IC 434');
    });

    test('a frame time that is not known reads "then"', () async {
      final r = await _rig(run: _running());
      expect(r.read().upNext.first.at, isNull);
    });

    test(
      "no plan: Up next stays empty with Tonight's Best candidates up",
      () async {
        final r = await _rig(
          plan: const SimpleRun(),
          sky: [
            TonightSkyObject.fromJson({
              'id': 'M 42',
              'name': 'M 42',
              'type': 'nebula',
              'ra_deg': 83.8,
              'dec_deg': -5.4,
              'altitude_deg': 62.0,
              'max_altitude_deg': 74.0,
            })!,
          ],
        );
        expect(r.read().upNext, isEmpty);
      },
    );
  });

  group('the meridian flip tile', () {
    for (final state in const ['due_at', 'waiting_for_exposure', 'flipping', 'auto_flip_off', 'none_tonight']) {
      test(state, () async {
        final r = await _rig(run: _running(flip: MeridianFlipInfo(state: state, expectedUtc: DateTime.utc(2026, 11, 11, 12))));
        expect(r.read().flip.state, state);
        expect(r.read().flip.step, state == 'flipping' ? 'Taking frames' : null);
      });
    }

    test('no flip field with no active run', () async {
      final r = await _rig(plan: const SimpleRun());
      expect(r.read().flip.state, isNull);
    });
  });

  group('trends', () {
    test('one per saved frame, capped at 200, reset on a new run', () async {
      final r = await _rig(run: _running());
      final t = r.c.read(monitorTrendsProvider.notifier);
      for (var i = 0; i < 205; i++) {
        t.add(MonitorFrame(id: 'f$i', hfr: 2.0));
      }
      expect(r.read().lastFrame.frames.length, 200);
      expect(
        [for (final f in r.read().lastFrame.frames.take(4)) f.id],
        ['f0', 'f1', 'f2', 'f8'],
        reason: 'the run\'s first frames stay, the oldest after them go',
      );
      t.add(const MonitorFrame(id: 'f204', hfr: 2.4));
      expect(r.read().lastFrame.frames.length, 200, reason: 'the same frame read again replaces its point');
      expect(r.read().lastFrame.last!.hfr, 2.4);
      r.run.set(_running(runId: 'r2'));
      await Future<void>.delayed(Duration.zero);
      expect(r.read().lastFrame.frames, isEmpty);
    });

    test('a value the analysis lacks stays missing', () async {
      final r = await _rig(run: _running());
      r.c.read(monitorTrendsProvider.notifier).add(const MonitorFrame(id: 'f1', stars: 10));
      expect(r.read().lastFrame.last!.hfr, isNull);
    });

    test('the clipped share with zero stars, and when the daemon did not count them', () {
      expect(monitorClippedShare(0, 0), 0);
      expect(monitorClippedShare(null, null), isNull);
      expect(monitorClippedShare(1412, 6), closeTo(6 / 1418, 1e-12));
    });
  });

  group('text defects (#1875 slice 7)', () {
    const osc = SimpleTarget(
      name: 'NGC 1269',
      tag: 'NGC1269',
      steps: [FilterPlanStep(filterName: '', exposureSeconds: 60, frameCount: 120)],
    );

    test('a plan row with no filter name gives no filter on Now and none in Up next', () async {
      final r = await _rig(
        run: _running(frames: 26),
        plan: const SimpleRun(sequenceId: 'seq-1', started: true, targets: [osc]),
      );
      expect(r.read().now.filter, isNull);
      expect(r.read().upNext.firstWhere((u) => u.kind == MonitorUpKind.nextFrame).name, isNull);
    });

    test('a long name equal to the short one once spaces are removed is not kept', () async {
      final r = await _rig(
        run: _running(frames: 26),
        plan: const SimpleRun(sequenceId: 'seq-1', started: true, targets: [osc]),
      );
      expect(r.read().target!.name, 'NGC1269');
      expect(r.read().target!.longName, isNull);
    });
  });

  group('session graphs (#1875 slice 7)', () {
    List<double> hfr(List<double> v) => v;

    test('no flag before six saved frames', () {
      expect(monitorDeviation(hfr([2, 2, 2, 3, 3]), higherIsWorse: true), isNull);
    });

    test('the start is the median of the first three, now the median of the last three', () {
      // Medians: first three 2.0 (a 9.0 outlier among them does not count), last three 2.36.
      final d = monitorDeviation([2.0, 9.0, 2.0, 1.0, 2.36, 2.36, 2.4, 2.36], higherIsWorse: true)!;
      expect(d.start, 2.0);
      expect(d.pct, closeTo(18, 1e-9));
      expect(d.flagged, isTrue);
    });

    test('flagged at 15 % the wrong way, not at 14 %, and never for an improvement', () {
      bool flagged(List<double> v, {required bool up}) => monitorDeviation(v, higherIsWorse: up)!.flagged;
      expect(flagged([100, 100, 100, 100, 100, 115, 115, 115], up: true), isTrue);
      expect(flagged([100, 100, 100, 100, 100, 114, 114, 114], up: true), isFalse);
      expect(flagged([100, 100, 100, 100, 100, 85, 85, 85], up: false), isTrue);
      expect(flagged([100, 100, 100, 100, 100, 85, 85, 85], up: true), isFalse, reason: 'HFR falling is better');
      expect(flagged([100, 100, 100, 100, 100, 130, 130, 130], up: false), isFalse, reason: 'stars rising is better');
    });

    test('a zero start gives no percentage', () {
      expect(monitorDeviation([0, 0, 0, 1, 1, 1], higherIsWorse: true), isNull);
    });

    test('the last frame carries the HFR, stars and background deviations from the saved frames', () async {
      final r = await _rig(run: _running());
      final t = r.c.read(monitorTrendsProvider.notifier);
      for (var i = 0; i < 6; i++) {
        t.add(MonitorFrame(id: 'f$i', hfr: i < 3 ? 2.0 : 2.4, stars: i < 3 ? 400 : 320, background: 2000));
      }
      final f = r.read().lastFrame;
      expect(f.hfrDeviation!.flagged, isTrue);
      expect(f.starsDeviation!.pct, closeTo(-20, 1e-9));
      expect(f.backgroundDeviation!.flagged, isFalse);
    });

    test("the guider's RMS is one point per saved frame, from /stats/guiding", () async {
      final r = await _rig(
        run: _running(),
        rms: GuidingRmsSeries(samples: [for (final v in [0.9, 1.0, 1.1, 1.2]) GuidingRmsPoint(rmsArcsec: v)]),
      );
      expect(r.read().guider!.rmsPerFrame, isEmpty, reason: 'no saved frame yet');
      final t = r.c.read(monitorTrendsProvider.notifier);
      t.add(const MonitorFrame(id: 'f0'));
      t.add(const MonitorFrame(id: 'f1'));
      expect(r.read().guider!.rmsPerFrame, [1.1, 1.2], reason: 'the last samples, newest last');
    });

    test("each saved frame's event reads the guider's RMS series again", () async {
      final ws = StreamController<WsEvent>();
      addTearDown(ws.close);
      final r = await _rig(run: _running(), events: ws.stream);
      final rms = r.c.read(guidingRmsProvider.notifier) as _Rms;
      ws.add(WsEvent(type: 'frame.complete', ts: DateTime.utc(2026, 11, 11), seq: 1, payload: const {'frame_id': 'f1'}));
      await Future<void>.delayed(Duration.zero);
      await Future<void>.delayed(Duration.zero);
      expect(r.read().lastFrame.frames.single.id, 'f1');
      expect(rms.refreshes, 1, reason: 'otherwise the RMS graph freezes at the series read when Monitor opened');
    });

    test("the session start stays the run's first frames after the oldest are dropped", () async {
      final r = await _rig(run: _running());
      final t = r.c.read(monitorTrendsProvider.notifier);
      for (var i = 0; i < 205; i++) {
        t.add(MonitorFrame(id: 'f$i', hfr: i < 3 ? 2.0 : 2.6));
      }
      final f = r.read().lastFrame;
      expect(f.frames.length, kMonitorTrendFrames);
      expect(f.hfrDeviation!.start, 2.0);
      expect(f.hfrDeviation!.flagged, isTrue);
    });

    test("the guider's RMS per frame leaves out samples from before the run started", () async {
      final started = DateTime.utc(2026, 11, 15, 21);
      final r = await _rig(
        run: _running(started: started),
        rms: GuidingRmsSeries(
          samples: [
            GuidingRmsPoint(
              timestamp: DateTime.utc(2026, 11, 14, 23),
              rmsArcsec: 0.7,
            ),
            GuidingRmsPoint(
              timestamp: DateTime.utc(2026, 11, 14, 23, 1),
              rmsArcsec: 0.8,
            ),
            GuidingRmsPoint(
              timestamp: started.add(const Duration(minutes: 5)),
              rmsArcsec: 1.3,
            ),
          ],
        ),
      );
      final t = r.c.read(monitorTrendsProvider.notifier);
      for (var i = 0; i < 3; i++) {
        t.add(MonitorFrame(id: 'f$i'));
      }
      expect(r.read().guider!.rmsPerFrame, [1.3]);
    });
  });
}
