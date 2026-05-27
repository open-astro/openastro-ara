import 'package:flutter/material.dart';

import 'placeholder_tab.dart';

class OptionsTab extends StatelessWidget {
  const OptionsTab({super.key});

  @override
  Widget build(BuildContext context) {
    return const PlaceholderTab(
      title: 'Options',
      icon: Icons.settings,
      description:
          'Tree-based settings (Equipment / Imaging / Plate Solving / Astrometry / '
          'File Saving / Telescope / Astronomy / Sequence / Application). Wired in '
          'Phase 12h with the §61 smart-search registry per §25.5.5.',
    );
  }
}
