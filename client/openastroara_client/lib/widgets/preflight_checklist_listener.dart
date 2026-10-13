import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../state/app_shell_state.dart';
import '../state/preflight/preflight_state.dart';
import '../state/settings/settings_nav.dart';
import 'preflight/preflight_checklist_card.dart';

/// §58.14 / #1138 — the bedtime card. Once per night, when a sequence is
/// running after dark and this device has not answered yet, the daemon's
/// pre-sleep checklist is read fresh and shown whatever tab is showing.
/// "All good" answers for tonight; "Adjust" answers too and opens Setup →
/// Before you sleep, where the card stays reachable with a Refresh.
class PreflightChecklistListener extends ConsumerStatefulWidget {
  final Widget child;
  const PreflightChecklistListener({super.key, required this.child});

  @override
  ConsumerState<PreflightChecklistListener> createState() =>
      _PreflightChecklistListenerState();
}

class _PreflightChecklistListenerState
    extends ConsumerState<PreflightChecklistListener> {
  bool _showing = false;

  @override
  Widget build(BuildContext context) {
    ref.listen<AsyncValue<PreflightPromptState>>(preflightPromptProvider, (
      prev,
      next,
    ) {
      if (next.asData?.value.shouldPrompt != true) return;
      if (_showing) return;
      _showing = true;
      unawaited(_show());
    });
    return widget.child;
  }

  Future<void> _show() async {
    try {
      // Fresh, not cached: the user is about to go to bed on this answer.
      final checklist = await ref
          .read(preflightChecklistProvider.notifier)
          .refresh();
      // No checklist (older daemon) or the read failed: nothing to show. The
      // prompt stays armed and the next sky tick retries.
      if (checklist == null || !mounted) return;
      await showDialog<void>(
        context: context,
        barrierDismissible: false,
        builder: (dialogContext) => PreflightChecklistDialog(
          checklist: checklist,
          onAllGood: () => Navigator.of(dialogContext).pop(),
          onAdjust: () {
            Navigator.of(dialogContext).pop();
            ref.read(selectedTabIndexProvider.notifier).select(kSetupTabIndex);
            ref.read(setupPreSleepRequestProvider.notifier).request();
          },
        ),
      );
      if (!mounted) return;
      // Either way out answers tonight: the card is a once-per-night surface.
      unawaited(
        ref.read(preflightPromptProvider.notifier).acknowledgeTonight(),
      );
    } catch (_) {
      // An unreachable daemon is not a bedtime card; the pane shows the error.
    } finally {
      _showing = false;
    }
  }
}
