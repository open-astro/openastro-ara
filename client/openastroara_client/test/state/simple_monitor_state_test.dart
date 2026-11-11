import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/camera_status.dart';
import 'package:openastroara/models/focuser_status.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/models/mount_status.dart';
import 'package:openastroara/models/safety_monitor_status.dart';
import 'package:openastroara/models/sequence/imaging_run_body.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/models/weather_status.dart';
import 'package:openastroara/services/storage_space_api.dart';
import 'package:openastroara/services/tonight_sky_api.dart';
import 'package:openastroara/state/equipment/camera_state.dart';
import 'package:openastroara/state/equipment/focuser_state.dart';
import 'package:openastroara/state/equipment/mount_state.dart';
import 'package:openastroara/state/equipment/safety_monitor_state.dart';
import 'package:openastroara/state/equipment/weather_state.dart';
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_monitor_state.dart';
import 'package:openastroara/state/simple_run_state.dart';
import 'package:openastroara/state/sky_atlas/tonight_sky_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';

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

class _Clock extends MonitorClock {
  @override
  DateTime build() => ref.watch(clockProvider)();
}

class _Policies extends SafetyPoliciesNotifier {
  @override
  SafetyPolicies build() => const SafetyPolicies(weatherTriggersEnabled: true);
}

class _Link extends Notifier<bool> {
  @override
  bool build() => true;
  void set(bool v) => state = v;
}

final _link = NotifierProvider<_Link, bool>(_Link.new);

final _t0 = DateTime(2026, 11, 11, 23, 52);

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
}) =>
    SequenceRunStateInfo(
      sequenceId: 'seq-1',
      runId: runId,
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
}) async {
  late _Rig rig;
  final camN = _Cam(camera ? cam ?? _cam() : null);
  final guiderN = _Guider(guider ? guiderStatus ?? _guider() : null);
  final runN = _Run(run);
  final c = ProviderContainer(overrides: [
    clockProvider.overrideWithValue(() => rig.now),
    monitorClockProvider.overrideWith(_Clock.new),
    serverLinkUpProvider.overrideWith((ref) => ref.watch(_link)),
    skyClockProvider.overrideWith((ref) => const Stream<int>.empty()),
    wsEventsProvider.overrideWith((ref) => const Stream.empty()),
    sequenceRunStateProvider.overrideWith(() => runN),
    simpleRunProvider.overrideWith(() => _Plan(plan)),
    cameraStatusProvider.overrideWith(() => camN),
    mountProvider.overrideWith(() => _Mount(mount ?? MountStatus.fromJson(_wire('connected', {'tracking': true})))),
    focuserProvider.overrideWith(() => _Focuser(focuser)),
    guiderStatusProvider.overrideWith(() => guiderN),
    safetyMonitorProvider.overrideWith(() => _Safety(safety
        ? SafetyMonitorStatus.fromJson({..._wire('connected'), 'safe': safe, 'last_transition_at': '2026-11-11T07:14:00Z'})
        : null)),
    weatherProvider.overrideWith(() => _Weather(weather ? WeatherStatus.fromJson({..._wire('connected'), ...weatherReadings}) : null)),
    storageSpaceProvider.overrideWith((ref) async => StorageSpace(saveDirectory: '/d', isFallback: false, freeBytes: freeBytes)),
    tonightSkyProvider.overrideWith((ref) async => sky),
    safetyPoliciesProvider.overrideWith(_Policies.new),
  ]);
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

    test('Unsafe beats a question; the safety monitor or weather past its limit', () async {
      final r = await _rig(run: _running(), safe: false);
      r.c.read(simpleMessagesProvider.notifier).show(SimpleMessageKind.question, 'Swap in Ha',
          actions: [SimpleMessageAction('Confirm', () {})]);
      expect(r.read().verdict.kind, MonitorVerdictKind.unsafe);
      final windy = await _rig(run: _running(), weatherReadings: const {'wind_speed_ms': 12.0});
      final v = windy.read().verdict;
      expect((v.kind, v.breach?.kind), (MonitorVerdictKind.unsafe, WeatherBreach.wind));
      expect(windy.read().weather!.cells.single.overLimit, 36, reason: 'the wind cell marked with its limit');
    });

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

    test('a meridian flip in progress, the wait before it, a pause by Ara and the wait for dark', () async {
      final flipping = await _rig(run: _running(flip: const MeridianFlipInfo(state: 'flipping')));
      expect(flipping.read().verdict.kind, MonitorVerdictKind.flipping);
      final wait = await _rig(run: _running(reasonCode: 'meridian', waitLeft: 272));
      expect(wait.read().verdict.kind, MonitorVerdictKind.waitingForFlip);
      expect(wait.read().verdict.at, _t0.add(const Duration(seconds: 272)));
      final paused = await _rig(run: _running(state: SequenceRunState.paused));
      expect((paused.read().verdict.kind, paused.read().verdict.status), (MonitorVerdictKind.pausedByAra, MonitorStatus.changing));
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
        expect((s.guider != null, s.safety != null, s.weather != null), (guider, safety, weather));
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

    test("no plan: tonight's best", () async {
      final r = await _rig(plan: const SimpleRun(), sky: [
        TonightSkyObject.fromJson({'id': 'm42', 'name': 'M 42', 'type': 'nebula', 'ra_deg': 83.8, 'dec_deg': -5.4, 'altitude_deg': 62.0, 'max_altitude_deg': 74.0})!,
      ]);
      final s = r.read();
      expect(s.tonightsBest, isTrue);
      expect((s.upNext.single.kind, s.upNext.single.name, s.upNext.single.altitudeDeg), (MonitorUpKind.best, 'M 42', 62.0));
    });
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
      expect(r.read().lastFrame.frames.first.id, 'f5');
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
}
