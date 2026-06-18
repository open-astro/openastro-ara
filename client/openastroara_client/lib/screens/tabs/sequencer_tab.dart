import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/sequencer/sequence_list_state.dart';
import '../../state/sequencer/sequence_state.dart';
import '../../theme/ara_colors.dart';
import '../../widgets/sequencer/instruction_editor.dart';
import '../../widgets/sequencer/sequence_tree.dart';
import '../../widgets/sequencer/sequencer_toolbar.dart';

/// Sequencer tab per playbook §25.5.3: toolbar + tree view + selected-node
/// editor pane. Selecting a sequence in the Load picker loads its body into the
/// editor tree (via [_loadSelectedBody]); Run/Pause/Abort live on the toolbar.
class SequencerTab extends ConsumerWidget {
  const SequencerTab({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    // When the picked sequence changes, fetch + parse its body into the tree.
    ref.listen<String?>(selectedSequenceIdProvider, (prev, next) {
      if (next != null && next != prev) _loadSelectedBody(ref, next);
    });

    return Column(
      children: [
        const SequencerToolbar(),
        Expanded(
          child: Row(
            children: [
              Expanded(
                flex: 3,
                child: Container(
                  decoration: const BoxDecoration(
                    color: AraColors.bgPrimary,
                    border: Border(
                        right: BorderSide(color: AraColors.border)),
                  ),
                  child: const SequenceTree(),
                ),
              ),
              const Expanded(flex: 2, child: InstructionEditor()),
            ],
          ),
        ),
      ],
    );
  }
}

/// Fetch the selected sequence's body and load the parsed tree into the editor.
/// Best-effort: a transport/parse failure is logged and leaves the current tree
/// in place rather than throwing.
Future<void> _loadSelectedBody(WidgetRef ref, String id) async {
  final api = ref.read(sequenceApiProvider);
  if (api == null) return;
  try {
    final root = await api.getSequence(id);
    if (!ref.context.mounted) return;
    ref.read(sequenceControllerProvider.notifier).load(root);
  } catch (e) {
    debugPrint('[sequencer] failed to load sequence body for $id: $e');
  }
}
