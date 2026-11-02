import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../state/app_shell_state.dart';
import '../state/settings/settings_nav.dart';
import '../theme/ara_colors.dart';

/// Top-bar gear that opens Settings. Settings left the nav rail, which keeps
/// the night's workflow only (Planning → Checklist → Sequence → Live); the gear is lit
/// while Settings is showing.
class SettingsGearButton extends ConsumerWidget {
  const SettingsGearButton({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final open = ref.watch(selectedTabIndexProvider) == kOptionsTabIndex;
    return IconButton(
      icon: Icon(open ? Icons.settings : Icons.settings_outlined,
          size: 22, color: open ? AraColors.accentInfo : null),
      tooltip: 'Settings',
      onPressed: () =>
          ref.read(selectedTabIndexProvider.notifier).select(kOptionsTabIndex),
    );
  }
}
