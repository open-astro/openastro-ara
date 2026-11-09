import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../l10n/l10n.dart';
import '../services/ws_event_stream.dart';
import '../state/simple_messages_state.dart';
import '../state/simple_mode_state.dart';
import '../state/ws/ws_providers.dart';

/// §27 single-client policy surfacing. Wraps the app shell's body and listens
/// for the two daemon-initiated moments that need a modal:
///
///  1. `connection.request` — another client asked to connect while THIS client
///     holds the control slot → "X wants to connect. [Allow] [Keep me
///     connected]". Unanswered, the daemon times the request out at 30 s, so
///     the dialog auto-dismisses just before that.
///  2. WS close 4004 — the takeover happened (this client lost the slot) →
///     "Session transferred" modal with an explicit Reconnect action
///     (reconnecting silently would fight the new holder for the slot).
class ConnectionPolicyListener extends ConsumerStatefulWidget {
  final Widget child;

  const ConnectionPolicyListener({super.key, required this.child});

  @override
  ConsumerState<ConnectionPolicyListener> createState() =>
      _ConnectionPolicyListenerState();
}

class _ConnectionPolicyListenerState
    extends ConsumerState<ConnectionPolicyListener> {
  /// Pops the currently-shown takeover-request dialog, if any. A second
  /// request can only arrive after the first resolved server-side (one pending
  /// dance at a time), so a still-open first dialog is stale — close it.
  VoidCallback? _dismissTakeoverDialog;

  /// Simple Mode's takeover pill, withdrawn just before the daemon times out.
  Timer? _takeoverPillTimeout;

  @override
  void dispose() {
    _takeoverPillTimeout?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    ref.listen(wsTakeoverRequestsProvider, (previous, next) {
      final request = next.asData?.value;
      if (request != null) _showTakeoverRequestDialog(request);
    });
    ref.listen(wsConnectionStateProvider, (previous, next) {
      final became = next.asData?.value;
      final was = previous?.asData?.value;
      if (became == WsConnectionState.takenOver &&
          was != WsConnectionState.takenOver) {
        _showSessionTransferredDialog();
      }
    });
    return widget.child;
  }

  void _showTakeoverRequestDialog(WsConnectionRequest request) {
    _dismissTakeoverDialog?.call();
    if (ref.read(simpleModeActiveProvider)) {
      _askTakeoverInPill(request);
      return;
    }
    var dismissed = false;
    _dismissTakeoverDialog = () {
      if (!dismissed && mounted) {
        dismissed = true;
        Navigator.of(context, rootNavigator: true).pop();
      }
    };
    unawaited(
      showDialog<void>(
        context: context,
        // Ignoring the modal must NOT silently allow/reject — a barrier tap just
        // dismisses; the daemon's own 30 s timeout answers "unresponsive".
        barrierDismissible: true,
        builder: (dialogContext) => _TakeoverRequestDialog(
          from: request.from,
          onAnswer: (allow) {
            ref
                .read(wsEventStreamProvider)
                ?.sendConnectionResponse(request.requestId, allow: allow);
          },
        ),
      ).whenComplete(() {
        dismissed = true;
        _dismissTakeoverDialog = null;
      }),
    );
  }

  /// Simple Mode has no popups: the same question on a pill.
  void _askTakeoverInPill(WsConnectionRequest request) {
    final messages = ref.read(simpleMessagesProvider.notifier);
    final l10n = context.l10n;
    void answer(bool allow) => ref
        .read(wsEventStreamProvider)
        ?.sendConnectionResponse(request.requestId, allow: allow);
    final id = messages.show(
      SimpleMessageKind.question,
      l10n.takeoverWants(request.from),
      detail: l10n.takeoverDetail,
      actions: [
        SimpleMessageAction(l10n.takeoverKeep, () => answer(false)),
        SimpleMessageAction(l10n.takeoverAllow, () => answer(true)),
      ],
    );
    void withdraw() {
      _takeoverPillTimeout?.cancel();
      _dismissTakeoverDialog = null;
      messages.dismiss(id);
    }

    _dismissTakeoverDialog = withdraw;
    _takeoverPillTimeout = Timer(_TakeoverRequestDialog.timeout, withdraw);
  }

  void _showSessionTransferredDialog() {
    // The takeover-request dialog (if the user never answered it) is moot now.
    _dismissTakeoverDialog?.call();
    if (ref.read(simpleModeActiveProvider)) {
      ref.read(simpleMessagesProvider.notifier).show(
        SimpleMessageKind.question,
        _transferredTitle,
        detail: _transferredDetail,
        actions: [
          SimpleMessageAction('OK', () {}),
          SimpleMessageAction(
            'Reconnect',
            () => ref.read(wsEventStreamProvider)?.connect(),
          ),
        ],
      );
      return;
    }
    unawaited(
      showDialog<void>(
        context: context,
        builder: (dialogContext) => AlertDialog(
          title: const Text(_transferredTitle),
          content: const Text(_transferredDetail),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(dialogContext).pop(),
              child: const Text('OK'),
            ),
            FilledButton(
              onPressed: () {
                Navigator.of(dialogContext).pop();
                // Explicit user action: re-claim the slot. The other client gets
                // the Allow / Keep-me-connected modal in turn.
                ref.read(wsEventStreamProvider)?.connect();
              },
              child: const Text('Reconnect'),
            ),
          ],
        ),
      ),
    );
  }
}

const _transferredTitle = 'Session transferred';
const _transferredDetail =
    'Another client took control of the observatory, so this client was '
    'disconnected. The running sequence is unaffected — it continues on Ara.';

/// The holder-side §27.1 modal. Auto-dismisses shortly before the daemon's
/// 30 s answer window lapses (an unanswered request reads as "unresponsive"
/// to the daemon; keeping a dead dialog up would suggest the choice still
/// matters).
class _TakeoverRequestDialog extends StatefulWidget {
  /// Auto-dismiss just under the daemon's 30 s connection.request timeout.
  static const Duration timeout = Duration(seconds: 25);

  final String from;
  final void Function(bool allow) onAnswer;

  const _TakeoverRequestDialog({required this.from, required this.onAnswer});

  @override
  State<_TakeoverRequestDialog> createState() => _TakeoverRequestDialogState();
}

class _TakeoverRequestDialogState extends State<_TakeoverRequestDialog> {
  Timer? _autoDismiss;

  @override
  void initState() {
    super.initState();
    _autoDismiss = Timer(_TakeoverRequestDialog.timeout, () {
      // Self-closing dialog: gate the pop on State.mounted (the dialog may
      // already be gone via a button or the barrier).
      if (mounted) Navigator.of(context).pop();
    });
  }

  @override
  void dispose() {
    _autoDismiss?.cancel();
    _autoDismiss = null;
    super.dispose();
  }

  void _answer(bool allow) {
    widget.onAnswer(allow);
    if (mounted) Navigator.of(context).pop();
  }

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: Text(context.l10n.takeoverWants(widget.from)),
      content: Text(context.l10n.takeoverDetail),
      actions: [
        TextButton(
          onPressed: () => _answer(false),
          child: Text(context.l10n.takeoverKeep),
        ),
        FilledButton(
          onPressed: () => _answer(true),
          child: Text(context.l10n.takeoverAllow),
        ),
      ],
    );
  }
}
