import 'package:flutter/material.dart';
import '../../../util/friendly_error.dart';
import '../../../l10n/l10n.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/diagnostics_mode_state.dart';
import '../../../state/settings/loaded_section.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/settings/section_panel.dart';

/// §51 diagnostics mode picker. Phase 12h.6j added the daemon round-
/// trip. Unlike the other settings panels (which use a Save button),
/// this picker auto-saves on each radio-button tap — the UX expectation
/// for single-choice radios is "I tapped it, it's set." A failed PUT
/// shows a snackbar and the radio goes back to the rig's value; a 412
/// keeps the choice under the conflict banner (ADR 0015 rule 6).
class DiagnosticsModePanel extends ConsumerStatefulWidget {
  const DiagnosticsModePanel({super.key});

  @override
  ConsumerState<DiagnosticsModePanel> createState() =>
      _DiagnosticsModePanelState();
}

class _DiagnosticsModePanelState extends ConsumerState<DiagnosticsModePanel>
    with SectionPanel {
  @override
  List<PanelSection> get sections => [diagnosticsModeProvider];

  Future<void> _selectAndSave(DiagnosticsMode mode) async {
    final messenger = ScaffoldMessenger.of(context);
    final l10n = context.l10n;
    try {
      await ref.read(diagnosticsModeProvider.notifier).replaceAndSave(mode);
    } catch (e) {
      messenger.showSnackBar(
        SnackBar(
          content: Text(
            e is SectionNotLoadedException || e is SectionConflictException
                ? e.toString()
                : friendlyError(l10n, e, action: l10n.actionSave),
          ),
        ),
      );
    }
  }

  @override
  Widget build(BuildContext context) {
    final gate = sectionGate();
    if (gate != null) return gate;
    final selected = ref.watch(diagnosticsModeProvider).value!;
    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        ?sectionConflict(),
        _ModeOption(
          mode: DiagnosticsMode.notifyOnly,
          label: 'Notify only (default)',
          description:
              'Diagnostic events surface in the Diagnostic Panel + as WS '
              'notifications. Sequence execution is never auto-paused by '
              'diagnostics alone.',
          selected: selected == DiagnosticsMode.notifyOnly,
          onTap: () => _selectAndSave(DiagnosticsMode.notifyOnly),
        ),
        _ModeOption(
          mode: DiagnosticsMode.pauseOnCritical,
          label: 'Pause on critical',
          description:
              'Critical-severity diagnostic events (sensor temp out of '
              'range, mount drift, etc) auto-pause the current sequence '
              'and ring the alarm.',
          selected: selected == DiagnosticsMode.pauseOnCritical,
          onTap: () => _selectAndSave(DiagnosticsMode.pauseOnCritical),
        ),
        _ModeOption(
          mode: DiagnosticsMode.abortOnCritical,
          label: 'Abort on critical',
          description:
              'Critical-severity events trigger Abort + Park instead '
              'of pause. Use for unattended observatory automation where '
              'you trust the safety policies to recover.',
          selected: selected == DiagnosticsMode.abortOnCritical,
          onTap: () => _selectAndSave(DiagnosticsMode.abortOnCritical),
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
      checked: selected,
      inMutuallyExclusiveGroup: true,
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
                color: selected
                    ? AraColors.selectionBg
                    : AraColors.textSecondary,
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
      ),
    );
  }
}
