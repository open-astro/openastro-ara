import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';

void main() {
  group('SafetyPoliciesNotifier', () {
    late ProviderContainer container;
    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('defaults match playbook §35', () {
      final s = container.read(safetyPoliciesProvider);
      expect(s.onUnsafe, UnsafeAction.pauseAndPark);
      expect(s.autoResumeWhenSafe, isTrue);
      expect(s.resumeDelayMin, 10);
      expect(s.meridianFlipAuto, isTrue);
      expect(s.meridianPauseMin, 5);
      expect(s.meridianRecenter, isTrue);
      expect(s.meridianRecalGuider, isTrue);
      expect(s.onAltitudeLimit, AltitudeLimitAction.skipTarget);
      expect(s.parkIfNoMoreTargets, isTrue);
      expect(s.onGuiderLost, GuiderLostAction.pauseAndRetry);
      expect(s.guiderRetryTimeoutSec, 60);
      expect(s.skipTargetIfRecoveryFails, isTrue);
    });

    test('numeric setters reject negative', () {
      final n = container.read(safetyPoliciesProvider.notifier);
      n.setResumeDelayMin(-1);
      n.setMeridianPauseMin(-1);
      n.setGuiderRetryTimeoutSec(-5);
      final s = container.read(safetyPoliciesProvider);
      expect(s.resumeDelayMin, 10);
      expect(s.meridianPauseMin, 5);
      expect(s.guiderRetryTimeoutSec, 60);
    });

    test('numeric setters accept zero + positive', () {
      final n = container.read(safetyPoliciesProvider.notifier);
      n.setResumeDelayMin(0);
      n.setMeridianPauseMin(2);
      n.setGuiderRetryTimeoutSec(120);
      final s = container.read(safetyPoliciesProvider);
      expect(s.resumeDelayMin, 0);
      expect(s.meridianPauseMin, 2);
      expect(s.guiderRetryTimeoutSec, 120);
    });

    test('action enum setters switch policy', () {
      final n = container.read(safetyPoliciesProvider.notifier);
      n.setOnUnsafe(UnsafeAction.abortAndPark);
      n.setOnAltitudeLimit(AltitudeLimitAction.abortSequence);
      n.setOnGuiderLost(GuiderLostAction.skipTarget);
      final s = container.read(safetyPoliciesProvider);
      expect(s.onUnsafe, UnsafeAction.abortAndPark);
      expect(s.onAltitudeLimit, AltitudeLimitAction.abortSequence);
      expect(s.onGuiderLost, GuiderLostAction.skipTarget);
    });

    test('bool toggles flip independently', () {
      final n = container.read(safetyPoliciesProvider.notifier);
      n.setAutoResumeWhenSafe(false);
      n.setMeridianRecenter(false);
      n.setSkipTargetIfRecoveryFails(false);
      final s = container.read(safetyPoliciesProvider);
      expect(s.autoResumeWhenSafe, isFalse);
      expect(s.meridianRecenter, isFalse);
      expect(s.skipTargetIfRecoveryFails, isFalse);
      // Others unchanged.
      expect(s.meridianFlipAuto, isTrue);
      expect(s.meridianRecalGuider, isTrue);
    });

    test('§29 on-disk-space-critical defaults to warn + sets', () {
      final n = container.read(safetyPoliciesProvider.notifier);
      expect(container.read(safetyPoliciesProvider).onDiskSpaceCritical,
          DiskSpaceCriticalAction.warn);
      n.setOnDiskSpaceCritical(DiskSpaceCriticalAction.abort);
      expect(container.read(safetyPoliciesProvider).onDiskSpaceCritical,
          DiskSpaceCriticalAction.abort);
    });

    test('§58.10 unattended escalation defaults on + toggles', () {
      final n = container.read(safetyPoliciesProvider.notifier);
      expect(container.read(safetyPoliciesProvider).unattendedEscalation, isTrue);
      n.setUnattendedEscalation(false);
      expect(container.read(safetyPoliciesProvider).unattendedEscalation, isFalse);
    });

    test('§58.12 unattended shutdown defaults on/10min; wait rejects < 1', () {
      final n = container.read(safetyPoliciesProvider.notifier);
      final s0 = container.read(safetyPoliciesProvider);
      expect(s0.unattendedShutdownEnabled, isTrue);
      expect(s0.unattendedShutdownWaitMinutes, 10);

      n.setUnattendedShutdownEnabled(false);
      n.setUnattendedShutdownWaitMinutes(30);
      var s1 = container.read(safetyPoliciesProvider);
      expect(s1.unattendedShutdownEnabled, isFalse);
      expect(s1.unattendedShutdownWaitMinutes, 30);

      // The daemon clamps the wait to >= 1 minute — the client mirrors it so
      // a typo can't stage a value the server would silently rewrite.
      n.setUnattendedShutdownWaitMinutes(0);
      n.setUnattendedShutdownWaitMinutes(-5);
      expect(
        container.read(safetyPoliciesProvider).unattendedShutdownWaitMinutes,
        30,
      );
    });

    test('§58.8 the flag is daemon-owned — the model carries it, nothing local mutates it', () {
      // Model-level round-trip only: the notifier deliberately has NO local
      // mutation for firstFlipConfirmed in either direction — re-arm goes
      // through ProfileApi.rearmFirstFlip (the daemon's dedicated endpoint)
      // and the general safety PUT is ignored server-side for this field, so
      // a stale panel Save can neither clear nor set a confirmation.
      const confirmed = SafetyPolicies(firstFlipConfirmed: true);
      expect(confirmed.firstFlipConfirmed, isTrue);
      expect(confirmed.copyWith(firstFlipConfirmed: false).firstFlipConfirmed,
          isFalse);
      expect(container.read(safetyPoliciesProvider).firstFlipConfirmed, isFalse,
          reason: 'fresh state mirrors the daemon default — announce armed');

      // The re-arm patch shape: only the flag moves; staged panel edits on the
      // other fields survive (rearmFirstFlip patches via copyWith rather than
      // replacing state with the daemon's persisted echo).
      const staged = SafetyPolicies(resumeDelayMin: 42, firstFlipConfirmed: true);
      final patched = staged.copyWith(firstFlipConfirmed: false);
      expect(patched.firstFlipConfirmed, isFalse);
      expect(patched.resumeDelayMin, 42,
          reason: 'unsaved edits must not be clobbered by the re-arm echo');
    });
  });
}
