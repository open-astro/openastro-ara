import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../state/pending_profile_drafts_state.dart';

/// §2 / #1137 — tells the user, once, when profiles the wizard saved offline
/// were applied to the rig (or could not be yet). The replay itself runs at
/// the root (`pendingProfileDraftReplayProvider`); this widget only turns its
/// notice into a snackbar where there is a Scaffold to show one.
class PendingProfileReplayListener extends ConsumerWidget {
  final Widget child;
  const PendingProfileReplayListener({super.key, required this.child});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    ref.listen<String?>(pendingProfileReplayNoticeProvider, (previous, next) {
      if (next == null) return;
      final message = ref
          .read(pendingProfileReplayNoticeProvider.notifier)
          .consume();
      if (message == null) return;
      ScaffoldMessenger.maybeOf(context)?.showSnackBar(
        SnackBar(content: Text(message), duration: const Duration(seconds: 8)),
      );
    });
    return child;
  }
}
