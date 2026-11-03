import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/simple/simple_autopilot.dart';
import 'package:openastroara/screens/simple/simple_plan_panel.dart';
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
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
  Future<void> start() async {
    if (startError case final e?) throw e;
    calls.add('start');
  }
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

/// The polar routine as the rig reports it; [run] makes it active.
class _Live extends PolarAlignLiveNotifier {
  @override
  PolarAlignLive build() => const PolarAlignLive();
  void run() => state = const PolarAlignLive(phase: PolarAlignStates.adjusting);
}

Future<(ProviderContainer, _PolarApi)> _container({bool permanent = false}) async {
  final api = _PolarApi();
  final c = ProviderContainer(overrides: [
    polarAlignLiveProvider.overrideWith(_Live.new),
    setupFocusStateProvider.overrideWith((ref) => ref.watch(_focus)),
    setupPolarAlignStateProvider.overrideWith((ref) => ref.watch(_aligned)),
    polarAlignApiProvider.overrideWithValue(api),
    simplePermanentPierPrefsProvider.overrideWithValue(_Prefs(permanent)),
    simpleRunProvider.overrideWith(_Plan.new),
  ]);
  addTearDown(c.dispose);
  await c.read(simplePermanentPierProvider.future);
  c.listen(autopilotProvider, (_, _) {});
  return (c, api);
}

void main() {
  test('Plan first, then the first setup step not done; nothing left means start imaging', () {
    expect(autopilotNext(focused: true, aligned: true, permanentPier: false), AutopilotStep.plan,
        reason: 'the plan is never done beforehand: Guided Run starts on it');
    expect(autopilotNext(after: AutopilotStep.plan, focused: false, aligned: false, permanentPier: false),
        AutopilotStep.focus);
    expect(autopilotNext(after: AutopilotStep.plan, focused: true, aligned: false, permanentPier: false),
        AutopilotStep.polarAlign);
    expect(autopilotNext(after: AutopilotStep.plan, focused: true, aligned: false, permanentPier: true), isNull);
    expect(autopilotNext(after: AutopilotStep.focus, focused: false, aligned: false, permanentPier: false),
        AutopilotStep.polarAlign, reason: 'Skip from Focus');
    expect(autopilotNext(after: AutopilotStep.polarAlign, focused: false, aligned: false, permanentPier: false), isNull);
  });

  test('Guided Run opens on the plan; its Next goes to Focus', () async {
    final (c, _) = await _container();
    c.read(autopilotProvider.notifier).start();
    expect(c.read(autopilotProvider), AutopilotStep.plan);
    c.read(autopilotProvider.notifier).next();
    expect(c.read(autopilotProvider), AutopilotStep.focus);
  });

  test('a finished autofocus moves on to polar align and starts its routine', () async {
    final (c, api) = await _container();
    c.read(autopilotProvider.notifier).start();
    c.read(autopilotProvider.notifier).next();
    expect(api.calls, isEmpty);
    c.read(_focus.notifier).set(SetupStepState.done);
    expect(c.read(autopilotProvider), AutopilotStep.polarAlign);
    expect(api.calls, ['start'], reason: 'entering polar align starts it');
  });

  test("on a permanent pier a finished autofocus waits for its Done: nothing starts on its own", () async {
    final (c, api) = await _container(permanent: true);
    c.read(autopilotProvider.notifier).start();
    c.read(autopilotProvider.notifier).next();
    c.read(_focus.notifier).set(SetupStepState.done);
    expect(c.read(autopilotProvider), AutopilotStep.focus);
    expect((c.read(simpleRunProvider.notifier) as _Plan).starts, isEmpty);
    expect(api.calls, isEmpty);
  });

  test("the last setup step's Done starts imaging (the queue, at dark when it's before dark) and ends Guided Run",
      () async {
    final (c, _) = await _container();
    final plan = c.read(simpleRunProvider.notifier) as _Plan;
    c.read(autopilotProvider.notifier).start();
    plan.queued();
    c.read(autopilotProvider.notifier).next();
    c.read(_focus.notifier).set(SetupStepState.done);
    expect(c.read(autopilotProvider), AutopilotStep.polarAlign);
    c.read(autopilotProvider.notifier).back();
    expect(c.read(autopilotProvider), AutopilotStep.focus);
    c.read(autopilotProvider.notifier).next();
    c.read(autopilotProvider.notifier).next();
    await Future<void>.value();
    expect(plan.starts, hasLength(1));
    expect(c.read(autopilotProvider), isNull, reason: 'imaging: Guided Run is done');
  });

  test('with nothing queued the last Done goes back to the plan instead of doing nothing', () async {
    // PR #1486 review: Skip on the plan with nothing queued, then the last Done reached startQueue, which
    // returns silently without a queue.
    final (c, _) = await _container();
    final plan = c.read(simpleRunProvider.notifier) as _Plan;
    c.read(autopilotProvider.notifier).start();
    c.read(autopilotProvider.notifier).next(); // Skip the plan
    c.read(_focus.notifier).set(SetupStepState.done);
    expect(c.read(autopilotProvider), AutopilotStep.polarAlign);
    c.read(autopilotProvider.notifier).next(); // Done · Start imaging
    await Future<void>.value();
    expect(plan.starts, isEmpty);
    expect(c.read(autopilotProvider), AutopilotStep.plan, reason: 'pick something to image first');
  });

  for (final (how, leave) in <(String, void Function(AutopilotNotifier))>[
    ('Skip', (p) => p.next()),
    ('Back', (p) => p.back()),
    ('leaving Guided Run', (p) => p.stop()),
  ]) {
    test('$how from a running polar routine stops it: the mount must not keep moving', () async {
      final (c, api) = await _container();
      final plan = c.read(simpleRunProvider.notifier) as _Plan;
      final pilot = c.read(autopilotProvider.notifier);
      pilot.start();
      plan.queued();
      pilot.next();
      c.read(_focus.notifier).set(SetupStepState.done);
      expect(c.read(autopilotProvider), AutopilotStep.polarAlign);
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
    final pilot = c.read(autopilotProvider.notifier);
    pilot.start();
    pilot.next();
    c.read(_focus.notifier).set(SetupStepState.done);
    expect(c.read(autopilotProvider), AutopilotStep.polarAlign);
    await Future<void>.delayed(Duration.zero);
    final m = c.read(simpleMessagesProvider).single;
    expect(m.kind, SimpleMessageKind.error);
    expect(m.title, contains('No guide camera is connected'), reason: 'the same words the polar shutter shows');
  });

  test("the polar step's Done completes the routine: moving on stops nothing", () async {
    final (c, api) = await _container();
    final plan = c.read(simpleRunProvider.notifier) as _Plan;
    final pilot = c.read(autopilotProvider.notifier);
    pilot.start();
    plan.queued();
    pilot.next();
    c.read(_focus.notifier).set(SetupStepState.done);
    expect(c.read(autopilotProvider), AutopilotStep.polarAlign);
    (c.read(polarAlignLiveProvider.notifier) as _Live).run(); // complete sent, the rig not caught up yet
    pilot.polarDone();
    await Future<void>.value();
    expect(api.calls, ['start']);
    expect(plan.starts, hasLength(1));
  });

  test('a polar routine that already ended is not stopped again', () async {
    final (c, api) = await _container();
    final pilot = c.read(autopilotProvider.notifier);
    pilot.start();
    pilot.next();
    c.read(_focus.notifier).set(SetupStepState.done);
    expect(c.read(autopilotProvider), AutopilotStep.polarAlign);
    pilot.back();
    expect(api.calls, ['start'], reason: 'nothing running: no stop');
  });

  testWidgets('the strip shows Plan, Focus, Polar align, the current one lit; Skip and Back move', (tester) async {
    final (c, _) = await _container();
    c.read(autopilotProvider.notifier).start();
    await tester.pumpWidget(UncontrolledProviderScope(
      container: c,
      child: const MaterialApp(home: Scaffold(body: Center(child: AutopilotStrip()))),
    ));
    final x = [for (final s in ['Plan', 'Focus', 'Polar align']) tester.getCenter(find.text(s)).dx];
    expect(x, orderedEquals([...x]..sort()), reason: 'Plan first');
    expect(tester.widget<TextButton>(find.byKey(const Key('autopilot-back'))).onPressed, isNull);
    (c.read(simpleRunProvider.notifier) as _Plan).queued();
    await tester.tap(find.byKey(const Key('autopilot-skip')));
    await tester.pump();
    expect(c.read(autopilotProvider), AutopilotStep.focus);
    expect(find.bySemanticsLabel('Plan, done'), findsOneWidget, reason: 'a queue is in');
    expect(find.bySemanticsLabel('Focus, current step'), findsOneWidget);
    expect(find.bySemanticsLabel('Polar align, next'), findsOneWidget);
    await tester.tap(find.byKey(const Key('autopilot-skip')));
    await tester.pump();
    expect(find.bySemanticsLabel('Focus, skipped'), findsOneWidget);
    expect(tester.widget<Icon>(find.byIcon(Icons.skip_next)).color, AraColors.accentBusy);
    await tester.tap(find.byKey(const Key('autopilot-back')));
    await tester.pump();
    expect(c.read(autopilotProvider), AutopilotStep.focus);
  });

  testWidgets('offered only while nothing is done tonight', (tester) async {
    final (c, _) = await _container();
    bool? offered;
    await tester.pumpWidget(UncontrolledProviderScope(
      container: c,
      child: MaterialApp(home: Consumer(builder: (_, ref, _) {
        offered = autopilotOffered(ref);
        return const SizedBox();
      })),
    ));
    expect(offered, isTrue);
    c.read(_focus.notifier).set(SetupStepState.done);
    await tester.pump();
    expect(offered, isFalse, reason: 'focused already');
    c.read(_focus.notifier).set(SetupStepState.pending);
    c.read(autopilotProvider.notifier).start();
    await tester.pump();
    expect(offered, isFalse, reason: 'Autopilot is running');
  });
}
