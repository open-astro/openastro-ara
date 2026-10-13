import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/tracking_calibration.dart';
import '../../../services/profile_api.dart';
import '../../../state/equipment/mount_state.dart';
import '../../../state/saved_server_state.dart';
import '../../../state/settings/panel_save_registry.dart';
import '../../../state/settings/unguided_settings_state.dart';
import '../../../state/tracking/tracking_calibration_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../util/friendly_error.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// #1264 Imaging → Unguided — what the rig's mount can hold without a guider,
/// and the knobs behind the Tracking Probe, the Unguided Exposure Guard and
/// Center After Drift (profile section `unguided`, hydrated on mount, saved
/// on Save like every other profile panel). The measured ceiling and the
/// mount's spec sheet are read-only facts; Recalibrate forgets the ceiling so
/// the next run's ladder starts from scratch.
class ImagingUnguidedPanel extends ConsumerStatefulWidget {
  const ImagingUnguidedPanel({super.key});

  @override
  ConsumerState<ImagingUnguidedPanel> createState() =>
      _ImagingUnguidedPanelState();
}

class _ImagingUnguidedPanelState extends ConsumerState<ImagingUnguidedPanel>
    with PanelSaveRegistration {
  bool _busy = false;
  String? _lastError;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _hydrate());
  }

  ProfileApi? _api() {
    final server = ref.read(activeServerProvider);
    return server == null ? null : ProfileApi(server);
  }

  Future<void> _hydrate() async {
    final api = _api();
    if (api == null) return;
    try {
      await ref.read(unguidedSettingsProvider.notifier).hydrateFromServer(api);
    } catch (e) {
      if (mounted) {
        setState(() => _lastError = friendlyError(e, action: 'load your saved settings'));
      }
    }
  }

  @override
  Future<void> panelSave() => _save();

  Future<void> _save() async {
    setState(() => _lastError = null);
    final api = _api();
    final messenger = ScaffoldMessenger.of(context);
    if (api == null) {
      setState(() => _lastError = 'Not connected — connect to your rig to save this.');
      messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
      return;
    }
    try {
      await ref.read(unguidedSettingsProvider.notifier).persistToServer(api);
      if (!mounted) return;
      messenger.showSnackBar(const SnackBar(content: Text('Saved.')));
    } catch (e) {
      if (!mounted) return;
      setState(() => _lastError = friendlyError(e, action: 'save that'));
      messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
    }
  }

  Future<void> _recalibrate() async {
    final messenger = ScaffoldMessenger.of(context);
    setState(() => _busy = true);
    try {
      await ref.read(trackingCalibrationProvider.notifier).recalibrate();
      messenger.showSnackBar(
        const SnackBar(
          content: Text('Tracking ceiling cleared — the next unguided run measures it again.'),
        ),
      );
    } catch (e) {
      messenger.showSnackBar(
        SnackBar(content: Text(friendlyError(e, action: 'clear the ceiling'))),
      );
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  static String _num(double v) =>
      v == v.roundToDouble() ? v.toStringAsFixed(0) : v.toStringAsFixed(2);

  static const _basisLabels = {
    'max': 'Larger of one pixel or half the seeing',
    'pixel': 'One pixel',
    'seeing': 'Half the seeing disc',
  };

  @override
  Widget build(BuildContext context) {
    final connected = ref.watch(activeServerProvider) != null;
    final cal = ref.watch(trackingCalibrationProvider);
    final record = cal.asData?.value;
    final mount = ref.watch(mountProvider);
    final u = ref.watch(unguidedSettingsProvider);
    final n = ref.read(unguidedSettingsProvider.notifier);
    final dim = Theme.of(context).textTheme.labelSmall?.copyWith(color: AraColors.textDisabled);
    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        if (_lastError != null)
          Padding(
            padding: const EdgeInsets.only(bottom: 12),
            child: Text(_lastError!, style: const TextStyle(color: AraColors.accentError)),
          ),
        const SettingsSectionHeader('Tracking ceiling'),
        if (!connected)
          const SettingsRow(
            label: 'Unguided ceiling',
            value: 'Connect to your rig to see what its mount can hold.',
            helpKey: 'img.unguided.ceiling',
          )
        else if (cal.isLoading && !cal.hasValue)
          const SettingsRow(label: 'Unguided ceiling', value: 'Reading…')
        else if (record == null)
          const SettingsRow(
            label: 'Unguided ceiling',
            value: 'Not measured yet — the first run without a guider measures it at the target.',
            helpKey: 'img.unguided.ceiling',
          )
        else ...[
          SettingsRow(
            label: 'Unguided ceiling',
            value:
                '${formatExposureSeconds(record.ceilingSeconds)} · '
                '${record.measured ? 'measured' : 'prior kept'} on ${formatCalibrationDate(record.measuredUtc)}',
            helpKey: 'img.unguided.ceiling',
          ),
          if (record.priorSeconds != null)
            SettingsRow(
              label: 'Prior going in',
              value: formatExposureSeconds(record.priorSeconds!),
              hint: 'The spec sheet or the previous night',
            ),
          if (record.polarAlignErrorArcmin != null)
            SettingsRow(label: 'Polar-align residual', value: "${record.polarAlignErrorArcmin!.toStringAsFixed(1)}'"),
          if (record.pixelScaleArcsec != null)
            SettingsRow(label: 'Pixel scale', value: '${record.pixelScaleArcsec!.toStringAsFixed(2)}"/px'),
          if (record.seeingFwhmArcsec != null)
            SettingsRow(label: 'Seeing', value: '${record.seeingFwhmArcsec!.toStringAsFixed(1)}" FWHM'),
          if (record.rungs.isNotEmpty) ...[
            const SettingsSectionHeader('Ladder'),
            for (final r in record.rungs)
              SettingsRow(
                label: formatExposureSeconds(r.exposureSeconds),
                value: [
                  if (r.eccentricity != null) 'ecc ${r.eccentricity!.toStringAsFixed(2)}',
                  if (r.fwhmPx != null) 'FWHM ${r.fwhmPx!.toStringAsFixed(1)} px',
                  '${r.stars} stars',
                  !r.usable ? 'too few stars' : r.passed ? 'pass' : 'trailed',
                ].join(' · '),
              ),
          ],
        ],
        // #1264 — the mount's spec sheet (AlpacaBridge #815): the prior the
        // first unguided run starts from when nothing is measured yet.
        if (connected)
          SettingsRow(
            label: 'Mount spec sheet',
            value: switch (mount) {
              AsyncData(value: final m) when m == null =>
                'Mount not connected.',
              AsyncData(value: final m) => switch (m!.capabilities?.specs) {
                null => 'No spec sheet for this mount — the first unguided run measures the ceiling from the ladder.',
                final sheet =>
                  '${sheet.model ?? 'This mount'}: ${sheet.trackingFact}${sheet.hasPec ? ' · PEC available' : ''}',
              },
              AsyncError() => 'Couldn\'t read the mount — check its connection.',
              _ => 'Reading the mount…',
            },
            helpKey: 'img.unguided.ceiling',
          ),
        const SizedBox(height: 8),
        Align(
          alignment: Alignment.centerLeft,
          child: OutlinedButton.icon(
            onPressed: connected && record != null && !_busy ? _recalibrate : null,
            icon: const Icon(Icons.refresh, size: 18),
            label: const Text('Recalibrate on the next run'),
          ),
        ),
        const SizedBox(height: 24),
        const SettingsSectionHeader('Exposure guard'),
        SettingsSwitchRow(
          label: 'Unguided exposure guard',
          helpKey: 'img.unguided.guard_enabled',
          value: u.guardEnabled,
          onChanged: n.setGuardEnabled,
          hint: u.guardEnabled
              ? 'Trailed lights step the exposure down a rung; round ones step it back up'
              : 'Off: the probe\'s ceiling holds for the whole loop',
        ),
        EditableNumberRow(
          label: 'Trailed above eccentricity',
          helpKey: 'img.unguided.eccentricity',
          currentValue: _num(u.eccentricityTolerance),
          getCanonical: () => _num(ref.read(unguidedSettingsProvider).eccentricityTolerance),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) n.setEccentricityTolerance(v);
          },
        ),
        EditableNumberRow(
          label: 'Round at or under eccentricity',
          helpKey: 'img.unguided.eccentricity',
          hint: 'Kept below the trailed bar; follows it down when you lower that',
          currentValue: _num(u.goodEccentricity),
          getCanonical: () => _num(ref.read(unguidedSettingsProvider).goodEccentricity),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) n.setGoodEccentricity(v);
          },
        ),
        EditableNumberRow(
          label: 'Step down after N trailed lights',
          helpKey: 'img.unguided.guard_enabled',
          currentValue: u.stepDownAfter.toString(),
          getCanonical: () => ref.read(unguidedSettingsProvider).stepDownAfter.toString(),
          parse: (s) {
            final v = int.tryParse(s);
            if (v != null) n.setStepDownAfter(v);
          },
        ),
        EditableNumberRow(
          label: 'Step up after N round lights',
          helpKey: 'img.unguided.guard_enabled',
          currentValue: u.stepUpAfter.toString(),
          getCanonical: () => ref.read(unguidedSettingsProvider).stepUpAfter.toString(),
          parse: (s) {
            final v = int.tryParse(s);
            if (v != null) n.setStepUpAfter(v);
          },
        ),
        const SettingsSectionHeader('Tracking probe'),
        EditableTextRow(
          label: 'Probe ladder (s, shortest first)',
          helpKey: 'img.unguided.probe_rungs',
          currentValue: u.probeRungsText,
          getCanonical: () => ref.read(unguidedSettingsProvider).probeRungsText,
          parse: (s) {
            final rungs = UnguidedSettings.parseRungs(s);
            if (rungs != null) n.setProbeRungs(rungs);
          },
        ),
        SettingsDropdownRow<String>(
          label: 'Spec-sheet prior tolerance',
          helpKey: 'img.unguided.prior_tolerance',
          value: u.priorToleranceBasis,
          items: _basisLabels,
          onChanged: (v) {
            if (v != null) n.setPriorToleranceBasis(v);
          },
        ),
        // #1264 — a single-figure basis gives no prior until that figure exists.
        if (u.priorToleranceBasis != 'max')
          Padding(
            padding: const EdgeInsets.only(left: 4, bottom: 4),
            child: Text(
              u.priorToleranceBasis == 'pixel'
                  ? 'Needs the optics (focal length and pixel size) set; without them the spec sheet gives no prior.'
                  : 'Needs the site\'s typical seeing set; without it the spec sheet gives no prior.',
              style: dim,
            ),
          ),
        const SettingsSectionHeader('Re-centre after drift'),
        EditableNumberRow(
          label: 'Re-centre past drift (arcmin)',
          helpKey: 'img.unguided.recentre',
          currentValue: _num(u.recentreDriftArcmin),
          getCanonical: () => _num(ref.read(unguidedSettingsProvider).recentreDriftArcmin),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) n.setRecentreDriftArcmin(v);
          },
        ),
        EditableNumberRow(
          label: 'Solve every N lights',
          helpKey: 'img.unguided.recentre',
          currentValue: u.recentreSolveEveryN.toString(),
          getCanonical: () => ref.read(unguidedSettingsProvider).recentreSolveEveryN.toString(),
          parse: (s) {
            final v = int.tryParse(s);
            if (v != null) n.setRecentreSolveEveryN(v);
          },
        ),
        Padding(
          padding: const EdgeInsets.only(top: 12),
          child: Text(
            'Guided rigs are untouched: with a guider none of these nodes are added to the run. The trailed bar '
            'is shared by the probe\'s pass rule and the guard. The probe\'s FWHM rule (within 1.2× the first usable '
            'rung) and the 2× prior cap stay fixed. A ladder rung above the planned sub is simply trimmed by the cap.',
            style: dim,
          ),
        ),
      ],
    );
  }
}
