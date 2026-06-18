import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/sequence_summary.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../theme/ara_colors.dart';
import 'sequence_import.dart';

/// §38 "Load sequence" picker. Lists the active server's saved sequences
/// (newest-first) from [sequenceListProvider]; tapping one records it as the
/// [selectedSequenceIdProvider] and pops. Handles loading / error / no-server /
/// empty states. The actual body→tree population is a later slice — this slice
/// only selects which sequence is loaded.
class SequenceLoadDialog extends ConsumerWidget {
  const SequenceLoadDialog({super.key});

  /// Show the dialog. Returns the selected sequence id, or null if dismissed.
  static Future<String?> show(BuildContext context) =>
      showDialog<String>(context: context, builder: (_) => const SequenceLoadDialog());

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(sequenceListProvider);
    return AlertDialog(
      title: const Text('Load sequence'),
      content: SizedBox(
        width: 420,
        child: async.when(
          loading: () => const SizedBox(
            height: 120,
            child: Center(child: CircularProgressIndicator()),
          ),
          error: (e, st) {
            // Keep the raw error in the logs (diagnosable in dev); show the user
            // a clean, actionable message.
            debugPrint('[sequencer] sequence list load failed: $e\n$st');
            return const _Message(
                'Couldn\'t load sequences from the server. Check the connection and try again.');
          },
          data: (list) {
            if (list == null) {
              return const _Message('Connect to a daemon to load saved sequences.');
            }
            if (list.isEmpty) {
              return const _Message('No saved sequences yet.');
            }
            // Cap the height so a server with dozens of sequences can't grow the
            // dialog past the screen; the list scrolls within the cap.
            return ConstrainedBox(
              constraints: const BoxConstraints(maxHeight: 360),
              child: ListView.builder(
              // No shrinkWrap: the ConstrainedBox bounds the viewport, so the
              // builder can lazily build only visible rows.
              itemCount: list.length,
              itemBuilder: (context, i) {
                final s = list[i];
                return ListTile(
                  contentPadding: EdgeInsets.zero,
                  title: Text(s.name.isEmpty ? '(untitled)' : s.name),
                  subtitle: Text(
                    '${s.instructionCount} instruction(s) · ${s.targetCount} target(s)',
                    style: const TextStyle(color: AraColors.textSecondary),
                  ),
                  trailing: _RunStateBadge(s.currentRunState),
                  onTap: () {
                    ref.read(selectedSequenceIdProvider.notifier).select(s.id);
                    Navigator.of(context).pop(s.id);
                  },
                );
              },
              ),
            );
          },
        ),
      ),
      actions: [
        // Import a NINA sequence file; on success it selects the new sequence
        // (loaded into the tree) and closes this picker. On a cancel/error the
        // dialog stays open so the user can retry or pick from the list.
        const _ImportNinaButton(),
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
      ],
    );
  }
}

/// The "Import NINA…" action. Stateful so it can disable itself for the whole
/// async flow (picker → upload → warnings dialog) — without the busy guard a
/// second tap on a slow connection would open a second picker and fire a second
/// concurrent import. On a successful import it pops the Load dialog.
class _ImportNinaButton extends ConsumerStatefulWidget {
  const _ImportNinaButton();

  @override
  ConsumerState<_ImportNinaButton> createState() => _ImportNinaButtonState();
}

class _ImportNinaButtonState extends ConsumerState<_ImportNinaButton> {
  bool _busy = false;

  Future<void> _run() async {
    final navigator = Navigator.of(context);
    setState(() => _busy = true);
    var popped = false;
    try {
      final imported = await pickAndImportSequence(context, ref);
      // Gate on this State's `mounted`, not navigator.mounted: the dialog is
      // barrierDismissible, so the user can tap it away mid-import. Once the
      // dialog route is gone this State is disposed (mounted == false), but the
      // app-level navigator is still alive — popping it here would pop the wrong
      // route (the tab beneath). `mounted` false ⇒ skip the pop.
      if (imported && mounted) {
        navigator.pop();
        popped = true;
      }
    } finally {
      // Only clear the busy flag when we kept the dialog open; if we popped, the
      // dialog is gone and there's no state to reset (explicit flag rather than
      // relying on `mounted` flipping as a side-effect of pop()).
      if (!popped && mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return TextButton.icon(
      icon: _busy
          ? const SizedBox(
              width: 18,
              height: 18,
              child: CircularProgressIndicator(strokeWidth: 2),
            )
          : const Icon(Icons.upload_file, size: 18),
      label: const Text('Import NINA…'),
      onPressed: _busy ? null : _run,
    );
  }
}

/// Small coloured chip for a sequence's current run state; nothing for idle/none.
class _RunStateBadge extends StatelessWidget {
  const _RunStateBadge(this.state);
  final SequenceRunState? state;

  @override
  Widget build(BuildContext context) {
    final s = state;
    if (s == null || s == SequenceRunState.idle) return const SizedBox.shrink();
    // Exhaustive (no wildcard) so a new SequenceRunState forces a compile error
    // here rather than silently defaulting — matching SequenceRunState.isActive.
    final color = switch (s) {
      SequenceRunState.running || SequenceRunState.starting => AraColors.accentBusy,
      SequenceRunState.paused => AraColors.accentInfo,
      SequenceRunState.failed || SequenceRunState.aborting => AraColors.accentError,
      SequenceRunState.stopped || SequenceRunState.completed => AraColors.textSecondary,
      SequenceRunState.idle => AraColors.textSecondary, // unreachable (early-returned above)
    };
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.18),
        borderRadius: BorderRadius.circular(4),
        border: Border.all(color: color),
      ),
      child: Text(s.name,
          style: TextStyle(color: color, fontSize: 11, fontWeight: FontWeight.w600)),
    );
  }
}

class _Message extends StatelessWidget {
  const _Message(this.text);
  final String text;
  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 24),
        child: Text(text, style: const TextStyle(color: AraColors.textSecondary)),
      );
}
