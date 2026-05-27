import 'package:flutter/material.dart';

import '../settings/settings_shell.dart';

/// Options tab per §25.5.5. Phase 12h.1 replaces the Phase 12a placeholder
/// with the real `SettingsShell` (tree nav + selected panel). Phase 12h.2
/// fills in the remaining panels; 12h.3 layers the §61 ⌘K smart search.
class OptionsTab extends StatelessWidget {
  const OptionsTab({super.key});

  @override
  Widget build(BuildContext context) {
    return const SettingsShell();
  }
}
