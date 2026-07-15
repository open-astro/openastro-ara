import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../state/launch_gate_state.dart';
import '../theme/ara_colors.dart';

/// "Launchpad" — leave the shell and re-run the §30 launch flow (server
/// connect skipped when unambiguous, profile box, offline picker). Re-arms
/// the session gates; the root router then swaps the shell out and the
/// window drops back to its compact launchpad size. Confirmation keeps a
/// mis-click from yanking the user out of a planning session.
class LaunchpadButton extends ConsumerWidget {
  const LaunchpadButton({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    return TextButton.icon(
      onPressed: () async {
        final ok = await showDialog<bool>(
          context: context,
          builder: (ctx) => AlertDialog(
            title: const Text('Back to the launchpad?'),
            content: const Text(
                'Returns to the server & profile chooser. Nothing running on '
                'the server is affected; unsaved local edits stay in memory '
                'until the app closes.'),
            actions: [
              TextButton(
                  onPressed: () => Navigator.of(ctx).pop(false),
                  child: const Text('Cancel')),
              FilledButton(
                  onPressed: () => Navigator.of(ctx).pop(true),
                  child: const Text('Launchpad')),
            ],
          ),
        );
        if (ok != true) return;
        ref.read(offlineModeProvider.notifier).exit();
        ref.read(profileGatePassedProvider.notifier).reset();
      },
      icon: const Icon(Icons.rocket_launch_outlined, size: 16),
      label: const Text('Launchpad'),
      style: TextButton.styleFrom(
        foregroundColor: AraColors.textSecondary,
        textStyle: Theme.of(context).textTheme.bodySmall,
      ),
    );
  }
}
