import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/simple/simple_autopilot.dart';
import 'package:openastroara/screens/simple/simple_plan_panel.dart';
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/state/setup/setup_readiness.dart';
import 'package:openastroara/state/simple_mode_state.dart';

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
  @override
  Future<void> start() async => calls.add('start');
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
  void started() => state = const SimpleRun(sequenceId: 'seq-1', started: true);
}

Future<(ProviderContainer, _PolarApi)> _container({bool permanent = false}) async {
  final api = _PolarApi();
  final c = ProviderContainer(overrides: [
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
  test('the step to start on, or next: the first not done; P.A. skipped on a permanent pier', () {
    expect(autopilotNext(focused: false, aligned: false, permanentPier: false), AutopilotStep.focus);
    expect(autopilotNext(focused: true, aligned: false, permanentPier: false), AutopilotStep.polarAlign);
    expect(autopilotNext(focused: true, aligned: false, permanentPier: true), AutopilotStep.plan);
    expect(autopilotNext(focused: true, aligned: true, permanentPier: false), AutopilotStep.plan);
    expect(autopilotNext(after: AutopilotStep.focus, focused: false, aligned: false, permanentPier: false),
        AutopilotStep.polarAlign, reason: 'Skip from Focus');
    expect(autopilotNext(after: AutopilotStep.polarAlign, focused: false, aligned: false, permanentPier: false),
        AutopilotStep.plan);
  });

  test('a finished autofocus moves on to polar align and starts its routine', () async {
    final (c, api) = await _container();
    c.read(autopilotProvider.notifier).start();
    expect(c.read(autopilotProvider), AutopilotStep.focus);
    expect(api.calls, isEmpty);
    c.read(_focus.notifier).set(SetupStepState.done);
    expect(c.read(autopilotProvider), AutopilotStep.polarAlign);
    expect(api.calls, ['start'], reason: 'entering polar align starts it');
  });

  test('on a permanent pier a finished autofocus goes straight to the plan', () async {
    final (c, api) = await _container(permanent: true);
    c.read(autopilotProvider.notifier).start();
    c.read(_focus.notifier).set(SetupStepState.done);
    expect(c.read(autopilotProvider), AutopilotStep.plan);
    expect(api.calls, isEmpty);
  });

  test('Done (next) moves on to the plan; starting the plan ends Autopilot; Back goes back', () async {
    final (c, _) = await _container();
    c.read(_focus.notifier).set(SetupStepState.done);
    c.read(autopilotProvider.notifier).start();
    expect(c.read(autopilotProvider), AutopilotStep.polarAlign);
    c.read(autopilotProvider.notifier).next();
    expect(c.read(autopilotProvider), AutopilotStep.plan);
    c.read(autopilotProvider.notifier).back();
    expect(c.read(autopilotProvider), AutopilotStep.polarAlign);
    c.read(autopilotProvider.notifier).next();
    (c.read(simpleRunProvider.notifier) as _Plan).started();
    expect(c.read(autopilotProvider), isNull, reason: 'the plan runs: Autopilot is done');
  });

  testWidgets('the strip shows the steps, the current one lit; Skip and Back move', (tester) async {
    final (c, _) = await _container();
    c.read(autopilotProvider.notifier).start();
    await tester.pumpWidget(UncontrolledProviderScope(
      container: c,
      child: const MaterialApp(home: Scaffold(body: Center(child: AutopilotStrip()))),
    ));
    for (final s in ['Focus', 'Polar align', 'Plan']) {
      expect(find.text(s), findsOneWidget);
    }
    expect(tester.widget<TextButton>(find.byKey(const Key('autopilot-back'))).onPressed, isNull);
    await tester.tap(find.byKey(const Key('autopilot-skip')));
    await tester.pump();
    expect(c.read(autopilotProvider), AutopilotStep.polarAlign);
    expect(find.byIcon(Icons.check_circle), findsOneWidget, reason: 'Focus ticked');
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
