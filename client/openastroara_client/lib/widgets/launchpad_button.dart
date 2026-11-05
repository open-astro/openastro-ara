import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../state/launch_gate_state.dart';

/// Confirm, then re-arm the session gates so the root router drops back to
/// the launchpad. Shared by the status-bar button and its overflow menu.
Future<void> backToLaunchpad(BuildContext context, WidgetRef ref) async {
  final ok = await showDialog<bool>(
    context: context,
    builder: (ctx) => AlertDialog(
      title: const Text('Back to the launchpad?'),
      content: const Text(
        'Returns to the server & profile chooser. Nothing running on '
        'the server is affected; unsaved local edits stay in memory '
        'until the app closes.',
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(ctx).pop(false),
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: () => Navigator.of(ctx).pop(true),
          child: const Text('Launchpad'),
        ),
      ],
    ),
  );
  if (ok != true) return;
  ref.read(offlineModeProvider.notifier).exit();
  ref.read(profileGatePassedProvider.notifier).reset();
  // All the way back to screen one: the server chooser, not the
  // profile box (with servers saved the router would otherwise
  // resume the flow one step in).
  ref.read(serverChooserRequestedProvider.notifier).request();
}
