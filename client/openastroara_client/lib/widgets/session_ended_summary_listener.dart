import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/sequence/sequence_summary.dart';
import '../services/notifications_api.dart';
import '../state/app_shell_state.dart';
import '../state/sequencer/sequence_list_state.dart';
import '../state/session/session_summary_state.dart';
import '../state/settings/settings_nav.dart';
import '../theme/ara_colors.dart';
import '../util/session_summary.dart';
import 'notifications/notification_center.dart';

/// §58.13 / #1138 — the morning card. When the client reconnects and finds a
/// run that ended (or was parked awaiting the user) while nobody was watching,
/// one dialog says what happened, when, what landed, and what the daemon did
/// about it — with the ways out: review the incidents, go to the run, dismiss.
class SessionEndedSummaryListener extends ConsumerStatefulWidget {
  final Widget child;
  const SessionEndedSummaryListener({super.key, required this.child});

  @override
  ConsumerState<SessionEndedSummaryListener> createState() =>
      _SessionEndedSummaryListenerState();
}

class _SessionEndedSummaryListenerState
    extends ConsumerState<SessionEndedSummaryListener> {
  String? _showingRunId;

  @override
  Widget build(BuildContext context) {
    ref.listen<AsyncValue<SessionSummary?>>(sessionSummaryProvider, (
      prev,
      next,
    ) {
      final summary = next.asData?.value;
      if (summary == null || summary.runId == _showingRunId) return;
      _showingRunId = summary.runId;
      unawaited(_show(summary));
    });
    return widget.child;
  }

  Future<void> _show(SessionSummary summary) async {
    await showDialog<void>(
      context: context,
      barrierDismissible: false,
      builder: (dialogContext) => SessionEndedSummaryDialog(
        summary: summary,
        onReview: () {
          Navigator.of(dialogContext).pop();
          unawaited(showNotificationCenter(context));
        },
        onOpenRun: () {
          Navigator.of(dialogContext).pop();
          ref
              .read(selectedSequenceIdProvider.notifier)
              .select(summary.sequenceId);
          ref.read(selectedTabIndexProvider.notifier).select(kRunTabIndex);
        },
        onDismiss: () => Navigator.of(dialogContext).pop(),
      ),
    );
    if (!mounted) return;
    // Any way out acknowledges: the card is a once-per-night surface.
    unawaited(
      ref.read(sessionSummaryProvider.notifier).acknowledge(summary.runId),
    );
    _showingRunId = null;
  }
}

/// The card itself (stateless so it can be golden/widget-tested alone).
class SessionEndedSummaryDialog extends StatelessWidget {
  final SessionSummary summary;
  final VoidCallback onReview;
  final VoidCallback onOpenRun;
  final VoidCallback onDismiss;
  const SessionEndedSummaryDialog({
    super.key,
    required this.summary,
    required this.onReview,
    required this.onOpenRun,
    required this.onDismiss,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final dim = theme.textTheme.bodySmall?.copyWith(
      color: AraColors.textSecondary,
    );
    final needsYou =
        summary.state == SequenceRunState.pausedAwaitingUser ||
        summary.state == SequenceRunState.failed;
    return AlertDialog(
      title: Row(
        children: [
          Icon(
            needsYou
                ? Icons.report_problem_outlined
                : Icons.nights_stay_outlined,
            color: needsYou ? AraColors.accentBusy : AraColors.accentInfo,
          ),
          const SizedBox(width: 8),
          Expanded(child: Text(summary.title)),
        ],
      ),
      content: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 480),
        child: SingleChildScrollView(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: [
              Text(summary.headline),
              const SizedBox(height: 8),
              Text(summary.framesLine, style: dim),
              if (summary.incidents.isNotEmpty) ...[
                const SizedBox(height: 12),
                Text('What happened', style: theme.textTheme.titleSmall),
                const SizedBox(height: 4),
                for (final n in summary.incidents) _Incident(n),
              ] else ...[
                const SizedBox(height: 8),
                Text('No warnings were raised during the run.', style: dim),
              ],
            ],
          ),
        ),
      ),
      actions: [
        if (summary.incidents.isNotEmpty)
          TextButton(onPressed: onReview, child: const Text('Review details')),
        TextButton(onPressed: onOpenRun, child: const Text('Open the run')),
        FilledButton(onPressed: onDismiss, child: const Text('Got it')),
      ],
    );
  }
}

class _Incident extends StatelessWidget {
  final AraNotification n;
  const _Incident(this.n);

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final local = n.postedUtc.toLocal();
    final hhmm =
        '${local.hour.toString().padLeft(2, '0')}:${local.minute.toString().padLeft(2, '0')}';
    final color = switch (n.severity) {
      NotificationSeverity.critical ||
      NotificationSeverity.error => AraColors.accentError,
      NotificationSeverity.warning => AraColors.accentBusy,
      NotificationSeverity.info => AraColors.textSecondary,
    };
    return Padding(
      padding: const EdgeInsets.only(bottom: 6),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            '$hhmm · ${n.title}',
            style: theme.textTheme.bodyMedium?.copyWith(color: color),
          ),
          if (n.message.trim().isNotEmpty)
            Text(
              n.message,
              style: theme.textTheme.bodySmall?.copyWith(
                color: AraColors.textSecondary,
              ),
              maxLines: 4,
              overflow: TextOverflow.ellipsis,
            ),
        ],
      ),
    );
  }
}
