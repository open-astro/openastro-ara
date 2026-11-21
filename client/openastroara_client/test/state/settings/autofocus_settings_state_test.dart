import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../support/section_load.dart';
import 'package:openastroara/state/settings/autofocus_settings_state.dart';

void main() {
  group('AutofocusSettingsNotifier', () {
    late ProviderContainer container;
    setUp(() async {
      container = ProviderContainer();
      await loadSection(container, autofocusSettingsProvider, 'autofocus');
    });
    tearDown(() => container.dispose());

    test('defaults match playbook §37.11', () {
      final s = container.read(autofocusSettingsProvider).value!;
      expect(s.method, AutofocusMethod.hfrVCurve);
      expect(s.steps, 7);
      expect(s.stepSize, 50);
      expect(s.stepSizeAuto, isTrue, reason: '§59.8 — auto is the default');
      expect(s.exposureSeconds, 5);
      expect(s.binning, 1);
      expect(s.afFilter, 'L');
      expect(s.runAfterFilterChange, isTrue);
      expect(s.triggerTempDeltaC, 2.0);
      expect(s.triggerHfrDriftPct, 15);
      expect(s.everyNHours, 2);
      expect(s.abortSequenceOnAfFailure, isTrue);
      expect(s.restorePositionOnFailure, isTrue);
    });

    test('setStepSizeAuto assigns directly and leaves the typed step size', () {
      final n = container.read(autofocusSettingsProvider.notifier);
      n.setStepSize(80);
      n.setStepSizeAuto(false);
      expect(container.read(autofocusSettingsProvider).value!.stepSizeAuto, isFalse);
      expect(container.read(autofocusSettingsProvider).value!.stepSize, 80);
      n.setStepSizeAuto(true);
      expect(container.read(autofocusSettingsProvider).value!.stepSizeAuto, isTrue);
    });

    test('setSteps clamps to [3, 31]', () {
      final n = container.read(autofocusSettingsProvider.notifier);
      n.setSteps(2);
      n.setSteps(32);
      expect(container.read(autofocusSettingsProvider).value!.steps, 7);
      n.setSteps(5);
      n.setSteps(15);
      expect(container.read(autofocusSettingsProvider).value!.steps, 15);
    });

    test('setStepSize + setExposureSeconds reject non-positive', () {
      final n = container.read(autofocusSettingsProvider.notifier);
      n.setStepSize(0);
      n.setStepSize(-10);
      n.setExposureSeconds(0);
      n.setExposureSeconds(-1);
      final s = container.read(autofocusSettingsProvider).value!;
      expect(s.stepSize, 50);
      expect(s.exposureSeconds, 5);
    });

    test('setBinning rejects below 1', () {
      final n = container.read(autofocusSettingsProvider.notifier);
      n.setBinning(0);
      n.setBinning(-1);
      expect(container.read(autofocusSettingsProvider).value!.binning, 1);
      n.setBinning(2);
      expect(container.read(autofocusSettingsProvider).value!.binning, 2);
    });

    test('setAfFilter trims + rejects empty or whitespace-only', () {
      final n = container.read(autofocusSettingsProvider.notifier);
      n.setAfFilter('');
      n.setAfFilter('   ');
      n.setAfFilter('\t\n');
      expect(container.read(autofocusSettingsProvider).value!.afFilter, 'L');
      n.setAfFilter('  Hα  ');
      expect(container.read(autofocusSettingsProvider).value!.afFilter, 'Hα');
    });

    test('triggerHfrDriftPct clamps to [0, 100]', () {
      final n = container.read(autofocusSettingsProvider.notifier);
      n.setTriggerHfrDriftPct(-1);
      n.setTriggerHfrDriftPct(101);
      expect(container.read(autofocusSettingsProvider).value!.triggerHfrDriftPct,
          15);
      n.setTriggerHfrDriftPct(25);
      expect(container.read(autofocusSettingsProvider).value!.triggerHfrDriftPct,
          25);
    });

    test('triggerTempDeltaC + everyNHours reject negative', () {
      final n = container.read(autofocusSettingsProvider.notifier);
      n.setTriggerTempDeltaC(-1);
      n.setEveryNHours(-1);
      final s = container.read(autofocusSettingsProvider).value!;
      expect(s.triggerTempDeltaC, 2.0);
      expect(s.everyNHours, 2);
    });

    test('method + booleans assign directly', () {
      final n = container.read(autofocusSettingsProvider.notifier);
      n.setMethod(AutofocusMethod.fwhm);
      n.setRunAfterFilterChange(false);
      n.setAbortSequenceOnAfFailure(false);
      n.setRestorePositionOnFailure(false);
      final s = container.read(autofocusSettingsProvider).value!;
      expect(s.method, AutofocusMethod.fwhm);
      expect(s.runAfterFilterChange, isFalse);
      expect(s.abortSequenceOnAfFailure, isFalse);
      expect(s.restorePositionOnFailure, isFalse);
    });

    test('telescope type defaults to other and assigns directly (§59.4)', () {
      expect(container.read(autofocusSettingsProvider).value!.telescopeType,
          TelescopeType.other);
      container
          .read(autofocusSettingsProvider.notifier)
          .setTelescopeType(TelescopeType.sct);
      expect(container.read(autofocusSettingsProvider).value!.telescopeType,
          TelescopeType.sct);
    });

    test('telescope type wire strings are exact §59.13 values', () {
      // The daemon treats anything unrecognised as `other`, so the client must
      // emit the exact wire strings and read unknowns defensively.
      const wire = {
        TelescopeType.refractor: 'refractor',
        TelescopeType.sct: 'sct',
        TelescopeType.mak: 'mak',
        TelescopeType.rc: 'rc',
        TelescopeType.newtonian: 'newtonian',
        TelescopeType.other: 'other',
      };
      for (final e in wire.entries) {
        expect(telescopeTypeToWire(e.key), e.value);
        expect(telescopeTypeFromWire(e.value), e.key);
      }
      expect(telescopeTypeFromWire(null), TelescopeType.other);
      expect(telescopeTypeFromWire('SCT'), TelescopeType.other,
          reason: 'wire strings are case-sensitive-exact');
      expect(telescopeTypeFromWire('dobsonian'), TelescopeType.other,
          reason: 'a future/unknown value must degrade, not throw');
    });
  });
}
