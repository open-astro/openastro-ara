import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/settings/autofocus_settings_state.dart';

void main() {
  group('AutofocusSettingsNotifier', () {
    late ProviderContainer container;
    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('defaults match playbook §37.11', () {
      final s = container.read(autofocusSettingsProvider);
      expect(s.method, AutofocusMethod.hfrVCurve);
      expect(s.steps, 7);
      expect(s.stepSize, 50);
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

    test('setSteps clamps to [3, 31]', () {
      final n = container.read(autofocusSettingsProvider.notifier);
      n.setSteps(2);
      n.setSteps(32);
      expect(container.read(autofocusSettingsProvider).steps, 7);
      n.setSteps(5);
      n.setSteps(15);
      expect(container.read(autofocusSettingsProvider).steps, 15);
    });

    test('setStepSize + setExposureSeconds reject non-positive', () {
      final n = container.read(autofocusSettingsProvider.notifier);
      n.setStepSize(0);
      n.setStepSize(-10);
      n.setExposureSeconds(0);
      n.setExposureSeconds(-1);
      final s = container.read(autofocusSettingsProvider);
      expect(s.stepSize, 50);
      expect(s.exposureSeconds, 5);
    });

    test('setBinning rejects below 1', () {
      final n = container.read(autofocusSettingsProvider.notifier);
      n.setBinning(0);
      n.setBinning(-1);
      expect(container.read(autofocusSettingsProvider).binning, 1);
      n.setBinning(2);
      expect(container.read(autofocusSettingsProvider).binning, 2);
    });

    test('setAfFilter trims + rejects empty or whitespace-only', () {
      final n = container.read(autofocusSettingsProvider.notifier);
      n.setAfFilter('');
      n.setAfFilter('   ');
      n.setAfFilter('\t\n');
      expect(container.read(autofocusSettingsProvider).afFilter, 'L');
      n.setAfFilter('  Hα  ');
      expect(container.read(autofocusSettingsProvider).afFilter, 'Hα');
    });

    test('triggerHfrDriftPct clamps to [0, 100]', () {
      final n = container.read(autofocusSettingsProvider.notifier);
      n.setTriggerHfrDriftPct(-1);
      n.setTriggerHfrDriftPct(101);
      expect(container.read(autofocusSettingsProvider).triggerHfrDriftPct,
          15);
      n.setTriggerHfrDriftPct(25);
      expect(container.read(autofocusSettingsProvider).triggerHfrDriftPct,
          25);
    });

    test('triggerTempDeltaC + everyNHours reject negative', () {
      final n = container.read(autofocusSettingsProvider.notifier);
      n.setTriggerTempDeltaC(-1);
      n.setEveryNHours(-1);
      final s = container.read(autofocusSettingsProvider);
      expect(s.triggerTempDeltaC, 2.0);
      expect(s.everyNHours, 2);
    });

    test('method + booleans assign directly', () {
      final n = container.read(autofocusSettingsProvider.notifier);
      n.setMethod(AutofocusMethod.fwhm);
      n.setRunAfterFilterChange(false);
      n.setAbortSequenceOnAfFailure(false);
      n.setRestorePositionOnFailure(false);
      final s = container.read(autofocusSettingsProvider);
      expect(s.method, AutofocusMethod.fwhm);
      expect(s.runAfterFilterChange, isFalse);
      expect(s.abortSequenceOnAfFailure, isFalse);
      expect(s.restorePositionOnFailure, isFalse);
    });
  });
}
