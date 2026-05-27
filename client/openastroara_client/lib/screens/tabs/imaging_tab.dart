import 'package:flutter/material.dart';

import 'placeholder_tab.dart';

class ImagingTab extends StatelessWidget {
  const ImagingTab({super.key});

  @override
  Widget build(BuildContext context) {
    return const PlaceholderTab(
      title: 'Imaging',
      icon: Icons.camera_alt,
      description:
          'Live capture workspace: most-recent frame preview, plate-solve overlay, '
          'exposure/gain/offset controls, Take One + Live View. '
          'Wired in Phase 12c per playbook §25.5.1.',
    );
  }
}
