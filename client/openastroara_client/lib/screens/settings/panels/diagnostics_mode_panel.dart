import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../services/profile_api.dart';
import '../../../state/saved_server_state.dart';
import '../../../state/settings/diagnostics_mode_state.dart';
import '../../../theme/ara_colors.dart';

/// §51 diagnostics mode picker. Phase 12h.6j added the daemon round-
/// trip. Unlike the other settings panels (which use a Save button),
/// this picker auto-saves on each radio-button tap — the UX expectation
/// for single-choice radios is "I tapped it, it's set." A failed PUT
/// shows a snackbar; local state still reflects the user's choice.
class DiagnosticsModePanel extends ConsumerStatefulWidget {
  const DiagnosticsModePanel({super.key});

  @override
  ConsumerState<DiagnosticsModePanel> createState() =>
      _DiagnosticsModePanelState();
}

class _DiagnosticsModePanelState extends ConsumerState<DiagnosticsModePanel> {
  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _hydrate());
  }

  Future<void> _hydrate() async {
    final api = _api();
    if (api == null) return;
    try {
      await ref.read(diagnosticsModeProvider.notifier).hydrateFromServer(api);
    } catch (_) {
      // Hydration failures are silent — the user can still pick + save.
    }
  }

  Future<void> _selectAndSave(DiagnosticsMode mode) async {
    ref.read(diagnosticsModeProvider.notifier).setMode(mode);
    final api = _api();
    final messenger = ScaffoldMessenger.of(context);
    if (api == null) {
      messenger.showSnackBar(const SnackBar(
        content: Text('No active server — selection is local-only.'),
      ));
      return;
    }
    try {
      await ref.read(diagnosticsModeProvider.notifier).persistToServer(api);
    } catch (e) {
      if (!mounted) return;
      messenger.showSnackBar(SnackBar(content: Text('Save failed: $e')));
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
    final selected = ref.watch(diagnosticsModeProvider);
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
          onTap: () => _selectAndSave(DiagnosticsMode.notifyOnly),
        ),
        _ModeOption(
          mode: DiagnosticsMode.pauseOnCritical,
          label: 'Pause on critical',
          description:
              'Critical-severity diagnostic events (sensor temp out of '
              'range, mount drift, etc) auto-pause the current sequence '
              'and ring the §35 alarm.',
          selected: selected == DiagnosticsMode.pauseOnCritical,
          onTap: () => _selectAndSave(DiagnosticsMode.pauseOnCritical),
        ),
        _ModeOption(
          mode: DiagnosticsMode.abortOnCritical,
          label: 'Abort on critical',
          description:
              'Critical-severity events trigger §35 Abort + Park instead '
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
