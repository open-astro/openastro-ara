import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../state/app_shell_state.dart';
import '../state/settings/settings_nav.dart';
import '../state/simple_mode_state.dart';

/// Full UI top-bar button that switches to Simple Mode (ALP-4810), shown only
/// while the experimental toggle is on. It points
/// the full UI at Live first, so any later tab jump made from Simple Mode
/// (gear, device dots, ⌘K) registers as a change and brings the full UI back.
class SimpleModeSwitchButton extends ConsumerWidget {
  const SimpleModeSwitchButton({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    // Experimental: nothing in the full UI hints at it until it's turned on.
    if (!(ref.watch(simpleModeFeatureProvider).value ?? false)) {
      return const SizedBox.shrink();
    }
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 2),
      child: IconButton(
        icon: const Icon(Icons.fit_screen_outlined, size: 22),
        tooltip: 'Simple Mode',
        onPressed: () {
          ref.read(selectedTabIndexProvider.notifier).select(kLiveTabIndex);
          ref.read(simpleModeProvider.notifier).set(true);
        },
      ),
    );
  }
}
