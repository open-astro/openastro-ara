import 'package:flutter/material.dart';

import '../../models/preflight_checklist.dart';
import '../../theme/ara_colors.dart';
import '../help_icon.dart';

/// §58.14 — the pre-sleep checklist as the spec draws it: graded lines, the
/// meridian crossings tonight, the unattended hours and the alarm device.
/// Stateless so the bedtime dialog and the Setup pane share one rendering.
class PreflightChecklistCard extends StatelessWidget {
  final PreflightChecklist checklist;
  const PreflightChecklistCard({super.key, required this.checklist});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final dim = theme.textTheme.bodySmall?.copyWith(
      color: AraColors.textSecondary,
    );
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: [
        Row(
          children: [
            Expanded(
              child: Text(
                "Tonight's sequence — pre-flight check",
                style: theme.textTheme.titleMedium,
              ),
            ),
            const HelpIcon(helpKey: 'setup.presleep'),
          ],
        ),
        const SizedBox(height: 8),
        for (final item in checklist.items)
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 3),
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                _StatusGlyph(status: item.status),
                const SizedBox(width: 8),
                Expanded(
                  child: Text.rich(
                    TextSpan(
                      text: '${item.title}: ',
                      style: theme.textTheme.bodyMedium?.copyWith(
                        fontWeight: FontWeight.w600,
                      ),
                      children: [
                        TextSpan(
                          text: item.detail,
                          style: theme.textTheme.bodyMedium,
                        ),
                      ],
                    ),
                  ),
                ),
              ],
            ),
          ),
        const SizedBox(height: 10),
        Text(
          'Planned meridian flips tonight:',
          style: theme.textTheme.bodyMedium,
        ),
        if (checklist.flips.isEmpty)
          Padding(
            padding: const EdgeInsets.only(left: 12, top: 2),
            child: Text(
              checklist.unforecastTargets > 0
                  ? 'None for the active target before dawn.'
                  : 'None forecast — nothing is running, or the active target has already crossed.',
              style: dim,
            ),
          ),
        for (final f in checklist.flips)
          Padding(
            padding: const EdgeInsets.only(left: 12, top: 2),
            child: Text(
              '• ${_hhmm(f.atUtc.toLocal())} — ${f.target} (alt ${f.altitudeDeg.round()}° at flip, ${_standing(f.standing)})',
              style: theme.textTheme.bodyMedium?.copyWith(
                color: switch (f.standing) {
                  'below_floor' => AraColors.accentError,
                  'soft_warning' => AraColors.accentBusy,
                  _ => null,
                },
              ),
            ),
          ),
        if (checklist.unforecastTargets > 0)
          Padding(
            padding: const EdgeInsets.only(left: 12, top: 2),
            child: Text(
              checklist.unforecastTargets == 1
                  ? '1 more target in the sequence is not forecast (only the active target is).'
                  : '${checklist.unforecastTargets} more targets in the sequence are not forecast (only the active target is).',
              style: dim,
            ),
          ),
        const SizedBox(height: 10),
        Text(checklist.unattendedLine, style: theme.textTheme.bodyMedium),
        Text(
          checklist.alarmLine,
          style: theme.textTheme.bodyMedium?.copyWith(
            color: checklist.alarmDevice != null && !checklist.alarmSoundEnabled
                ? AraColors.accentBusy
                : null,
          ),
        ),
        const SizedBox(height: 6),
        Text(
          'Checked at ${_hhmm(checklist.generatedUtc.toLocal())}. The solver is checked for presence, not test-solved; the slew rate is not verified.',
          style: dim,
        ),
      ],
    );
  }

  static String _hhmm(DateTime t) =>
      '${t.hour.toString().padLeft(2, '0')}:${t.minute.toString().padLeft(2, '0')}';

  static String _standing(String s) => switch (s) {
    'below_floor' => 'BELOW the hard floor — the flip will be refused',
    'soft_warning' => 'below the soft warning, above the hard floor',
    _ => 'OK',
  };
}

class _StatusGlyph extends StatelessWidget {
  final String status;
  const _StatusGlyph({required this.status});

  @override
  Widget build(BuildContext context) => switch (status) {
    'ok' => const Icon(
      Icons.check_circle,
      size: 18,
      color: AraColors.accentConnected,
    ),
    'warn' => const Icon(
      Icons.warning_amber_rounded,
      size: 18,
      color: AraColors.accentBusy,
    ),
    'fail' => const Icon(
      Icons.error_outline,
      size: 18,
      color: AraColors.accentError,
    ),
    _ => const Icon(
      Icons.help_outline,
      size: 18,
      color: AraColors.textDisabled,
    ),
  };
}

/// The bedtime dialog: the card plus the two ways out the spec names.
class PreflightChecklistDialog extends StatelessWidget {
  final PreflightChecklist checklist;
  final VoidCallback onAllGood;
  final VoidCallback onAdjust;
  const PreflightChecklistDialog({
    super.key,
    required this.checklist,
    required this.onAllGood,
    required this.onAdjust,
  });

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: Row(
        children: [
          Icon(
            checklist.hasFailure
                ? Icons.report_problem_outlined
                : Icons.bedtime_outlined,
            color: checklist.hasFailure
                ? AraColors.accentError
                : AraColors.accentInfo,
          ),
          const SizedBox(width: 8),
          const Expanded(child: Text('Before you sleep')),
        ],
      ),
      content: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 520),
        child: SingleChildScrollView(
          child: PreflightChecklistCard(checklist: checklist),
        ),
      ),
      actions: [
        TextButton(onPressed: onAdjust, child: const Text('Adjust')),
        FilledButton(
          onPressed: onAllGood,
          child: Text(
            checklist.hasFailure
                ? 'I know — let me go to bed'
                : 'All good — let me go to bed',
          ),
        ),
      ],
    );
  }
}
