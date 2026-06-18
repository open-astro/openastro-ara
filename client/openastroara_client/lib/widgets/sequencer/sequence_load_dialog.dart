import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/sequence_summary.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../theme/ara_colors.dart';

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
          error: (e, _) => const _Message(
              'Couldn\'t load sequences from the server. Check the connection and try again.'),
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
              shrinkWrap: true,
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
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
      ],
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
    final color = switch (s) {
      SequenceRunState.running || SequenceRunState.starting => AraColors.accentBusy,
      SequenceRunState.paused => AraColors.accentInfo,
      SequenceRunState.failed || SequenceRunState.aborting => AraColors.accentError,
      _ => AraColors.textSecondary, // stopped / completed
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
