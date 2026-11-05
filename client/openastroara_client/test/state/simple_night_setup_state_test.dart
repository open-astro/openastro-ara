import 'package:openastroara/models/polar_align.dart' show PolarAlignStates;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/simple/simple_plan_panel.dart';
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/state/setup/setup_readiness.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/simple_night_setup_state.dart';

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
}

Future<ProviderContainer> _container() async {
  final c = ProviderContainer(overrides: [
    polarAlignLiveProvider.overrideWith(_Live.new),
    setupFocusStateProvider.overrideWith((ref) => ref.watch(_focus)),
    setupPolarAlignStateProvider.overrideWith((ref) => ref.watch(_aligned)),
    polarAlignApiProvider.overrideWithValue(_PolarApi()),
    simplePermanentPierPrefsProvider.overrideWithValue(_Prefs(false)),
    simpleModePrefsProvider.overrideWithValue(_Prefs(true)),
    simpleModeFeaturePrefsProvider.overrideWithValue(_Prefs(true)),
    simpleRunProvider.overrideWith(_Plan.new),
  ]);
  addTearDown(c.dispose);
  await c.read(simplePermanentPierProvider.future);
  c.listen(nightSetupProvider, (_, _) {});
  c.listen(simpleShowingProvider, (_, _) {});
  return c;
}

/// What is on screen is one value with two writers: the person's pick and
/// Night Setup's own steps. The shell only reads it (#1495).
void main() {
  test('Simple Mode opens on Preview', () async {
    final c = await _container();
    expect(c.read(simpleShowingProvider), SimpleModeScreen.preview);
  });

  // Falsified by: simple_night_setup_state.dart SimpleShowingNotifier.build, drop the simpleModeProvider listen.
  for (final (name, toggle) in [
    ('the mode', (ProviderContainer c, bool on) => c.read(simpleModeProvider.notifier).set(on)),
    ('the feature', (ProviderContainer c, bool on) => c.read(simpleModeFeatureProvider.notifier).set(on)),
  ]) {
    test('Simple Mode opening again ($name toggled) shows Preview and ends a Night Setup left running', () async {
      final c = await _container();
      await c.read(simpleModeProvider.future);
      await c.read(simpleModeFeatureProvider.future);
      expect(c.read(simpleModeActiveProvider), isTrue);
      c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.nightSetup);
      await toggle(c, false);
      await toggle(c, true);
      expect(c.read(simpleShowingProvider), SimpleModeScreen.preview);
      expect(c.read(nightSetupProvider), isNull);
    });
  }

  test('picking Night Setup starts it on Plan, and Plan is what shows; the pick itself never shows', () async {
    final c = await _container();
    c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.nightSetup);
    expect(c.read(nightSetupProvider), NightSetupStep.plan);
    expect(c.read(simpleShowingProvider), SimpleModeScreen.plan);
  });

  test('a step moves what shows with it', () async {
    final c = await _container();
    c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.nightSetup);
    c.read(nightSetupProvider.notifier).next();
    expect(c.read(nightSetupProvider), NightSetupStep.focus);
    expect(c.read(simpleShowingProvider), SimpleModeScreen.focus);
  });

  test('picking another mode shows it and ends Night Setup', () async {
    final c = await _container();
    c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.nightSetup);
    c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.preview);
    expect(c.read(nightSetupProvider), isNull);
    expect(c.read(simpleShowingProvider), SimpleModeScreen.preview);
  });

  test('imaging started by hand ends Night Setup and leaves Plan on screen', () async {
    final c = await _container();
    c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.nightSetup);
    c.read(nightSetupProvider.notifier).next();
    expect(c.read(simpleShowingProvider), SimpleModeScreen.focus);
    (c.read(simpleRunProvider.notifier) as _Plan).started();
    expect(c.read(nightSetupProvider), isNull);
    expect(c.read(simpleShowingProvider), SimpleModeScreen.plan);
  });

  test('stop() from anywhere leaves Plan on screen', () async {
    final c = await _container();
    c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.nightSetup);
    c.read(nightSetupProvider.notifier).next();
    c.read(nightSetupProvider.notifier).stop();
    expect(c.read(simpleShowingProvider), SimpleModeScreen.plan);
  });

  // Falsified by: simple_night_setup_state.dart NightSetupNotifier.build, drop the close listens (#1513).
  for (final (name, toggle) in [
    ('the mode', (ProviderContainer c) => c.read(simpleModeProvider.notifier).set(false)),
    ('the feature', (ProviderContainer c) => c.read(simpleModeFeatureProvider.notifier).set(false)),
  ]) {
    test('Simple Mode closing ($name off) ends Night Setup: a focus finished in Advanced starts no polar routine', () async {
      final c = await _container();
      await c.read(simpleModeProvider.future);
      await c.read(simpleModeFeatureProvider.future);
      c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.nightSetup);
      c.read(nightSetupProvider.notifier).next();
      expect(c.read(nightSetupProvider), NightSetupStep.focus);
      await toggle(c);
      expect(c.read(nightSetupProvider), isNull);
      c.read(_focus.notifier).set(SetupStepState.done);
      expect(c.read(nightSetupProvider), isNull);
      expect((c.read(polarAlignApiProvider) as _PolarApi).calls, isEmpty);
    });
  }

  Future<ProviderContainer> onPolar() async {
    final c = await _container();
    await c.read(simpleModeProvider.future);
    await c.read(simpleModeFeatureProvider.future);
    c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.nightSetup);
    c.read(_focus.notifier).set(SetupStepState.done);
    c.read(nightSetupProvider.notifier).next();
    expect(c.read(nightSetupProvider), NightSetupStep.polarAlign);
    (c.read(polarAlignLiveProvider.notifier) as _Live).running();
    (c.read(polarAlignApiProvider) as _PolarApi).calls.clear();
    return c;
  }

  test('closing Simple Mode on Polar with its routine running stops it once', () async {
    final c = await onPolar();
    await c.read(simpleModeProvider.notifier).set(false);
    expect(c.read(nightSetupProvider), isNull);
    expect((c.read(polarAlignApiProvider) as _PolarApi).calls, ['stop']);
  });

  test('a routine Advanced starts after Simple Mode closed is not stopped on reopening (#1514)', () async {
    final c = await onPolar();
    await c.read(simpleModeProvider.notifier).set(false);
    (c.read(polarAlignApiProvider) as _PolarApi).calls.clear();
    await c.read(simpleModeProvider.notifier).set(true);
    expect((c.read(polarAlignApiProvider) as _PolarApi).calls, isEmpty);
  });

  test('a queue Night Setup started keeps running after Simple Mode closes', () async {
    final c = await _container();
    await c.read(simpleModeProvider.future);
    await c.read(simpleModeFeatureProvider.future);
    c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.nightSetup);
    (c.read(simpleRunProvider.notifier) as _Plan).queued();
    c.read(_focus.notifier).set(SetupStepState.done);
    c.read(_aligned.notifier).set(SetupStepState.done);
    c.read(nightSetupProvider.notifier).next();
    c.read(nightSetupProvider.notifier).next();
    await c.read(simpleModeProvider.notifier).set(false);
    expect(c.read(simpleRunProvider).started, isTrue);
    expect(c.read(nightSetupProvider), isNull);
  });
}
