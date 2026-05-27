import 'package:flutter/material.dart';

import '../../theme/ara_colors.dart';
import '../../widgets/sequencer/instruction_editor.dart';
import '../../widgets/sequencer/sequence_tree.dart';
import '../../widgets/sequencer/sequencer_toolbar.dart';

/// Sequencer tab per playbook §25.5.3. Phase 12d.1 wires the layout
/// (toolbar + tree view + selected-node editor pane) over the in-memory
/// demo sequence in `SequenceController._demoSequence()`. Phase 12d.2
/// connects to `/api/v1/sequences` for Load / Save / Run. Phase 12d.3 adds
/// NINA import + conditional/loop UI + template instantiation.
class SequencerTab extends StatelessWidget {
  const SequencerTab({super.key});

  @override
  Widget build(BuildContext context) {
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
