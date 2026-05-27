import 'package:flutter/material.dart';

import 'placeholder_tab.dart';

class SequencerTab extends StatelessWidget {
  const SequencerTab({super.key});

  @override
  Widget build(BuildContext context) {
    return const PlaceholderTab(
      title: 'Sequencer',
      icon: Icons.list_alt,
      description:
          'Tree-based instruction editor (Areas → Targets → Instructions), '
          'drag-reorder, run/pause/abort. Wired in Phase 12d per §25.5.3.',
    );
  }
}
