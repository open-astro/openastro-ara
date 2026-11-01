import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../state/app_shell_state.dart';
import '../state/settings/settings_nav.dart';
import '../state/simple_mode_state.dart';

/// Full UI top-bar button that switches to Simple Mode (ALP-4810). It points
/// the full UI at Live first, so any later tab jump made from Simple Mode
/// (gear, device dots, ⌘K) registers as a change and brings the full UI back.
class SimpleModeSwitchButton extends ConsumerWidget {
  const SimpleModeSwitchButton({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    return IconButton(
      icon: const Icon(Icons.fit_screen_outlined, size: 22),
      tooltip: 'Simple Mode',
      onPressed: () {
        ref.read(selectedTabIndexProvider.notifier).select(kLiveTabIndex);
        ref.read(simpleModeProvider.notifier).set(true);
      },
    );
  }
}
