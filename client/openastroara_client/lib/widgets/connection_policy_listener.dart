import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../services/ws_event_stream.dart';
import '../state/ws/ws_providers.dart';

/// §27 single-client policy surfacing. Wraps the app shell's body and listens
/// for the two daemon-initiated moments that need a modal:
///
///  1. `connection.request` — another WILMA asked to connect while THIS client
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

  void _showSessionTransferredDialog() {
    // The takeover-request dialog (if the user never answered it) is moot now.
    _dismissTakeoverDialog?.call();
    unawaited(
      showDialog<void>(
        context: context,
        builder: (dialogContext) => AlertDialog(
          title: const Text('Session transferred'),
          content: const Text(
            'Another WILMA client took control of the observatory, so this '
            'client was disconnected. The running sequence is unaffected — it '
            'continues on the daemon.',
          ),
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
      title: Text('${widget.from} wants to connect'),
      content: const Text(
        'Only one WILMA can control the observatory at a time. Allowing '
        'hands control to the other client and disconnects this one; the '
        'running sequence continues either way.',
      ),
      actions: [
        TextButton(
          onPressed: () => _answer(false),
          child: const Text('Keep me connected'),
        ),
        FilledButton(
          onPressed: () => _answer(true),
          child: const Text('Allow'),
        ),
      ],
    );
  }
}
