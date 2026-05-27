import 'package:flutter/material.dart';

import '../../../theme/ara_colors.dart';

/// §51 diagnostics mode picker. Phase 12h.1 renders the radio group +
/// per-mode description. 12h.2 wires the selection to the active profile
/// + persists via `/api/v1/profile/diagnostics-mode`.
class DiagnosticsModePanel extends StatelessWidget {
  const DiagnosticsModePanel({super.key});

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.all(24),
      children: const [
        _ModeOption(
          mode: 'notify_only',
          label: 'Notify only (default)',
          description:
              'Diagnostic events surface in the Diagnostic Panel + as WS '
              'notifications. Sequence execution is never auto-paused by '
              'diagnostics alone.',
          selected: true,
        ),
        _ModeOption(
          mode: 'pause_on_critical',
          label: 'Pause on critical',
          description:
              'Critical-severity diagnostic events (sensor temp out of '
              'range, mount drift, etc) auto-pause the current sequence '
              'and ring the §35 alarm.',
        ),
        _ModeOption(
          mode: 'abort_on_critical',
          label: 'Abort on critical',
          description:
              'Critical-severity events trigger §35 Abort + Park instead '
              'of pause. Use for unattended observatory automation where '
              'you trust the safety policies to recover.',
        ),
      ],
    );
  }
}

class _ModeOption extends StatelessWidget {
  final String mode;
  final String label;
  final String description;
  final bool selected;

  const _ModeOption({
    required this.mode,
    required this.label,
    required this.description,
    this.selected = false,
  });

  @override
  Widget build(BuildContext context) {
    return Semantics(
      // Expose selected state + label to assistive tech so screen readers
      // announce which mode is active (the Icon-based visual indicator
      // alone isn't audible).
      label: label,
      selected: selected,
      button: true,
      child: Container(
      margin: const EdgeInsets.symmetric(vertical: 8),
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: AraColors.bgPanel,
        border: Border.all(
          color: selected ? AraColors.selectionBg : AraColors.border,
          width: selected ? 2 : 1,
        ),
        borderRadius: BorderRadius.circular(6),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          // Visual-only radio dot — real selection wiring (via RadioGroup
          // per Flutter 3.32+) lands in Phase 12h.2 alongside the persist
          // call to /api/v1/profile/diagnostics-mode.
          Icon(
            selected
                ? Icons.radio_button_checked
                : Icons.radio_button_unchecked,
            color: selected ? AraColors.selectionBg : AraColors.textSecondary,
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(label, style: Theme.of(context).textTheme.titleSmall),
                const SizedBox(height: 4),
                Text(
                  description,
                  style: Theme.of(context).textTheme.bodySmall?.copyWith(
                        color: AraColors.textSecondary,
                      ),
                ),
              ],
            ),
          ),
        ],
      ),
    ),
    );
  }
}
