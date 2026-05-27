import 'package:flutter/material.dart';

import 'placeholder_tab.dart';

class SkyAtlasTab extends StatelessWidget {
  const SkyAtlasTab({super.key});

  @override
  Widget build(BuildContext context) {
    return const PlaceholderTab(
      title: 'Sky Atlas',
      icon: Icons.public,
      description:
          'Aladin Lite (CDS) with bundled HiPS catalogs + Tonight’s Sky '
          'planetarium view. Wired in Phase 12e per playbook §36.',
    );
  }
}
