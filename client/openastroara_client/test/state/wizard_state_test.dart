import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/wizard_state.dart';

void main() {
  group('WizardController', () {
    late ProviderContainer container;

    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('starts on step 1 with empty draft', () {
      final state = container.read(wizardControllerProvider);
      expect(state.step, 1);
      expect(state.draft.skippedScreens, isEmpty);
    });

    test('next advances step', () {
      final notifier = container.read(wizardControllerProvider.notifier);
      notifier.next();
      expect(container.read(wizardControllerProvider).step, 2);
      notifier.next();
      expect(container.read(wizardControllerProvider).step, 3);
    });

    test('next clamps at totalSteps', () {
      final notifier = container.read(wizardControllerProvider.notifier);
      notifier.jumpTo(ProfileWizard.totalSteps);
      notifier.next();
      expect(container.read(wizardControllerProvider).step,
          ProfileWizard.totalSteps);
    });

    test('back decrements step but never below 1', () {
      final notifier = container.read(wizardControllerProvider.notifier);
      notifier.back();
      expect(container.read(wizardControllerProvider).step, 1);
      notifier.jumpTo(5);
      notifier.back();
      expect(container.read(wizardControllerProvider).step, 4);
    });

    test('jumpTo clamps to [1, totalSteps]', () {
      final notifier = container.read(wizardControllerProvider.notifier);
      notifier.jumpTo(-5);
      expect(container.read(wizardControllerProvider).step, 1);
      notifier.jumpTo(ProfileWizard.totalSteps + 100);
      expect(container.read(wizardControllerProvider).step,
          ProfileWizard.totalSteps);
      notifier.jumpTo(7);
      expect(container.read(wizardControllerProvider).step, 7);
    });

    test('skipCurrent adds to skippedScreens + advances', () {
      final notifier = container.read(wizardControllerProvider.notifier);
      notifier.jumpTo(3);
      notifier.skipCurrent();
      expect(container.read(wizardControllerProvider).step, 4);
      expect(container.read(wizardControllerProvider).draft.skippedScreens,
          contains(3));
    });

    test('skipCurrent on last step still notifies listeners', () {
      final notifier = container.read(wizardControllerProvider.notifier);
      notifier.jumpTo(ProfileWizard.totalSteps);
      final before = container.read(wizardControllerProvider);
      notifier.skipCurrent();
      final after = container.read(wizardControllerProvider);
      // Step doesn't advance past totalSteps.
      expect(after.step, ProfileWizard.totalSteps);
      // But a new state object was emitted (so the rebuild fires).
      expect(identical(before, after), isFalse);
      expect(after.draft.skippedScreens, contains(ProfileWizard.totalSteps));
    });
  });

  group('ProfileWizard catalog', () {
    test('has entries for every step 1..totalSteps', () {
      for (var i = 1; i <= ProfileWizard.totalSteps; i++) {
        expect(ProfileWizard.steps[i], isNotNull,
            reason: 'missing step $i');
      }
    });
  });
}
