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

  ProfileApi? _api() {
    final servers = ref.read(savedServersProvider).maybeWhen(
          data: (list) => list,
          orElse: () => const [],
        );
    if (servers.isEmpty) return null;
    // Most-recently-saved server is the de-facto active one — same
    // convention as §52.2 Alpaca chooser + §54 help dialog.
    return ProfileApi(servers.last);
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
