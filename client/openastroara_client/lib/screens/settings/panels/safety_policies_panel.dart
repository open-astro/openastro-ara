import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import '../../../util/friendly_error.dart';
import '../../../l10n/l10n.dart';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/loaded_section.dart';
import '../../../state/settings/panel_save_registry.dart';
import '../../../state/settings/safety_policies_state.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/section_panel.dart';
import '../../../widgets/settings/settings_row.dart';

/// §35 Safety Policies panel — editable. The fields show the rig's copy of the
/// `safety` section (ADR 0015: loaded before it is saved; not loaded, they are
/// not shown) and save back on Save.
class SafetyPoliciesPanel extends ConsumerStatefulWidget {
  const SafetyPoliciesPanel({super.key});

  @override
  ConsumerState<SafetyPoliciesPanel> createState() =>
      _SafetyPoliciesPanelState();
}

class _SafetyPoliciesPanelState extends ConsumerState<SafetyPoliciesPanel>
    with PanelSaveRegistration, SectionPanel {
  String? _lastError;

  @override
  List<PanelSection> get sections => [safetyPoliciesProvider];

  @override
  Future<void> panelSave() async {
    setState(() => _lastError = null);
    // A field still being edited commits on focus loss; drop focus first so
    // the typed value is what gets sent.
    FocusManager.instance.primaryFocus?.unfocus();
    final error = await saveSections();
    if (mounted) setState(() => _lastError = error);
  }

  Future<void> _rearmFirstFlip() async {
    final messenger = ScaffoldMessenger.of(context);
    final l10n = context.l10n;
    try {
      await ref.read(safetyPoliciesProvider.notifier).rearmFirstFlip();
      if (!mounted) return;
      messenger.showSnackBar(
        SnackBar(content: Text(l10n.safetyFirstFlipRearmedSnack)),
      );
    } catch (e) {
      if (!mounted) return;
      messenger.showSnackBar(
        SnackBar(
          content: Text(
            e is SectionNotLoadedException || e is SectionConflictException
                ? e.toString()
                : friendlyError(l10n, e, action: l10n.actionRearmSafety),
          ),
        ),
      );
    }
  }

  Future<void> _confirmFirstFlip() async {
    final messenger = ScaffoldMessenger.of(context);
    final l10n = context.l10n;
    try {
      await ref.read(safetyPoliciesProvider.notifier).confirmFirstFlip();
      if (!mounted) return;
      messenger.showSnackBar(
        SnackBar(content: Text(l10n.safetyFirstFlipConfirmedSnack)),
      );
    } catch (e) {
      if (!mounted) return;
      messenger.showSnackBar(
        SnackBar(
          content: Text(
            e is SectionNotLoadedException || e is SectionConflictException
                ? e.toString()
                : friendlyError(l10n, e, action: l10n.actionConfirmFlip),
          ),
        ),
      );
    }
  }

  /// #1589 — what the first-flip row says: not confirmed and no completed
  /// flip, a completed flip waiting for the user's confirm, or confirmed.
  static String _firstFlipStatus(AppLocalizations l10n, SafetyPolicies s) {
    final completed = s.firstFlipCompletedUtc;
    if (s.firstFlipConfirmed) {
      return l10n.safetyFirstFlipConfirmedStatus;
    }
    if (completed == null) {
      return l10n.safetyFirstFlipUnconfirmedStatus;
    }
    final when = DateFormat('yyyy-MM-dd HH:mm').format(completed.toLocal());
    return s.firstFlipPierSideChanged
        ? l10n.safetyFirstFlipCompletedPierChanged(when)
        : l10n.safetyFirstFlipCompletedCheckByEye(when);
  }

  @override
  Widget build(BuildContext context) {
    final gate = sectionGate();
    if (gate != null) return gate;
    final s = ref.watch(safetyPoliciesProvider).value!;
    final n = ref.read(safetyPoliciesProvider.notifier);
    SafetyPolicies now() => ref.read(safetyPoliciesProvider).value!;

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        ?sectionConflict(),
        const SettingsSectionHeader('On unsafe weather'),
        SettingsDropdownRow<UnsafeAction>(
          label: 'Action',
          helpKey: 'safety.policies.on_unsafe',
          value: s.onUnsafe,
          items: const {
            UnsafeAction.pauseAndPark: 'Pause + park + close dome',
            UnsafeAction.parkOnly: 'Park only',
            UnsafeAction.abortAndPark: 'Abort sequence + park',
            UnsafeAction.ignore: 'Ignore (not recommended)',
          },
          onChanged: (v) {
            if (v != null) n.setOnUnsafe(v);
          },
        ),
        SettingsSwitchRow(
          label: 'Auto-resume when safe',
          helpKey: 'safety.policies.auto_resume',
          value: s.autoResumeWhenSafe,
          onChanged: n.setAutoResumeWhenSafe,
        ),
        EditableNumberRow(
          label: 'Resume delay (min)',
          helpKey: 'safety.policies.resume_delay',
          currentValue: s.resumeDelayMin.toString(),
          getCanonical: () => now().resumeDelayMin.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setResumeDelayMin(v);
          },
        ),
        const SettingsSectionHeader('Weather limits'),
        SettingsSwitchRow(
          label: 'React to weather-station thresholds',
          helpKey: 'safety.policies.weather_triggers',
          value: s.weatherTriggersEnabled,
          hint:
              'With a weather station connected, a breached threshold below '
              'makes conditions unsafe — the same reaction as the safety '
              'monitor (the action above runs, and auto-resume waits for the '
              'weather to clear too).',
          onChanged: n.setWeatherTriggersEnabled,
        ),
        if (s.weatherTriggersEnabled) ...[
          EditableNumberRow(
            label: 'Max wind (km/h, sustained or gust)',
            helpKey: 'safety.policies.max_wind_kmh',
            currentValue: s.maxWindKmh.toString(),
            getCanonical: () => now().maxWindKmh.toString(),
            parse: (str) {
              final v = int.tryParse(str);
              if (v != null) n.setMaxWindKmh(v);
            },
          ),
          EditableNumberRow(
            label: 'Max humidity (%)',
            helpKey: 'safety.policies.max_humidity_pct',
            currentValue: s.maxHumidityPct.toString(),
            getCanonical: () => now().maxHumidityPct.toString(),
            parse: (str) {
              final v = int.tryParse(str);
              if (v != null) n.setMaxHumidityPct(v);
            },
          ),
          EditableNumberRow(
            label: 'Min dew delta (°C above dew point)',
            helpKey: 'safety.policies.min_dew_delta_c',
            currentValue: s.minDewDeltaC.toStringAsFixed(1),
            getCanonical: () => now().minDewDeltaC.toStringAsFixed(1),
            parse: (str) {
              final v = double.tryParse(str);
              if (v != null) n.setMinDewDeltaC(v);
            },
          ),
        ],
        const SettingsSectionHeader('On meridian flip'),
        SettingsSwitchRow(
          label: 'Auto flip',
          helpKey: 'safety.policies.meridian_flip_auto',
          value: s.meridianFlipAuto,
          onChanged: n.setMeridianFlipAuto,
        ),
        EditableNumberRow(
          label: 'Stop before meridian (min)',
          helpKey: 'safety.policies.meridian_pause_min',
          currentValue: s.meridianPauseMin.toString(),
          getCanonical: () => now().meridianPauseMin.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setMeridianPauseMin(v);
          },
        ),
        // #2044 — the flip window after the meridian, and the flip-off stop
        // (also set from Simple Mode's Mount window).
        EditableNumberRow(
          label: 'Flip after the meridian (min)',
          helpKey: 'safety.policies.minutes_after_meridian',
          currentValue: s.minutesAfterMeridian.toString(),
          getCanonical: () => now().minutesAfterMeridian.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setMinutesAfterMeridian(v);
          },
        ),
        EditableNumberRow(
          label: 'Latest flip after the meridian (min)',
          helpKey: 'safety.policies.max_minutes_after_meridian',
          currentValue: s.maxMinutesAfterMeridian.toString(),
          getCanonical: () => now().maxMinutesAfterMeridian.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setMaxMinutesAfterMeridian(v);
          },
        ),
        EditableNumberRow(
          label: 'Stop before meridian, flip off (min)',
          helpKey: 'safety.policies.stop_before_meridian_min',
          currentValue: s.stopBeforeMeridianMin.toString(),
          getCanonical: () => now().stopBeforeMeridianMin.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setStopBeforeMeridianMin(v);
          },
        ),
        SettingsSwitchRow(
          label: 'Re-center after flip',
          value: s.meridianRecenter,
          onChanged: n.setMeridianRecenter,
        ),
        // §58.4 step 3 (#1138) — re-focus policy and its drift threshold.
        SettingsDropdownRow<MeridianRefocusMode>(
          label: 'Re-focus after flip',
          helpKey: 'safety.policies.meridian_refocus',
          value: s.meridianRefocus,
          items: const {
            MeridianRefocusMode.ifTempDrifted:
                'Only if the focuser temperature drifted',
            MeridianRefocusMode.always: 'Always',
            MeridianRefocusMode.never: 'Never',
          },
          onChanged: (v) {
            if (v != null) n.setMeridianRefocus(v);
          },
        ),
        if (s.meridianRefocus == MeridianRefocusMode.ifTempDrifted)
          EditableNumberRow(
            label: 'Re-focus when drift exceeds (°C)',
            helpKey: 'safety.policies.meridian_refocus_temp_threshold_c',
            currentValue: s.meridianRefocusTempThresholdC.toString(),
            getCanonical: () => now().meridianRefocusTempThresholdC.toString(),
            parse: (str) {
              final v = double.tryParse(str);
              if (v != null) n.setMeridianRefocusTempThresholdC(v);
            },
          ),
        // §58.4 step 4 — how guiding comes back.
        // A legacy profile (null mode) shows the daemon's effective behaviour —
        // guiding restored when a guider is connected — without writing it back
        // until the user picks something.
        SettingsDropdownRow<GuiderRecalMode>(
          label: 'Guiding after flip',
          helpKey: 'safety.policies.meridian_guider_recal',
          value: s.meridianGuiderRecal ?? GuiderRecalMode.autoRestore,
          items: const {
            GuiderRecalMode.autoRestore: 'Restore calibration (fast)',
            GuiderRecalMode.full: 'Full recalibration (5-10 min)',
            GuiderRecalMode.skip: 'Do not resume guiding',
          },
          onChanged: (v) {
            if (v != null) n.setMeridianGuiderRecal(v);
          },
        ),
        SettingsSwitchRow(
          label: 'Extra care on flips when you are away',
          hint:
              'Pre-flip flight check, in-slew watchdog and hard pier-side '
              'verification. Turn off only if this mount misreports pier side.',
          value: s.flipSafetyEnabled,
          onChanged: n.setFlipSafetyEnabled,
        ),
        EditableNumberRow(
          label: 'Expected flip-slew duration (s)',
          helpKey: 'safety.policies.expected_flip_slew_s',
          currentValue: s.expectedFlipSlewSeconds.toString(),
          getCanonical: () => now().expectedFlipSlewSeconds.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setExpectedFlipSlewSeconds(v);
          },
        ),
        SettingsSwitchRow(
          label: 'Louder alerts overnight',
          hint:
              'During the unattended window — astronomical darkness at the '
              'site unless you set the from/until times below — '
              'equipment-impacting warnings ride one severity level higher so '
              'the alarm behaviour engages earlier for a sleeping user.',
          value: s.unattendedEscalation,
          onChanged: n.setUnattendedEscalation,
        ),
        // §58.10 (#1138) — an explicit unattended window; blank = the sun decides.
        EditableNumberRow(
          label: 'Unattended from (HH:mm, blank = dusk)',
          helpKey: 'safety.policies.unattended_window',
          currentValue: s.unattendedWindowStart,
          getCanonical: () => now().unattendedWindowStart,
          parse: n.setUnattendedWindowStart,
        ),
        EditableNumberRow(
          label: 'Unattended until (HH:mm, blank = dawn)',
          helpKey: 'safety.policies.unattended_window',
          currentValue: s.unattendedWindowEnd,
          getCanonical: () => now().unattendedWindowEnd,
          parse: n.setUnattendedWindowEnd,
        ),
        SettingsSwitchRow(
          label: 'Put the rig to bed if nobody responds',
          hint:
              'If a run pauses awaiting your attention and nobody responds '
              'within the wait window, Ara parks the mount, warms the '
              'cooler and disconnects the equipment. Any command — or simply '
              'opening the app — cancels the countdown.',
          value: s.unattendedShutdownEnabled,
          onChanged: n.setUnattendedShutdownEnabled,
        ),
        EditableNumberRow(
          label: 'Unattended shutdown wait (min)',
          helpKey: 'safety.policies.unattended_shutdown_wait_min',
          currentValue: s.unattendedShutdownWaitMinutes.toString(),
          getCanonical: () => now().unattendedShutdownWaitMinutes.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setUnattendedShutdownWaitMinutes(v);
          },
        ),
        // §58.8 / #1589 — first-flip status, Confirm and Re-arm. The daemon
        // owns the fields: it records a flip that ran to the end, and only
        // the user confirms it, here, once one is on record. Re-arm (and an
        // optics change) asks again.
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Row(
            children: [
              Expanded(
                child: Text(
                  _firstFlipStatus(context.l10n, s),
                  style: Theme.of(context).textTheme.bodyMedium,
                ),
              ),
              const SizedBox(width: 8),
              FilledButton(
                key: const Key('confirm_first_flip'),
                // Immediate daemon round-trip, like Re-arm: the confirmation
                // is daemon-owned and the general Save can't touch it.
                onPressed:
                    !s.firstFlipConfirmed && s.firstFlipCompletedUtc != null
                    ? _confirmFirstFlip
                    : null,
                child: Text(context.l10n.safetyFirstFlipConfirmButton),
              ),
              const SizedBox(width: 8),
              OutlinedButton(
                key: const Key('rearm_first_flip'),
                // Immediate daemon round-trip (not deferred to Save): the
                // fields are daemon-owned and the general Save deliberately
                // can't touch them. A completed flip awaiting Confirm can be
                // dropped too (the rig changed since it ran).
                onPressed:
                    s.firstFlipConfirmed || s.firstFlipCompletedUtc != null
                    ? _rearmFirstFlip
                    : null,
                child: const Text('Re-arm'),
              ),
            ],
          ),
        ),
        // #1589 — the old "On altitude limit" action had no daemon behind it
        // and is gone; the park switch stays.
        const SettingsSectionHeader('When targets run out'),
        SettingsSwitchRow(
          label: 'Park if no more targets',
          value: s.parkIfNoMoreTargets,
          onChanged: n.setParkIfNoMoreTargets,
        ),
        const SettingsSectionHeader('On guider lost'),
        SettingsDropdownRow<GuiderLostAction>(
          label: 'Action',
          helpKey: 'safety.policies.on_guider_lost',
          value: s.onGuiderLost,
          items: const {
            GuiderLostAction.pauseAndRetry: 'Pause + retry until timeout',
            GuiderLostAction.skipTarget: 'Skip target',
            GuiderLostAction.abortSequence: 'Abort sequence',
          },
          onChanged: (v) {
            if (v != null) n.setOnGuiderLost(v);
          },
        ),
        EditableNumberRow(
          label: 'Retry timeout (s)',
          helpKey: 'safety.policies.guider_retry_timeout',
          currentValue: s.guiderRetryTimeoutSec.toString(),
          getCanonical: () => now().guiderRetryTimeoutSec.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setGuiderRetryTimeoutSec(v);
          },
        ),
        SettingsSwitchRow(
          label: 'Skip target if recovery fails',
          value: s.skipTargetIfRecoveryFails,
          onChanged: n.setSkipTargetIfRecoveryFails,
        ),
        const SettingsSectionHeader('When the drive is nearly full'),
        SettingsDropdownRow<DiskSpaceCriticalAction>(
          label: 'Action',
          helpKey: 'safety.policies.on_disk_space_critical',
          value: s.onDiskSpaceCritical,
          items: const {
            DiskSpaceCriticalAction.warn:
                'Warn only (diagnostic + notification)',
            DiskSpaceCriticalAction.pause: 'Pause the running sequence',
            DiskSpaceCriticalAction.abort: 'Abort the running sequence',
          },
          onChanged: (v) {
            if (v != null) n.setOnDiskSpaceCritical(v);
          },
        ),
        const SizedBox(height: 24),
        if (_lastError != null) ...[
          Text(
            _lastError!,
            style: TextStyle(color: Theme.of(context).colorScheme.error),
          ),
          const SizedBox(height: 12),
        ],
        // Save lives in the settings-shell header (PanelSaveRegistration) —
        // fixed chrome, always visible, no scrolling to find it.
      ],
    );
  }
}
