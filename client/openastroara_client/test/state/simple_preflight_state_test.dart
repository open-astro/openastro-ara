import 'package:openastroara/models/polar_align.dart'
    show
        PolarAlignDirections,
        PolarAlignLastResult,
        PolarAlignStages,
        PolarAlignStartPlan,
        PolarAlignStates,
        PolarAlignStatus;
import 'package:flutter/foundation.dart' show TargetPlatform;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/autofocus_run.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/state/focus/autofocus_live_state.dart';
import 'package:openastroara/models/mount_status.dart';
import 'package:openastroara/state/equipment/mount_state.dart';
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_run_state.dart';
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/state/setup/setup_readiness.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/simple_preflight_state.dart';

/// The checklist's focus and polar-align states, set by the test.
class _Step extends Notifier<SetupStepState> {
  _Step(this.initial);
  final SetupStepState initial;
  @override
  SetupStepState build() => initial;
  void set(SetupStepState s) => state = s;
}

final _focus = NotifierProvider<_Step, SetupStepState>(() => _Step(SetupStepState.pending));
final _aligned = NotifierProvider<_Step, SetupStepState>(() => _Step(SetupStepState.pending));

/// The view's shortest side, set by the test: 800 is a tablet, under 600 a
/// phone (Companion Mode on Android).
class _Side extends Notifier<double> {
  @override
  double build() => 800;
  void set(double v) => state = v;
}

final _side = NotifierProvider<_Side, double>(_Side.new);

class _PolarApi implements PolarAlignClient {
  @override
  Future<PolarAlignStartPlan> startPlan({String? stage}) async =>
      PolarAlignStartPlan(stage: stage, direction: PolarAlignDirections.east);
  final calls = <String>[];
  @override
  Future<void> start(PolarAlignStartPlan plan) async => calls.add('start');
  @override
  Future<void> stop() async => calls.add('stop');
  @override
  Future<void> complete() async => calls.add('complete');
  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

class _Prefs extends SimpleModePrefsService {
  _Prefs(this.value);
  bool value;
  @override
  Future<bool> load() async => value;
  @override
  Future<void> save(bool enabled) async => value = enabled;
}

class _Plan extends SimpleRunNotifier {
  @override
  SimpleRun build() => const SimpleRun();
  void queued() => state = const SimpleRun(sequenceId: 'seq-1');
  void started() => state = const SimpleRun(sequenceId: 'seq-1', started: true);
  @override
  Future<bool> startQueue({DateTime? startAt}) async {
    started();
    return true;
  }
}

class _Live extends PolarAlignLiveNotifier {
  @override
  PolarAlignLive build() => const PolarAlignLive();
  void running() => state = PolarAlignLive(phase: PolarAlignStates.adjusting);
  void checked() => state = const PolarAlignLive(phase: PolarAlignStates.checked, stage: PolarAlignStages.check);
  void stopped({String? zone}) => state = PolarAlignLive(
    phase: PolarAlignStates.stopped,
    stage: PolarAlignStages.fine,
    zone: zone,
  );
}

class _FocusLive extends AutofocusLiveNotifier {
  @override
  AutofocusLive build() => AutofocusLive.idle;
  void put(AutofocusLive v) => state = v;
}

AutofocusLive _focusRun(String state, {bool manual = false}) => AutofocusLive(
  run: AutofocusRun(
    state: state,
    startedUtc: DateTime.now().toUtc(),
    completedUtc: state == AutofocusRunStates.complete
        ? DateTime.now().toUtc()
        : null,
  ),
  focusedThisSession: true,
  hydrated: true,
  manualMarkSinceRun: manual,
);

/// The rig's status: only its last polar-align result, which the test moves.
class _StatusApi implements PolarAlignClient {
  @override
  Future<PolarAlignStartPlan> startPlan({String? stage}) async =>
      PolarAlignStartPlan(stage: stage, direction: PolarAlignDirections.east);
  PolarAlignLastResult? last;
  String state = PolarAlignStates.idle;
  @override
  Future<PolarAlignStatus?> getStatus() async =>
      PolarAlignStatus(state: state, lastResult: last);
  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

/// What the mount and the guider were asked, in order.
final _asks = <String>[];

class _Mount extends MountNotifier {
  /// Refuses every tracking ask.
  bool refuse = false;
  @override
  Future<MountStatus?> build() async => MountStatus.fromJson({
    'device_id': 'm',
    'name': 'EQ6-R',
    'state': 'connected',
    'capabilities': {'can_set_tracking': true},
    'runtime': {'state': 'idle', 'tracking': false},
  });
  @override
  Future<bool> setTracking(bool enabled) async {
    _asks.add('tracking:$enabled');
    if (refuse) throw StateError('Mount is parked');
    return true;
  }
}

class _Guider extends GuiderStatusNotifier {
  @override
  Future<GuiderStatus?> build() async => GuiderStatus.fromJson({
    'device_id': 'g',
    'name': 'PHD2',
    'state': 'connected',
    'runtime': {'state': 'stopped'},
  });
  @override
  Future<void> startGuiding() async => _asks.add('startGuiding');
}

Future<ProviderContainer> _container({bool prefs = true, bool refuseTracking = false}) async {
  _asks.clear();
  final c = ProviderContainer(
    overrides: [
      polarAlignLiveProvider.overrideWith(_Live.new),
      nightFocusStateProvider.overrideWith((ref) => ref.watch(_focus)),
      nightPolarAlignStateProvider.overrideWith((ref) => ref.watch(_aligned)),
      polarAlignApiProvider.overrideWithValue(_PolarApi()),
      mountProvider.overrideWith(() => _Mount()..refuse = refuseTracking),
      guiderStatusProvider.overrideWith(_Guider.new),
      simplePermanentPierPrefsProvider.overrideWithValue(_Prefs(false)),
      simpleModePrefsProvider.overrideWithValue(_Prefs(prefs)),
      simpleModeFeaturePrefsProvider.overrideWithValue(_Prefs(prefs)),
      simpleRunProvider.overrideWith(_Plan.new),
      companionPlatformProvider.overrideWithValue(TargetPlatform.android),
      companionShortestSideProvider.overrideWith((ref) => ref.watch(_side)),
    ],
  );
  addTearDown(c.dispose);
  await c.read(simplePermanentPierProvider.future);
  c.listen(simplePreflightProvider, (_, _) {});
  c.listen(simpleShowingProvider, (_, _) {});
  return c;
}

/// What is on screen is one value with two writers: the person's pick and
/// Preflight's own steps. The shell only reads it (#1495).
void main() {
  test('Simple Mode opens on Preview', () async {
    final c = await _container();
    expect(c.read(simpleShowingProvider), SimpleModeScreen.preview);
  });

  // Falsified by: simple_preflight_state.dart SimpleShowingNotifier.build, drop the simpleModeProvider listen.
  for (final (name, toggle) in [
    ('the mode', (ProviderContainer c, bool on) => c.read(simpleModeProvider.notifier).set(on)),
    ('the feature', (ProviderContainer c, bool on) => c.read(simpleModeFeatureProvider.notifier).set(on)),
  ]) {
    test('Simple Mode opening again ($name toggled) shows Preview and ends a Preflight left running', () async {
      final c = await _container();
      await c.read(simpleModeProvider.future);
      await c.read(simpleModeFeatureProvider.future);
      expect(c.read(simpleModeActiveProvider), isTrue);
      c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.preflight);
      await toggle(c, false);
      await toggle(c, true);
      expect(c.read(simpleShowingProvider), SimpleModeScreen.preview);
      expect(c.read(simplePreflightProvider), isNull);
    });
  }

  test('picking Preflight starts it on Plan, and Plan is what shows; the pick itself never shows', () async {
    final c = await _container();
    c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.preflight);
    expect(c.read(simplePreflightProvider), SimplePreflightStep.plan);
    expect(c.read(simpleShowingProvider), SimpleModeScreen.plan);
  });

  test('a step moves what shows with it', () async {
    final c = await _container();
    c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.preflight);
    c.read(simplePreflightProvider.notifier).next();
    expect(c.read(simplePreflightProvider), SimplePreflightStep.focus);
    expect(c.read(simpleShowingProvider), SimpleModeScreen.focus);
  });

  test('picking another mode shows it and ends Preflight', () async {
    final c = await _container();
    c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.preflight);
    c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.preview);
    expect(c.read(simplePreflightProvider), isNull);
    expect(c.read(simpleShowingProvider), SimpleModeScreen.preview);
  });

  test('imaging started by hand ends Preflight and leaves Plan on screen', () async {
    final c = await _container();
    c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.preflight);
    c.read(simplePreflightProvider.notifier).next();
    expect(c.read(simpleShowingProvider), SimpleModeScreen.focus);
    (c.read(simpleRunProvider.notifier) as _Plan).started();
    expect(c.read(simplePreflightProvider), isNull);
    expect(c.read(simpleShowingProvider), SimpleModeScreen.plan);
  });

  test('stop() from anywhere leaves Plan on screen', () async {
    final c = await _container();
    c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.preflight);
    c.read(simplePreflightProvider.notifier).next();
    c.read(simplePreflightProvider.notifier).stop();
    expect(c.read(simpleShowingProvider), SimpleModeScreen.plan);
  });

  // Falsified by: simple_preflight_state.dart SimplePreflightNotifier.build, drop the close listens (#1513).
  for (final (name, toggle) in [
    ('the mode', (ProviderContainer c) => c.read(simpleModeProvider.notifier).set(false)),
    ('the feature', (ProviderContainer c) => c.read(simpleModeFeatureProvider.notifier).set(false)),
  ]) {
    test('Simple Mode closing ($name off) ends Preflight: a focus finished in Advanced starts no polar routine', () async {
      final c = await _container();
      await c.read(simpleModeProvider.future);
      await c.read(simpleModeFeatureProvider.future);
      c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.preflight);
      c.read(simplePreflightProvider.notifier).next();
      expect(c.read(simplePreflightProvider), SimplePreflightStep.focus);
      await toggle(c);
      expect(c.read(simplePreflightProvider), isNull);
      c.read(_focus.notifier).set(SetupStepState.done);
      expect(c.read(simplePreflightProvider), isNull);
      expect((c.read(polarAlignApiProvider) as _PolarApi).calls, isEmpty);
    });
  }

  // Falsified by: simple_preflight_state.dart SimplePreflightNotifier.build, drop the companionModeProvider close listen (#1513).
  test('#1774 Companion Mode ending (a split-screen tablet widened past 600) ends Preflight: a focus finished in the full UI starts no polar routine', () async {
    final c = await _container(prefs: false);
    await c.read(simpleModeProvider.future);
    await c.read(simpleModeFeatureProvider.future);
    c.read(_side.notifier).set(390);
    expect(c.read(simpleModeActiveProvider), isTrue);
    c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.preflight);
    c.read(simplePreflightProvider.notifier).next();
    expect(c.read(simplePreflightProvider), SimplePreflightStep.focus);
    c.read(_side.notifier).set(800);
    expect(c.read(simpleModeActiveProvider), isFalse);
    expect(c.read(simplePreflightProvider), isNull);
    c.read(_focus.notifier).set(SetupStepState.done);
    expect(c.read(simplePreflightProvider), isNull);
    expect((c.read(polarAlignApiProvider) as _PolarApi).calls, isEmpty);
  });

  // Falsified by: simple_preflight_state.dart SimpleShowingNotifier.build, drop the companionModeProvider open listen.
  test('#1774 Companion Mode starting with both prefs off opens Simple Mode on Preview', () async {
    final c = await _container(prefs: false);
    await c.read(simpleModeProvider.future);
    await c.read(simpleModeFeatureProvider.future);
    expect(c.read(simpleModeActiveProvider), isFalse);
    c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.focus);
    c.read(_side.notifier).set(390);
    expect(c.read(simpleModeActiveProvider), isTrue);
    expect(c.read(simpleShowingProvider), SimpleModeScreen.preview);
  });

  Future<ProviderContainer> onPolar() async {
    final c = await _container();
    await c.read(simpleModeProvider.future);
    await c.read(simpleModeFeatureProvider.future);
    c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.preflight);
    c.read(_focus.notifier).set(SetupStepState.done);
    c.read(simplePreflightProvider.notifier).next();
    expect(c.read(simplePreflightProvider), SimplePreflightStep.polarAlign);
    (c.read(polarAlignLiveProvider.notifier) as _Live).running();
    (c.read(polarAlignApiProvider) as _PolarApi).calls.clear();
    return c;
  }

  test('entering Polar over a finished check keeps the verdict: no start, so no Home and no wiped readout', () async {
    final c = await _container();
    await c.read(simpleModeProvider.future);
    await c.read(simpleModeFeatureProvider.future);
    (c.read(polarAlignLiveProvider.notifier) as _Live).checked();
    c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.preflight);
    c.read(_focus.notifier).set(SetupStepState.done);
    c.read(simplePreflightProvider.notifier).next();
    expect(c.read(simplePreflightProvider), SimplePreflightStep.polarAlign);
    expect((c.read(polarAlignApiProvider) as _PolarApi).calls, isEmpty);
  });

  test('closing Simple Mode on Polar with its routine running stops it once', () async {
    final c = await onPolar();
    await c.read(simpleModeProvider.notifier).set(false);
    expect(c.read(simplePreflightProvider), isNull);
    expect((c.read(polarAlignApiProvider) as _PolarApi).calls, ['stop']);
  });

  test('a routine Advanced starts after Simple Mode closed is not stopped on reopening (#1514)', () async {
    final c = await onPolar();
    await c.read(simpleModeProvider.notifier).set(false);
    (c.read(polarAlignApiProvider) as _PolarApi).calls.clear();
    await c.read(simpleModeProvider.notifier).set(true);
    expect((c.read(polarAlignApiProvider) as _PolarApi).calls, isEmpty);
  });

  test('a queue Preflight started keeps running after Simple Mode closes', () async {
    final c = await _container();
    await c.read(simpleModeProvider.future);
    await c.read(simpleModeFeatureProvider.future);
    c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.preflight);
    (c.read(simpleRunProvider.notifier) as _Plan).queued();
    c.read(_focus.notifier).set(SetupStepState.done);
    c.read(_aligned.notifier).set(SetupStepState.done);
    c.read(simplePreflightProvider.notifier).next();
    c.read(simplePreflightProvider.notifier).next();
    await c.read(simpleModeProvider.notifier).set(false);
    expect(c.read(simpleRunProvider).started, isTrue);
    expect(c.read(simplePreflightProvider), isNull);
  });

  // Falsified by: simple_preflight_state.dart polarAlignLastResultProvider, drop the polarAlignLiveProvider watch.
  test('a polar routine that ends reads the last result again', () async {
    final api = _StatusApi()
      ..last = PolarAlignLastResult(
        errorArcmin: 4,
        completedUtc: DateTime.now().toUtc(),
      );
    final c = ProviderContainer(
      overrides: [
        polarAlignLiveProvider.overrideWith(_Live.new),
        polarAlignApiProvider.overrideWithValue(api),
      ],
    );
    addTearDown(c.dispose);
    c.listen(nightPolarAlignStateProvider, (_, _) {});
    await c.read(polarAlignLastResultProvider.future);
    expect(c.read(nightPolarAlignStateProvider), SetupStepState.done);

    // A newer routine reads yellow and is stopped: the rig keeps that result.
    final live = c.read(polarAlignLiveProvider.notifier) as _Live;
    live.running();
    api.last = PolarAlignLastResult(
      errorArcmin: 18,
      completedUtc: DateTime.now().toUtc(),
    );
    live.stopped(zone: 'yellow');
    expect(c.read(nightPolarAlignStateProvider), SetupStepState.pending);
    expect(
      (await c.read(polarAlignLastResultProvider.future)).last?.errorArcmin,
      18,
    );

    // One more, stopped before its first solve: still not aligned.
    live.running();
    live.stopped();
    await c.read(polarAlignLastResultProvider.future);
    expect(c.read(nightPolarAlignStateProvider), SetupStepState.pending);
  });

  group('doneTonight', () {
    ProviderContainer container(_StatusApi api) {
      final c = ProviderContainer(
        overrides: [
          autofocusLiveProvider.overrideWith(_FocusLive.new),
          polarAlignLiveProvider.overrideWith(_Live.new),
          polarAlignApiProvider.overrideWithValue(api),
        ],
      );
      addTearDown(c.dispose);
      c.listen(doneTonightProvider, (_, _) {});
      return c;
    }

    test(
      'Focus: a run that finished tonight counts; a failed re-run clears it',
      () {
        final c = container(_StatusApi());
        final focus = c.read(autofocusLiveProvider.notifier) as _FocusLive;
        expect(
          c.read(doneTonightProvider),
          isNot(contains(SimpleModeScreen.focus)),
        );
        focus.put(_focusRun(AutofocusRunStates.complete));
        expect(c.read(doneTonightProvider), contains(SimpleModeScreen.focus));
        focus.put(_focusRun(AutofocusRunStates.failed));
        expect(
          c.read(doneTonightProvider),
          isNot(contains(SimpleModeScreen.focus)),
        );
      },
    );

    test('Focus: a manual in-focus mark after the failed run counts again', () {
      final c = container(_StatusApi());
      final focus = c.read(autofocusLiveProvider.notifier) as _FocusLive;
      focus.put(_focusRun(AutofocusRunStates.failed));
      focus.markFocusedManually();
      expect(c.read(doneTonightProvider), contains(SimpleModeScreen.focus));
      // The same failed run read again keeps the mark; a new run drops it.
      final same = c.read(autofocusLiveProvider).run;
      focus.put(applyRunSnapshot(c.read(autofocusLiveProvider), same));
      expect(c.read(doneTonightProvider), contains(SimpleModeScreen.focus));
      final next = AutofocusRun(
        state: AutofocusRunStates.failed,
        startedUtc: same.startedUtc!.add(const Duration(minutes: 5)),
      );
      focus.put(applyRunSnapshot(c.read(autofocusLiveProvider), next));
      expect(c.read(autofocusLiveProvider).manualMarkSinceRun, isFalse);
      expect(
        c.read(doneTonightProvider),
        isNot(contains(SimpleModeScreen.focus)),
      );
    });

    test('Polar align: a failed routine clears the tick though its zone stays green', () {
      final c = container(_StatusApi());
      final polar = c.read(polarAlignLiveProvider.notifier) as _Live;
      polar.state = const PolarAlignLive(
        phase: PolarAlignStates.checked,
        zone: 'green',
        stage: PolarAlignStages.fine,
      );
      expect(
        c.read(doneTonightProvider),
        contains(SimpleModeScreen.polarAlign),
      );
      polar.state = const PolarAlignLive(
        phase: PolarAlignStates.failed,
        zone: 'green',
        stage: PolarAlignStages.fine,
      );
      expect(
        c.read(doneTonightProvider),
        isNot(contains(SimpleModeScreen.polarAlign)),
      );
    });

    test('Polar align after a relaunch: green tonight but the daemon says failed, live idle: no tick', () async {
      final api = _StatusApi()
        ..last = PolarAlignLastResult(
          errorArcmin: 4,
          completedUtc: DateTime.now().toUtc(),
        );
      final c = container(api);
      await c.read(polarAlignLastResultProvider.future);
      expect(
        c.read(doneTonightProvider),
        contains(SimpleModeScreen.polarAlign),
      );

      api.state = PolarAlignStates.failed;
      c.invalidate(polarAlignLastResultProvider);
      await c.read(polarAlignLastResultProvider.future);
      expect(
        c.read(doneTonightProvider),
        isNot(contains(SimpleModeScreen.polarAlign)),
      );

      // A live phase other than idle wins over the fetched status.
      (c.read(polarAlignLiveProvider.notifier) as _Live).state =
          const PolarAlignLive(
            phase: PolarAlignStates.checked,
            zone: 'green',
            stage: PolarAlignStages.fine,
          );
      expect(
        c.read(doneTonightProvider),
        contains(SimpleModeScreen.polarAlign),
      );
    });
  });

  // Falsified by: simple_preflight_state.dart _go, call startGuiding instead of startGuidingTracked.
  test('2026-11-14: the Guide step turns tracking on before it starts the guider', () async {
    final c = await _container();
    await c.read(simpleModeProvider.future);
    await c.read(guiderStatusProvider.future);
    c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.preflight);
    c.read(_focus.notifier).set(SetupStepState.done);
    c.read(_aligned.notifier).set(SetupStepState.done);
    c.read(simplePreflightProvider.notifier).start();
    c.read(simplePreflightProvider.notifier).next();
    await Future<void>.delayed(Duration.zero);
    expect(c.read(simplePreflightProvider), SimplePreflightStep.guide);
    expect(_asks, ['tracking:true', 'startGuiding']);
  });

  // Falsified by: mount_state.dart ensureTracking, return true after the last refused try.
  test('a mount that refuses tracking every time: no guiding, an error pill', () async {
    final c = await _container(refuseTracking: true);
    await c.read(simpleModeProvider.future);
    await c.read(guiderStatusProvider.future);
    c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.preflight);
    c.read(_focus.notifier).set(SetupStepState.done);
    c.read(_aligned.notifier).set(SetupStepState.done);
    c.read(simplePreflightProvider.notifier).start();
    c.read(simplePreflightProvider.notifier).next();
    await Future<void>.delayed(const Duration(seconds: 6));
    expect(_asks.where((a) => a == 'tracking:true').length, 5);
    expect(_asks, isNot(contains('startGuiding')));
    expect(c.read(simpleMessagesProvider), isNotEmpty);
  });
}
