import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';

import '../../support/fake_section_rig.dart';
import '../../support/section_load.dart';

void main() {
  group('SafetyPoliciesNotifier', () {
    late ProviderContainer container;
    // ADR 0015: an edit counts only on a copy the rig served (its defaults).
    setUp(() async {
      container = ProviderContainer();
      await loadSection(container, safetyPoliciesProvider, 'safety');
    });
    tearDown(() => container.dispose());

    test('#1138 flip recovery policies and the unattended window', () async {
      final c = ProviderContainer();
      addTearDown(c.dispose);
      // A profile that has picked a guider mode (a legacy one sends null).
      await loadSection(c, safetyPoliciesProvider, 'safety', {
        ...rigDefaults['safety']!,
        'meridian_guider_recal': 'auto_restore',
      });
      final n = c.read(safetyPoliciesProvider.notifier);
      final d = c.read(safetyPoliciesProvider).value!;
      expect(d.meridianRefocus, MeridianRefocusMode.ifTempDrifted);
      expect(d.meridianRefocusTempThresholdC, 2.0);
      expect(d.meridianGuiderRecal, GuiderRecalMode.autoRestore);
      expect(d.unattendedWindowStart, '');

      n.setMeridianGuiderRecal(GuiderRecalMode.skip);
      expect(
        c.read(safetyPoliciesProvider).value!.meridianRecalGuider,
        isFalse,
        reason: 'the legacy bool follows the mode',
      );
      n.setMeridianGuiderRecal(GuiderRecalMode.full);
      expect(c.read(safetyPoliciesProvider).value!.meridianRecalGuider, isTrue);

      n.setMeridianRefocusTempThresholdC(0);
      expect(
        c.read(safetyPoliciesProvider).value!.meridianRefocusTempThresholdC,
        2.0,
        reason: 'non-positive rejected',
      );
      n.setMeridianRefocusTempThresholdC(50);
      expect(
        c.read(safetyPoliciesProvider).value!.meridianRefocusTempThresholdC,
        10.0,
        reason: 'clamped to the registry range',
      );
      n.setMeridianRefocusTempThresholdC(0.1);
      expect(
        c.read(safetyPoliciesProvider).value!.meridianRefocusTempThresholdC,
        0.5,
      );

      n.setUnattendedWindowStart('22:00');
      n.setUnattendedWindowEnd('6:00');
      expect(
        c.read(safetyPoliciesProvider).value!.unattendedWindowStart,
        '22:00',
      );
      expect(c.read(safetyPoliciesProvider).value!.unattendedWindowEnd, '6:00');
      n.setUnattendedWindowEnd('ten pm');
      expect(
        c.read(safetyPoliciesProvider).value!.unattendedWindowEnd,
        '6:00',
        reason: 'malformed clock text rejected',
      );
      n.setUnattendedWindowEnd('');
      expect(
        c.read(safetyPoliciesProvider).value!.unattendedWindowEnd,
        '',
        reason: 'blank clears back to the sun',
      );
      expect(isClockText('24:00'), isFalse);
      expect(isClockText('23:59'), isTrue);
    });

    // The fallback the read-only panels show while the rig's copy is not in.
    test('defaults match playbook §35', () {
      const s = SafetyPolicies();
      expect(s.onUnsafe, UnsafeAction.pauseAndPark);
      expect(s.autoResumeWhenSafe, isTrue);
      expect(s.resumeDelayMin, 10);
      expect(s.meridianFlipAuto, isTrue);
      expect(s.meridianPauseMin, 5);
      expect(s.meridianRecenter, isTrue);
      expect(s.meridianRecalGuider, isTrue);
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
      final s = container.read(safetyPoliciesProvider).value!;
      expect(s.resumeDelayMin, 10);
      expect(s.meridianPauseMin, 5);
      expect(s.guiderRetryTimeoutSec, 60);
    });

    test('numeric setters accept zero + positive', () {
      final n = container.read(safetyPoliciesProvider.notifier);
      n.setResumeDelayMin(0);
      n.setMeridianPauseMin(2);
      n.setGuiderRetryTimeoutSec(120);
      final s = container.read(safetyPoliciesProvider).value!;
      expect(s.resumeDelayMin, 0);
      expect(s.meridianPauseMin, 2);
      expect(s.guiderRetryTimeoutSec, 120);
    });

    test('action enum setters switch policy', () {
      final n = container.read(safetyPoliciesProvider.notifier);
      n.setOnUnsafe(UnsafeAction.abortAndPark);
      n.setOnGuiderLost(GuiderLostAction.skipTarget);
      final s = container.read(safetyPoliciesProvider).value!;
      expect(s.onUnsafe, UnsafeAction.abortAndPark);
      expect(s.onGuiderLost, GuiderLostAction.skipTarget);
    });

    test('bool toggles flip independently', () {
      final n = container.read(safetyPoliciesProvider.notifier);
      n.setAutoResumeWhenSafe(false);
      n.setMeridianRecenter(false);
      n.setSkipTargetIfRecoveryFails(false);
      final s = container.read(safetyPoliciesProvider).value!;
      expect(s.autoResumeWhenSafe, isFalse);
      expect(s.meridianRecenter, isFalse);
      expect(s.skipTargetIfRecoveryFails, isFalse);
      // Others unchanged.
      expect(s.meridianFlipAuto, isTrue);
      expect(s.meridianRecalGuider, isTrue);
    });

    test('§29 on-disk-space-critical defaults to warn + sets', () {
      final n = container.read(safetyPoliciesProvider.notifier);
      expect(
        container.read(safetyPoliciesProvider).value!.onDiskSpaceCritical,
        DiskSpaceCriticalAction.warn,
      );
      n.setOnDiskSpaceCritical(DiskSpaceCriticalAction.pause);
      expect(
        container.read(safetyPoliciesProvider).value!.onDiskSpaceCritical,
        DiskSpaceCriticalAction.pause,
        reason: "#1185",
      );
      n.setOnDiskSpaceCritical(DiskSpaceCriticalAction.abort);
      expect(
        container.read(safetyPoliciesProvider).value!.onDiskSpaceCritical,
        DiskSpaceCriticalAction.abort,
      );
    });

    test('§58.10 unattended escalation defaults on + toggles', () {
      final n = container.read(safetyPoliciesProvider.notifier);
      expect(
        container.read(safetyPoliciesProvider).value!.unattendedEscalation,
        isTrue,
      );
      n.setUnattendedEscalation(false);
      expect(
        container.read(safetyPoliciesProvider).value!.unattendedEscalation,
        isFalse,
      );
    });

    test('§58.12 unattended shutdown defaults on/30min; wait rejects < 1', () {
      final n = container.read(safetyPoliciesProvider.notifier);
      final s0 = container.read(safetyPoliciesProvider).value!;
      expect(s0.unattendedShutdownEnabled, isTrue);
      expect(s0.unattendedShutdownWaitMinutes, 30);

      n.setUnattendedShutdownEnabled(false);
      n.setUnattendedShutdownWaitMinutes(45);
      var s1 = container.read(safetyPoliciesProvider).value!;
      expect(s1.unattendedShutdownEnabled, isFalse);
      expect(s1.unattendedShutdownWaitMinutes, 45);

      // The daemon clamps the wait to >= 1 minute — the client mirrors it so
      // a typo can't stage a value the server would silently rewrite.
      n.setUnattendedShutdownWaitMinutes(0);
      n.setUnattendedShutdownWaitMinutes(-5);
      expect(
        container
            .read(safetyPoliciesProvider)
            .value!
            .unattendedShutdownWaitMinutes,
        45,
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
      expect(
        confirmed.copyWith(firstFlipConfirmed: false).firstFlipConfirmed,
        isFalse,
      );
      expect(
        container.read(safetyPoliciesProvider).value!.firstFlipConfirmed,
        isFalse,
        reason: 'fresh state mirrors the daemon default — announce armed',
      );

      // The re-arm patch shape: only the flag moves; staged panel edits on the
      // other fields survive (rearmFirstFlip patches via copyWith rather than
      // replacing state with the daemon's persisted echo).
      const staged = SafetyPolicies(
        resumeDelayMin: 42,
        firstFlipConfirmed: true,
      );
      final patched = staged.copyWith(firstFlipConfirmed: false);
      expect(patched.firstFlipConfirmed, isFalse);
      expect(
        patched.resumeDelayMin,
        42,
        reason: 'unsaved edits must not be clobbered by the re-arm echo',
      );
    });
  });

  // The daemon's fault policies are in the same section; the client has no
  // field for them, and the daemon fills a key a PUT leaves out with its own
  // default.
  const faultPolicies = {
    'hot_reconnect_enabled': false,
    'on_camera_lost': 'pause',
    'on_mount_lost': 'notify_only',
    'on_tracking_lost': 'pause',
    'switch_value_tolerance_pct': 12.5,
  };

  test("safety: a Save keeps the rig's fault policies the client has no field for (ADR 0015)", () async {
    final rig = FakeSectionRig({
      'safety': {...rigDefaults['safety']!, ...faultPolicies},
    });
    final c = ProviderContainer();
    addTearDown(c.dispose);
    final n = c.read(safetyPoliciesProvider.notifier);
    expect(await n.load(rig.api()), isTrue);
    n.setMeridianPauseMin(7);
    await n.save(rig.api());
    final body = rig.puts.single.body as Map;
    expect(body['meridian_pause_min'], 7);
    for (final e in faultPolicies.entries) {
      expect(body[e.key], e.value, reason: e.key);
    }
  });
}
