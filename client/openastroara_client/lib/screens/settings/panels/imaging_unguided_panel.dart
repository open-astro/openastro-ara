import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/tracking_calibration.dart';
import '../../../state/saved_server_state.dart';
import '../../../state/tracking/tracking_calibration_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../util/friendly_error.dart';
import '../../../widgets/settings/settings_row.dart';

/// #1264 Imaging → Unguided — what the rig's mount can hold without a guider.
/// Read-only: the Tracking Probe measures the ceiling at the target on every
/// unguided run and the Unguided Exposure Guard adapts the subs during it; the
/// ladder and the pass rules are fixed on the daemon (the issue's Advanced
/// knobs wait on a daemon settings section). The one action is Recalibrate:
/// forget the stored ceiling so the next run's ladder starts from scratch.
class ImagingUnguidedPanel extends ConsumerStatefulWidget {
  const ImagingUnguidedPanel({super.key});

  @override
  ConsumerState<ImagingUnguidedPanel> createState() =>
      _ImagingUnguidedPanelState();
}

class _ImagingUnguidedPanelState extends ConsumerState<ImagingUnguidedPanel> {
  bool _busy = false;

  Future<void> _recalibrate() async {
    final messenger = ScaffoldMessenger.of(context);
    setState(() => _busy = true);
    try {
      await ref.read(trackingCalibrationProvider.notifier).recalibrate();
      messenger.showSnackBar(
        const SnackBar(
          content: Text(
            'Tracking ceiling cleared — the next unguided run measures it again.',
          ),
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

  @override
  Widget build(BuildContext context) {
    final connected = ref.watch(activeServerProvider) != null;
    final cal = ref.watch(trackingCalibrationProvider);
    final record = cal.asData?.value;
    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Tracking ceiling'),
        if (!connected)
          const SettingsRow(
            label: 'Unguided ceiling',
            value: 'Connect to your rig to see what its mount can hold.',
            helpKey: 'img.unguided.ceiling',
          )
        else if (cal.isLoading)
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
            SettingsRow(
              label: 'Polar-align residual',
              value: "${record.polarAlignErrorArcmin!.toStringAsFixed(1)}'",
            ),
          if (record.pixelScaleArcsec != null)
            SettingsRow(
              label: 'Pixel scale',
              value: '${record.pixelScaleArcsec!.toStringAsFixed(2)}"/px',
            ),
          if (record.seeingFwhmArcsec != null)
            SettingsRow(
              label: 'Seeing',
              value: '${record.seeingFwhmArcsec!.toStringAsFixed(1)}" FWHM',
            ),
          if (record.rungs.isNotEmpty) ...[
            const SettingsSectionHeader('Ladder'),
            for (final r in record.rungs)
              SettingsRow(
                label: formatExposureSeconds(r.exposureSeconds),
                value: [
                  if (r.eccentricity != null)
                    'ecc ${r.eccentricity!.toStringAsFixed(2)}',
                  if (r.fwhmPx != null)
                    'FWHM ${r.fwhmPx!.toStringAsFixed(1)} px',
                  '${r.stars} stars',
                  !r.usable
                      ? 'too few stars'
                      : r.passed
                      ? 'pass'
                      : 'trailed',
                ].join(' · '),
              ),
          ],
        ],
        const SizedBox(height: 8),
        Align(
          alignment: Alignment.centerLeft,
          child: OutlinedButton.icon(
            onPressed: connected && record != null && !_busy
                ? _recalibrate
                : null,
            icon: const Icon(Icons.refresh, size: 18),
            label: const Text('Recalibrate on the next run'),
          ),
        ),
        const SizedBox(height: 24),
        const SettingsSectionHeader('How the run uses it'),
        const SettingsRow(
          label: 'Tracking probe',
          value:
              'After focus and before the first imaging loop: 10, 30, 60 and 120 s frames at the target, then the '
              'planned sub or twice the prior as a final rung. A rung passes with eccentricity ≤ 0.5 and FWHM within '
              '1.2× the first usable rung; the ceiling is the longest pass, moved halfway toward the first fail — or the '
              'first usable rung itself when even that one trails. Fewer than two usable rungs with no failure keeps the prior.',
          helpKey: 'img.unguided.probe',
        ),
        const SettingsRow(
          label: 'Exposure guard',
          value:
              'Two trailed lights in a row step the exposure down a rung (frames tagged "trailed", one notification); '
              'ten round ones (eccentricity ≤ 0.3) step it back up, never past the sky-limited length.',
          helpKey: 'img.unguided.guard',
        ),
        const SettingsRow(
          label: 'Guided rigs',
          value: 'Nothing changes — with a guider the probe and guard are never added to the run.',
        ),
        Padding(
          padding: const EdgeInsets.only(top: 12),
          child: Text(
            'The ladder and the pass rules are fixed on the rig for now; the mount\'s spec-sheet prior '
            'arrives with the AlpacaBridge mount sheets.',
            style: Theme.of(context).textTheme.labelSmall
                ?.copyWith(color: AraColors.textDisabled),
          ),
        ),
      ],
    );
  }
}
