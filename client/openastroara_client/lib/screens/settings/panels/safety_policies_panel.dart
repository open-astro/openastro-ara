import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../services/profile_api.dart';
import '../../../state/saved_server_state.dart';
import '../../../state/settings/safety_policies_state.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §35 Safety Policies panel — editable. Phase 12h.6g added the daemon
/// round-trip — values hydrate from the active server on mount and
/// persist back on Save.
class SafetyPoliciesPanel extends ConsumerStatefulWidget {
  const SafetyPoliciesPanel({super.key});

  @override
  ConsumerState<SafetyPoliciesPanel> createState() =>
      _SafetyPoliciesPanelState();
}

class _SafetyPoliciesPanelState extends ConsumerState<SafetyPoliciesPanel> {
  bool _saving = false;
  String? _lastError;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _hydrate());
  }

  Future<void> _hydrate() async {
    final api = _api();
    if (api == null) return;
    try {
      await ref.read(safetyPoliciesProvider.notifier).hydrateFromServer(api);
    } catch (e) {
      if (mounted) setState(() => _lastError = 'Could not load saved values: $e');
    }
  }

  Future<void> _save() async {
    setState(() {
      _saving = true;
      _lastError = null;
    });
    final api = _api();
    final messenger = ScaffoldMessenger.of(context);
    if (api == null) {
      setState(() {
        _saving = false;
        _lastError = 'No active server — connect to a daemon first.';
      });
      messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
      return;
    }
    try {
      await ref.read(safetyPoliciesProvider.notifier).persistToServer(api);
      if (!mounted) return;
      messenger.showSnackBar(
        const SnackBar(content: Text('Safety policies saved to daemon.')),
      );
    } catch (e) {
      if (!mounted) return;
      setState(() => _lastError = 'Save failed: $e');
      messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  Future<void> _rearmFirstFlip() async {
    final api = _api();
    final messenger = ScaffoldMessenger.of(context);
    if (api == null) {
      messenger.showSnackBar(const SnackBar(
          content: Text('No active server — connect to a daemon first.')));
      return;
    }
    try {
      await ref.read(safetyPoliciesProvider.notifier).rearmFirstFlip(api);
      if (!mounted) return;
      messenger.showSnackBar(const SnackBar(
          content: Text('First-flip announce re-armed — the next flip will '
              'alert and wait 60 s.')));
    } catch (e) {
      if (!mounted) return;
      messenger.showSnackBar(SnackBar(content: Text('Re-arm failed: $e')));
    }
  }

  ProfileApi? _api() {
    final server = ref.read(activeServerProvider);
    return server == null ? null : ProfileApi(server);
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(safetyPoliciesProvider);
    final n = ref.read(safetyPoliciesProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
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
          getCanonical: () =>
              ref.read(safetyPoliciesProvider).resumeDelayMin.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setResumeDelayMin(v);
          },
        ),
        const SettingsSectionHeader('Weather thresholds (§35.1)'),
        SettingsSwitchRow(
          label: 'React to weather-station thresholds',
          helpKey: 'safety.policies.weather_triggers',
          value: s.weatherTriggersEnabled,
          hint: 'With a weather station connected, a breached threshold below '
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
            getCanonical: () =>
                ref.read(safetyPoliciesProvider).maxWindKmh.toString(),
            parse: (str) {
              final v = int.tryParse(str);
              if (v != null) n.setMaxWindKmh(v);
            },
          ),
          EditableNumberRow(
            label: 'Max humidity (%)',
            helpKey: 'safety.policies.max_humidity_pct',
            currentValue: s.maxHumidityPct.toString(),
            getCanonical: () =>
                ref.read(safetyPoliciesProvider).maxHumidityPct.toString(),
            parse: (str) {
              final v = int.tryParse(str);
              if (v != null) n.setMaxHumidityPct(v);
            },
          ),
          EditableNumberRow(
            label: 'Min dew delta (°C above dew point)',
            helpKey: 'safety.policies.min_dew_delta_c',
            currentValue: s.minDewDeltaC.toStringAsFixed(1),
            getCanonical: () => ref
                .read(safetyPoliciesProvider)
                .minDewDeltaC
                .toStringAsFixed(1),
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
          label: 'Pause after flip (min)',
          helpKey: 'safety.policies.meridian_pause_min',
          currentValue: s.meridianPauseMin.toString(),
          getCanonical: () =>
              ref.read(safetyPoliciesProvider).meridianPauseMin.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setMeridianPauseMin(v);
          },
        ),
        SettingsSwitchRow(
          label: 'Re-center after flip',
          value: s.meridianRecenter,
          onChanged: n.setMeridianRecenter,
        ),
        SettingsSwitchRow(
          label: 'Re-calibrate guider after flip',
          value: s.meridianRecalGuider,
          onChanged: n.setMeridianRecalGuider,
        ),
        SettingsSwitchRow(
          label: 'Unattended flip safety (§58.9)',
          hint: 'Pre-flip flight check, in-slew watchdog and hard pier-side '
              'verification. Turn off only if this mount misreports pier side.',
          value: s.flipSafetyEnabled,
          onChanged: n.setFlipSafetyEnabled,
        ),
        EditableNumberRow(
          label: 'Expected flip-slew duration (s)',
          currentValue: s.expectedFlipSlewSeconds.toString(),
          getCanonical: () => ref
              .read(safetyPoliciesProvider)
              .expectedFlipSlewSeconds
              .toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setExpectedFlipSlewSeconds(v);
          },
        ),
        SettingsSwitchRow(
          label: 'Louder alerts during dark hours (§58.10)',
          hint: 'While the site sits in astronomical darkness, '
              'equipment-impacting warnings ride one severity level higher so '
              'the alarm behaviour engages earlier for a sleeping user.',
          value: s.unattendedEscalation,
          onChanged: n.setUnattendedEscalation,
        ),
        SettingsSwitchRow(
          label: 'Unattended shutdown after failure (§58.12)',
          hint: 'If a run pauses awaiting your attention and nobody responds '
              'within the wait window, the daemon parks the mount, warms the '
              'cooler and disconnects the equipment. Any command — or simply '
              'opening WILMA — cancels the countdown.',
          value: s.unattendedShutdownEnabled,
          onChanged: n.setUnattendedShutdownEnabled,
        ),
        EditableNumberRow(
          label: 'Unattended shutdown wait (min)',
          currentValue: s.unattendedShutdownWaitMinutes.toString(),
          getCanonical: () => ref
              .read(safetyPoliciesProvider)
              .unattendedShutdownWaitMinutes
              .toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setUnattendedShutdownWaitMinutes(v);
          },
        ),
        // §58.8 — first-flip announce status + manual re-arm. The daemon owns
        // the flag (set after the first announced flip, cleared automatically
        // on an optics change); the panel offers only the one-way re-arm, so
        // there is no way to silently skip the announce.
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Row(children: [
            Expanded(
              child: Text(
                s.firstFlipConfirmed
                    ? 'First-flip announce (§58.8): already ran — later flips '
                        'are silent. Re-arm after re-balancing or a rig change.'
                    : 'First-flip announce (§58.8): armed — the next meridian '
                        'flip alerts and waits 60 s before proceeding.',
                style: Theme.of(context).textTheme.bodyMedium,
              ),
            ),
            const SizedBox(width: 8),
            OutlinedButton(
              key: const Key('rearm_first_flip'),
              // Immediate daemon round-trip (not deferred to Save): the flag
              // is daemon-owned and the general Save deliberately can't touch
              // it, so the button is the only client-side path — one-way.
              onPressed: s.firstFlipConfirmed ? _rearmFirstFlip : null,
              child: const Text('Re-arm'),
            ),
          ]),
        ),
        const SettingsSectionHeader('On altitude limit'),
        SettingsDropdownRow<AltitudeLimitAction>(
          label: 'Action',
          helpKey: 'safety.policies.on_altitude_limit',
          value: s.onAltitudeLimit,
          items: const {
            AltitudeLimitAction.skipTarget: 'Skip + advance to next target',
            AltitudeLimitAction.pauseSequence: 'Pause sequence',
            AltitudeLimitAction.abortSequence: 'Abort sequence',
          },
          onChanged: (v) {
            if (v != null) n.setOnAltitudeLimit(v);
          },
        ),
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
          getCanonical: () =>
              ref.read(safetyPoliciesProvider).guiderRetryTimeoutSec.toString(),
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
        const SettingsSectionHeader('On critically-low disk (§29)'),
        SettingsDropdownRow<DiskSpaceCriticalAction>(
          label: 'Action',
          helpKey: 'safety.policies.on_disk_space_critical',
          value: s.onDiskSpaceCritical,
          items: const {
            DiskSpaceCriticalAction.warn: 'Warn only (diagnostic + notification)',
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
        Row(mainAxisAlignment: MainAxisAlignment.end, children: [
          FilledButton.icon(
            onPressed: _saving ? null : _save,
            icon: _saving
                ? const SizedBox(
                    width: 14,
                    height: 14,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.save, size: 16),
            label: Text(_saving ? 'Saving…' : 'Save'),
          ),
        ]),
      ],
    );
  }
}
