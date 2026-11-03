import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../state/app_shell_state.dart';
import '../l10n/l10n.dart';
import '../models/preflight_checklist.dart';
import '../state/preflight/preflight_state.dart';
import '../state/simple_messages_state.dart';
import '../state/settings/settings_nav.dart';
import 'preflight/preflight_checklist_card.dart';

/// §58.14 / #1138 — the bedtime card. Once per night, when a sequence is
/// running after dark and this device has not answered yet, the daemon's
/// pre-sleep checklist is read fresh and shown whatever tab is showing.
/// "All good" answers for tonight; "Adjust" answers too and opens Setup →
/// Before you sleep, where the card stays reachable with a Refresh.
class PreflightChecklistListener extends ConsumerStatefulWidget {
  final Widget child;

  /// Simple Mode: a message pill with one answer instead of the dialog, and
  /// no Adjust into the full UI (no popups, nothing links to it).
  final bool quiet;
  const PreflightChecklistListener({
    super.key,
    required this.child,
    this.quiet = false,
  });

  @override
  ConsumerState<PreflightChecklistListener> createState() =>
      _PreflightChecklistListenerState();
}

class _PreflightChecklistListenerState
    extends ConsumerState<PreflightChecklistListener> {
  bool _showing = false;

  /// Simple Mode's pill is up and not answered yet: one at a time.
  bool _pillUp = false;

  @override
  Widget build(BuildContext context) {
    ref.listen<AsyncValue<PreflightPromptState>>(preflightPromptProvider, (
      prev,
      next,
    ) {
      if (next.asData?.value.shouldPrompt != true) return;
      if (_showing || _pillUp) return;
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
      if (widget.quiet) {
        _pill(checklist);
        return; // answered from the pill
      }
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
      _answer();
    } catch (_) {
      // An unreachable daemon is not a bedtime card; the pane shows the error.
    } finally {
      _showing = false;
    }
  }

  // Either way out answers tonight: the card is a once-per-night surface.
  void _answer() {
    _pillUp = false;
    unawaited(ref.read(preflightPromptProvider.notifier).acknowledgeTonight());
  }

  /// What needs a look (or that nothing does), and the one answer.
  void _pill(PreflightChecklist checklist) {
    final l10n = context.l10n;
    final needs = [
      for (final i in checklist.items)
        if (i.status != 'ok') '${i.title}: ${i.detail}',
    ];
    _pillUp = true;
    // The pill can outlive this listener (a switch to the full UI): its
    // answer goes through the container, never this widget's ref.
    final container = ProviderScope.containerOf(context, listen: false);
    container
        .read(simpleMessagesProvider.notifier)
        .show(
          SimpleMessageKind.question,
          l10n.shellBedtimeTitle,
          detail: needs.isEmpty ? l10n.shellBedtimeAllGood : needs.join('\n'),
          actions: [
            SimpleMessageAction(l10n.shellBedtimeAnswer, () {
              if (mounted) _pillUp = false;
              unawaited(
                container
                    .read(preflightPromptProvider.notifier)
                    .acknowledgeTonight(),
              );
            }),
          ],
        );
  }
}
