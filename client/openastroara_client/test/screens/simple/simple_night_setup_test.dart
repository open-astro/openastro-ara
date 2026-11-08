import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/simple/simple_night_setup.dart';
import 'package:openastroara/screens/simple/simple_plan_panel.dart';
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/state/imaging/capture_progress_state.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/state/setup/setup_readiness.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/theme/ara_colors.dart';

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

class _PolarApi implements PolarAlignClient {
  final calls = <String>[];
  /// What start throws instead (no guide camera, say).
  Object? startError;
  @override
  Future<void> start({String? stage}) async {
    if (startError case final e?) throw e;
    calls.add('start');
  }
  @override
  Future<void> next() async => calls.add('next');
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
  final starts = <DateTime?>[];
  @override
  SimpleRun build() => const SimpleRun();
  void queued() => state = const SimpleRun(sequenceId: 'seq-1');
  void started() => state = const SimpleRun(sequenceId: 'seq-1', started: true);
  @override
  Future<bool> startQueue({DateTime? startAt}) async {
    starts.add(startAt);
    started();
    return true;
  }
}

/// A connected guider, stopped until [startGuiding]; records the asks.
class _Guider extends GuiderStatusNotifier {
  final calls = <String>[];
  static GuiderStatus _at(String runtime) => GuiderStatus.fromJson(
      {'device_id': 'g', 'name': 'PHD2', 'state': 'connected', 'runtime': {'state': runtime}});
  @override
  Future<GuiderStatus?> build() async => _at('stopped');
  @override
  Future<void> startGuiding() async {
    calls.add('start');
    state = AsyncData(_at('calibrating'));
  }
  @override
  Future<void> stopGuiding() async => calls.add('stop');
  void guiding() => state = AsyncData(_at('guiding'));
}

/// The polar routine as the rig reports it; [run] makes it active.
class _Live extends PolarAlignLiveNotifier {
  @override
  PolarAlignLive build() => const PolarAlignLive();
  void run() => state = const PolarAlignLive(phase: PolarAlignStates.adjusting);
}

Future<(ProviderContainer, _PolarApi)> _container({bool permanent = false, _Guider? guider}) async {
  final api = _PolarApi();
  final c = ProviderContainer(overrides: [
    if (guider != null) guiderStatusProvider.overrideWith(() => guider),
    polarAlignLiveProvider.overrideWith(_Live.new),
    setupFocusStateProvider.overrideWith((ref) => ref.watch(_focus)),
    setupPolarAlignStateProvider.overrideWith((ref) => ref.watch(_aligned)),
    polarAlignApiProvider.overrideWithValue(api),
    simplePermanentPierPrefsProvider.overrideWithValue(_Prefs(permanent)),
    simpleRunProvider.overrideWith(_Plan.new),
  ]);
  addTearDown(c.dispose);
  await c.read(simplePermanentPierProvider.future);
  if (guider != null) await c.read(guiderStatusProvider.future);
  c.listen(nightSetupProvider, (_, _) {});
  return (c, api);
}

void main() {
  test('Plan first, then the first setup step not done; nothing left means start imaging', () {
    expect(nightSetupNext(focused: true, aligned: true, permanentPier: false, guided: true), NightSetupStep.plan,
        reason: 'the plan is never done beforehand: Night Setup starts on it');
    expect(nightSetupNext(after: NightSetupStep.plan, focused: false, aligned: false, permanentPier: false, guided: true),
        NightSetupStep.focus);
    expect(nightSetupNext(after: NightSetupStep.plan, focused: true, aligned: false, permanentPier: false, guided: true),
        NightSetupStep.polarAlign);
    expect(nightSetupNext(after: NightSetupStep.plan, focused: true, aligned: false, permanentPier: true, guided: true), isNull);
    expect(nightSetupNext(after: NightSetupStep.focus, focused: false, aligned: false, permanentPier: false, guided: true),
        NightSetupStep.polarAlign, reason: 'Skip from Focus');
    expect(nightSetupNext(after: NightSetupStep.polarAlign, focused: false, aligned: false, permanentPier: false, guided: true), isNull);
  });

  test('Guide comes after polar align; skipped when guiding already or with no guider', () {
    // User, 2026-11-04: the guide routine is part of Night Setup, after Polar align.
    expect(nightSetupNext(after: NightSetupStep.polarAlign, focused: true, aligned: true, permanentPier: false, guided: false),
        NightSetupStep.guide);
    expect(nightSetupNext(after: NightSetupStep.plan, focused: true, aligned: false, permanentPier: true, guided: false),
        NightSetupStep.guide, reason: 'a permanent pier skips polar align, not guiding');
    expect(nightSetupNext(after: NightSetupStep.polarAlign, focused: true, aligned: true, permanentPier: false, guided: true),
        isNull, reason: 'already guiding (or no guider): imaging is next');
    expect(nightSetupNext(after: NightSetupStep.guide, focused: false, aligned: false, permanentPier: false, guided: false),
        isNull, reason: 'the last step');
  });

  test('the Guide step does not start guiding while the main camera is busy: calibration turns the mount '
      '(review, #1490: Night Setup can run over an imaging queue)', () async {
    final guider = _Guider();
    final (c, _) = await _container(guider: guider);
    final plan = c.read(simpleRunProvider.notifier) as _Plan;
    final pilot = c.read(nightSetupProvider.notifier);
    pilot.start();
    plan.queued();
    c.read(_focus.notifier).set(SetupStepState.done);
    c.read(_aligned.notifier).set(SetupStepState.done);
    c.read(captureProgressProvider.notifier).beginExposing(const Duration(seconds: 30));
    pilot.next(); // the plan's Next: focus and polar align done, so Guide
    expect(c.read(nightSetupProvider), NightSetupStep.guide, reason: 'the step is held, not skipped');
    await Future<void>.value();
    expect(guider.calls, isEmpty, reason: 'no calibration under a frame; the shutter says why');
    c.read(captureProgressProvider.notifier).reset();
  });

  test('with a guider connected, polar align hands over to Guide, which starts guiding on entry', () async {
    final guider = _Guider();
    final (c, _) = await _container(guider: guider);
    final plan = c.read(simpleRunProvider.notifier) as _Plan;
    final pilot = c.read(nightSetupProvider.notifier);
    pilot.start();
    plan.queued();
    c.read(_focus.notifier).set(SetupStepState.done);
    pilot.next(); // the plan's Next: focus is done, so polar align
    expect(c.read(nightSetupProvider), NightSetupStep.polarAlign);
    pilot.next(); // polar align's Done (or Skip)
    expect(c.read(nightSetupProvider), NightSetupStep.guide);
    await Future<void>.value();
    expect(guider.calls, ['start'], reason: 'entering Guide starts the routine (calibrating when needed)');
    guider.guiding();
    pilot.next(); // Guide's Done · Start imaging
    await Future<void>.value();
    expect(plan.starts, hasLength(1), reason: 'imaging starts');
    expect(guider.calls, ['start'], reason: 'leaving Guide keeps it guiding for imaging');
  });

  test('Night Setup opens on the plan; its Next goes to Focus', () async {
    final (c, _) = await _container();
    c.read(nightSetupProvider.notifier).start();
    expect(c.read(nightSetupProvider), NightSetupStep.plan);
    c.read(nightSetupProvider.notifier).next();
    expect(c.read(nightSetupProvider), NightSetupStep.focus);
  });

  test('a finished autofocus moves on to polar align and starts its routine', () async {
    final (c, api) = await _container();
    c.read(nightSetupProvider.notifier).start();
    c.read(nightSetupProvider.notifier).next();
    expect(api.calls, isEmpty);
    c.read(_focus.notifier).set(SetupStepState.done);
    expect(c.read(nightSetupProvider), NightSetupStep.polarAlign);
    expect(api.calls, ['start'], reason: 'entering polar align starts it');
  });

  test("on a permanent pier a finished autofocus waits for its Done: nothing starts on its own", () async {
    final (c, api) = await _container(permanent: true);
    c.read(nightSetupProvider.notifier).start();
    c.read(nightSetupProvider.notifier).next();
    c.read(_focus.notifier).set(SetupStepState.done);
    expect(c.read(nightSetupProvider), NightSetupStep.focus);
    expect((c.read(simpleRunProvider.notifier) as _Plan).starts, isEmpty);
    expect(api.calls, isEmpty);
  });

  test("the last setup step's Done starts imaging (the queue, at dark when it's before dark) and ends Night Setup",
      () async {
    final (c, _) = await _container();
    final plan = c.read(simpleRunProvider.notifier) as _Plan;
    c.read(nightSetupProvider.notifier).start();
    plan.queued();
    c.read(nightSetupProvider.notifier).next();
    c.read(_focus.notifier).set(SetupStepState.done);
    expect(c.read(nightSetupProvider), NightSetupStep.polarAlign);
    c.read(nightSetupProvider.notifier).back();
    expect(c.read(nightSetupProvider), NightSetupStep.focus);
    c.read(nightSetupProvider.notifier).next();
    c.read(nightSetupProvider.notifier).next();
    await Future<void>.value();
    expect(plan.starts, hasLength(1));
    expect(c.read(nightSetupProvider), isNull, reason: 'imaging: Night Setup is done');
  });

  test('a Guided Run started while a queue images stays open through its updates (ALP-4982)', () async {
    // #1486 review: any run update with started set ended Guided Run, not only imaging starting.
    final (c, _) = await _container();
    final plan = c.read(simpleRunProvider.notifier) as _Plan;
    plan.started();
    c.read(nightSetupProvider.notifier).start();
    final step = c.read(nightSetupProvider);
    expect(step, isNotNull, reason: 'precondition: running');
    plan.state = const SimpleRun(sequenceId: 'seq-1', started: true, busy: true); // an update
    await Future<void>.value();
    expect(c.read(nightSetupProvider), step);
    // Its last Done ends it without starting the running queue again.
    c.read(_focus.notifier).set(SetupStepState.done);
    c.read(_aligned.notifier).set(SetupStepState.done);
    while (c.read(nightSetupProvider) != null) {
      c.read(nightSetupProvider.notifier).next();
    }
    expect(plan.starts, isEmpty);
  });

  test('with nothing queued the last Done goes back to the plan instead of doing nothing', () async {
    // PR #1486 review: Skip on the plan with nothing queued, then the last Done reached startQueue, which
    // returns silently without a queue.
    final (c, _) = await _container();
    final plan = c.read(simpleRunProvider.notifier) as _Plan;
    c.read(nightSetupProvider.notifier).start();
    c.read(nightSetupProvider.notifier).next(); // Skip the plan
    c.read(_focus.notifier).set(SetupStepState.done);
    expect(c.read(nightSetupProvider), NightSetupStep.polarAlign);
    c.read(nightSetupProvider.notifier).next(); // Done · Start imaging
    await Future<void>.value();
    expect(plan.starts, isEmpty);
    expect(c.read(nightSetupProvider), NightSetupStep.plan, reason: 'pick something to image first');
  });

  for (final (how, leave) in <(String, void Function(NightSetupNotifier))>[
    ('Skip', (p) => p.next()),
    ('Back', (p) => p.back()),
    ('leaving Night Setup', (p) => p.stop()),
  ]) {
    test('$how from a running polar routine stops it: the mount must not keep moving', () async {
      final (c, api) = await _container();
      final plan = c.read(simpleRunProvider.notifier) as _Plan;
      final pilot = c.read(nightSetupProvider.notifier);
      pilot.start();
      plan.queued();
      pilot.next();
      c.read(_focus.notifier).set(SetupStepState.done);
      expect(c.read(nightSetupProvider), NightSetupStep.polarAlign);
      expect(api.calls, ['start']);
      (c.read(polarAlignLiveProvider.notifier) as _Live).run();
      leave(pilot);
      await Future<void>.value();
      expect(api.calls, ['start', 'stop']);
    });
  }

  test('a polar routine that fails to start says why in a pill, not a step that never started', () async {
    final (c, api) = await _container();
    api.startError = StateError('No guide camera is connected');
    final pilot = c.read(nightSetupProvider.notifier);
    pilot.start();
    pilot.next();
    c.read(_focus.notifier).set(SetupStepState.done);
    expect(c.read(nightSetupProvider), NightSetupStep.polarAlign);
    await Future<void>.delayed(Duration.zero);
    final m = c.read(simpleMessagesProvider).single;
    expect(m.kind, SimpleMessageKind.error);
    expect(m.title, contains('No guide camera is connected'), reason: 'the same words the polar shutter shows');
  });

  test("the polar step's Done completes the routine: moving on stops nothing", () async {
    final (c, api) = await _container();
    final plan = c.read(simpleRunProvider.notifier) as _Plan;
    final pilot = c.read(nightSetupProvider.notifier);
    pilot.start();
    plan.queued();
    pilot.next();
    c.read(_focus.notifier).set(SetupStepState.done);
    expect(c.read(nightSetupProvider), NightSetupStep.polarAlign);
    (c.read(polarAlignLiveProvider.notifier) as _Live).run(); // complete sent, the rig not caught up yet
    pilot.polarDone();
    await Future<void>.value();
    expect(api.calls, ['start']);
    expect(plan.starts, hasLength(1));
  });

  test('a polar routine that already ended is not stopped again', () async {
    final (c, api) = await _container();
    final pilot = c.read(nightSetupProvider.notifier);
    pilot.start();
    pilot.next();
    c.read(_focus.notifier).set(SetupStepState.done);
    expect(c.read(nightSetupProvider), NightSetupStep.polarAlign);
    pilot.back();
    expect(api.calls, ['start'], reason: 'nothing running: no stop');
  });

  testWidgets('the strip shows Plan, Focus, Polar align, the current one lit; Skip and Back move', (tester) async {
    final (c, _) = await _container();
    c.read(nightSetupProvider.notifier).start();
    await tester.pumpWidget(UncontrolledProviderScope(
      container: c,
      child: const MaterialApp(home: Scaffold(body: Center(child: NightSetupStrip()))),
    ));
    final x = [for (final s in ['Plan', 'Focus', 'Polar align']) tester.getCenter(find.text(s)).dx];
    expect(x, orderedEquals([...x]..sort()), reason: 'Plan first');
    expect(tester.widget<TextButton>(find.byKey(const Key('night-setup-back'))).onPressed, isNull);
    (c.read(simpleRunProvider.notifier) as _Plan).queued();
    await tester.tap(find.byKey(const Key('night-setup-skip')));
    await tester.pump();
    expect(c.read(nightSetupProvider), NightSetupStep.focus);
    expect(find.bySemanticsLabel('Plan, done'), findsOneWidget, reason: 'a queue is in');
    expect(find.bySemanticsLabel('Focus, current step'), findsOneWidget);
    expect(find.bySemanticsLabel('Polar align, next'), findsOneWidget);
    await tester.tap(find.byKey(const Key('night-setup-skip')));
    await tester.pump();
    expect(find.bySemanticsLabel('Focus, skipped'), findsOneWidget);
    expect(tester.widget<Icon>(find.byIcon(Icons.skip_next)).color, AraColors.accentBusy);
    await tester.tap(find.byKey(const Key('night-setup-back')));
    await tester.pump();
    expect(c.read(nightSetupProvider), NightSetupStep.focus);
  });
}
