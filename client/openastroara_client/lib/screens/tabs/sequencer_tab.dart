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
class SequencerTab extends ConsumerStatefulWidget {
  const SequencerTab({super.key});

  @override
  ConsumerState<SequencerTab> createState() => _SequencerTabState();
}

class _SequencerTabState extends ConsumerState<SequencerTab> {
  // The sequence id whose body is currently loaded into the tree — guards
  // against re-fetching the same sequence on rebuilds and lets us detect an
  // already-selected sequence on (re)mount.
  String? _loadedId;

  @override
  void initState() {
    super.initState();
    // ref.listen only fires on *changes*, so an already-selected sequence (e.g.
    // the tab was navigated away from and back) wouldn't load. Pick it up here.
    final id = ref.read(selectedSequenceIdProvider);
    if (id != null) _load(id);
  }

  void _load(String id) {
    _loadedId = id;
    _loadSelectedBody(ref, id);
  }

  @override
  Widget build(BuildContext context) {
    // When the picked sequence changes, fetch + parse its body into the tree.
    // Deselecting (next == null) intentionally leaves the current tree in place
    // — this slice is load-only; clearing the editor is a later slice.
    ref.listen<String?>(selectedSequenceIdProvider, (prev, next) {
      if (next != null && next != _loadedId) _load(next);
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
/// Best-effort: a transport/parse failure leaves the current tree in place and
/// surfaces a SnackBar rather than throwing.
Future<void> _loadSelectedBody(WidgetRef ref, String id) async {
  final api = ref.read(sequenceApiProvider);
  if (api == null) return;
  try {
    final root = await api.getSequence(id);
    if (!ref.context.mounted) return;
    // Drop a stale response: a newer selection was made while this was in flight,
    // so loading now would clobber the tree with the wrong (older) sequence.
    if (ref.read(selectedSequenceIdProvider) != id) return;
    ref.read(sequenceControllerProvider.notifier).load(root);
  } catch (e) {
    debugPrint('[sequencer] failed to load sequence body for $id: $e');
    if (!ref.context.mounted) return;
    ScaffoldMessenger.maybeOf(ref.context)?.showSnackBar(const SnackBar(
      content: Text(
          "Couldn't load the sequence. Check the connection and try again."),
      backgroundColor: AraColors.accentError,
    ));
  }
}
