import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/diagnostics_mode_state.dart';
import '../../../theme/ara_colors.dart';

/// §51 diagnostics mode picker. Phase 12h.2-diagnostics wires the radio
/// selection through `diagnosticsModeProvider`. 12h.2b persists via
/// `/api/v1/profile/diagnostics-mode`.
class DiagnosticsModePanel extends ConsumerWidget {
  const DiagnosticsModePanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final selected = ref.watch(diagnosticsModeProvider);
    final notifier = ref.read(diagnosticsModeProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        _ModeOption(
          mode: DiagnosticsMode.notifyOnly,
          label: 'Notify only (default)',
          description:
              'Diagnostic events surface in the Diagnostic Panel + as WS '
              'notifications. Sequence execution is never auto-paused by '
              'diagnostics alone.',
          selected: selected == DiagnosticsMode.notifyOnly,
          onTap: () => notifier.setMode(DiagnosticsMode.notifyOnly),
        ),
        _ModeOption(
          mode: DiagnosticsMode.pauseOnCritical,
          label: 'Pause on critical',
          description:
              'Critical-severity diagnostic events (sensor temp out of '
              'range, mount drift, etc) auto-pause the current sequence '
              'and ring the §35 alarm.',
          selected: selected == DiagnosticsMode.pauseOnCritical,
          onTap: () => notifier.setMode(DiagnosticsMode.pauseOnCritical),
        ),
        _ModeOption(
          mode: DiagnosticsMode.abortOnCritical,
          label: 'Abort on critical',
          description:
              'Critical-severity events trigger §35 Abort + Park instead '
              'of pause. Use for unattended observatory automation where '
              'you trust the safety policies to recover.',
          selected: selected == DiagnosticsMode.abortOnCritical,
          onTap: () => notifier.setMode(DiagnosticsMode.abortOnCritical),
        ),
      ],
    );
  }
}

class _ModeOption extends StatelessWidget {
  final DiagnosticsMode mode;
  final String label;
  final String description;
  final bool selected;
  final VoidCallback onTap;

  const _ModeOption({
    required this.mode,
    required this.label,
    required this.description,
    required this.selected,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return Semantics(
      label: label,
      selected: selected,
      button: true,
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(6),
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
              Icon(
                selected
                    ? Icons.radio_button_checked
                    : Icons.radio_button_unchecked,
                color:
                    selected ? AraColors.selectionBg : AraColors.textSecondary,
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(label,
                        style: Theme.of(context).textTheme.titleSmall),
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
      ),
    );
  }
}
