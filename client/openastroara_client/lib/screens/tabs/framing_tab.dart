import 'package:flutter/material.dart';

import 'placeholder_tab.dart';

class FramingTab extends StatelessWidget {
  const FramingTab({super.key});

  @override
  Widget build(BuildContext context) {
    return const PlaceholderTab(
      title: 'Framing Assistant',
      icon: Icons.crop_free,
      description:
          'Target search (DSO catalog), sky-chart preview, FOV + rotation + mosaic '
          'grid framing, "Set as Target". Wired in Phase 12c per §25.5.2.',
    );
  }
}
