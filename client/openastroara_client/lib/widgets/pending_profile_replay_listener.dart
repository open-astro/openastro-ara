import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../state/pending_profile_drafts_state.dart';

/// §2 / #1137 — tells the user, once, when profiles the wizard saved offline
/// were applied to the rig (or could not be yet). The replay itself runs at
/// the root (`pendingProfileDraftReplayProvider`); this widget only turns its
/// notice into a snackbar where there is a Scaffold to show one. A notice posted
/// before the shell mounted (during the launch gate) is shown on the first
/// frame — `ref.listen` alone would wait for the NEXT change.
class PendingProfileReplayListener extends ConsumerStatefulWidget {
  final Widget child;
  const PendingProfileReplayListener({super.key, required this.child});

  @override
  ConsumerState<PendingProfileReplayListener> createState() =>
      _PendingProfileReplayListenerState();
}

class _PendingProfileReplayListenerState
    extends ConsumerState<PendingProfileReplayListener> {
  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) _show();
    });
  }

  void _show() {
    final message = ref
        .read(pendingProfileReplayNoticeProvider.notifier)
        .consume();
    if (message == null) return;
    ScaffoldMessenger.maybeOf(context)?.showSnackBar(
      SnackBar(content: Text(message), duration: const Duration(seconds: 8)),
    );
  }

  @override
  Widget build(BuildContext context) {
    ref.listen<String?>(pendingProfileReplayNoticeProvider, (previous, next) {
      if (next != null) _show();
    });
    return widget.child;
  }
}
