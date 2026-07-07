import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../state/sequencer/sequence_list_state.dart';
import '../state/ws/ws_providers.dart';

/// §48 "capture calibration tonight?" surfacing. Wraps the app shell's body
/// and listens for the daemon's `sequence.auto_flats_prompt` event (emitted
/// at sequence start when the profile's calibration capture default is
/// "ask"). The answer rides `POST /sequences/{id}/auto-flats-decision`;
/// "remember my choice" persists it as the profile default so the prompt
/// stops appearing. Ignoring the dialog is safe — an unanswered run simply
/// ends without flats, same as "not tonight".
class AutoFlatsPromptListener extends ConsumerStatefulWidget {
  final Widget child;

  const AutoFlatsPromptListener({super.key, required this.child});

  @override
  ConsumerState<AutoFlatsPromptListener> createState() =>
      _AutoFlatsPromptListenerState();
}

class _AutoFlatsPromptListenerState
    extends ConsumerState<AutoFlatsPromptListener> {
  /// Pops the currently-shown prompt, if any. A second prompt can only mean a
  /// newer run started (one decision per run server-side), so a still-open
  /// first dialog answers a run that no longer wants it — close it.
  VoidCallback? _dismissPrompt;

  @override
  Widget build(BuildContext context) {
    ref.listen(wsEventsProvider, (previous, next) {
      final event = next.asData?.value;
      if (event == null || event.type != 'sequence.auto_flats_prompt') return;
      final sequenceId = event.payload['sequence_id'];
      if (sequenceId is! String || sequenceId.isEmpty) return;
      _showPrompt(sequenceId);
    });
    return widget.child;
  }

  void _showPrompt(String sequenceId) {
    _dismissPrompt?.call();
    var dismissed = false;
    _dismissPrompt = () {
      if (!dismissed && mounted) {
        dismissed = true;
        Navigator.of(context, rootNavigator: true).pop();
      }
    };
    unawaited(
      showDialog<void>(
        context: context,
        // Barrier-dismiss is a valid answer ("decide later") — the daemon
        // treats an unanswered run exactly like "not tonight".
        barrierDismissible: true,
        builder: (dialogContext) => _AutoFlatsPromptDialog(
          onAnswer: (choice, remember) async {
            final api = ref.read(sequenceApiProvider);
            if (api == null) return;
            try {
              await api.decideAutoFlats(sequenceId,
                  choice: choice, remember: remember);
            } catch (e) {
              if (!mounted) return;
              ScaffoldMessenger.of(context).showSnackBar(SnackBar(
                  content:
                      Text('Could not send the calibration choice: $e')));
            }
          },
        ),
      ).whenComplete(() {
        dismissed = true;
        _dismissPrompt = null;
      }),
    );
  }
}

/// The §48.1 prompt. Choices map to the daemon's wire tokens; "remember"
/// persists the pick as the profile's calibration capture default ("not
/// tonight" persists as "never" per §48.2).
class _AutoFlatsPromptDialog extends StatefulWidget {
  final Future<void> Function(String choice, bool remember) onAnswer;

  const _AutoFlatsPromptDialog({required this.onAnswer});

  @override
  State<_AutoFlatsPromptDialog> createState() => _AutoFlatsPromptDialogState();
}

class _AutoFlatsPromptDialogState extends State<_AutoFlatsPromptDialog> {
  bool _remember = false;

  void _answer(String choice) {
    unawaited(widget.onAnswer(choice, _remember));
    if (mounted) Navigator.of(context).pop();
  }

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: const Text('Capture calibration frames tonight?'),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Text(
            'When this sequence completes, Ara can generate matching flats '
            'from tonight\'s session — every filter replaying tonight\'s '
            'focus, gain, and offset. Panel flats start automatically at the '
            'end (light your panel when notified); sky flats are prepared '
            'ready to run at twilight.',
          ),
          const SizedBox(height: 12),
          CheckboxListTile(
            value: _remember,
            onChanged: (v) => setState(() => _remember = v ?? false),
            controlAffinity: ListTileControlAffinity.leading,
            contentPadding: EdgeInsets.zero,
            dense: true,
            title: const Text('Remember my choice (stop asking)'),
          ),
        ],
      ),
      actions: [
        TextButton(
          onPressed: () => _answer('later'),
          child: const Text('Not tonight'),
        ),
        TextButton(
          onPressed: () => _answer('sky_at_twilight'),
          child: const Text('Sky flats at twilight'),
        ),
        FilledButton(
          onPressed: () => _answer('panel_at_end'),
          child: const Text('Panel flats at end'),
        ),
      ],
    );
  }
}
