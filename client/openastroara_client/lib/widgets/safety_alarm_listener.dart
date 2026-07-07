import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../state/alarm/safety_alarm_state.dart';

/// §35.5 — surfaces the safety alarm as a modal the moment the flow starts
/// (during the silent-delay window, before the tone), so a user at the screen
/// can silence it before it ever rings. Wraps the app shell like the §27/§48
/// listeners.
class SafetyAlarmListener extends ConsumerStatefulWidget {
  final Widget child;

  const SafetyAlarmListener({super.key, required this.child});

  @override
  ConsumerState<SafetyAlarmListener> createState() =>
      _SafetyAlarmListenerState();
}

class _SafetyAlarmListenerState extends ConsumerState<SafetyAlarmListener> {
  bool _dialogShowing = false;

  @override
  Widget build(BuildContext context) {
    ref.listen(safetyAlarmProvider, (prev, next) {
      final wasActive = (prev?.pending ?? false) || (prev?.ringing ?? false);
      final isActive = next.pending || next.ringing;
      if (isActive && !wasActive && !_dialogShowing) {
        _showAlarmDialog(next.reason);
      }
      if (!isActive && wasActive && _dialogShowing) {
        // Single pop path: EVERY dismissal (Silence button, safety.safe)
        // flows through state → here. The flag clears synchronously BEFORE
        // the pop so a re-entrant state change can't double-pop and take the
        // screen underneath the modal with it.
        _dialogShowing = false;
        Navigator.of(context, rootNavigator: true).pop();
      }
    });
    return widget.child;
  }

  void _showAlarmDialog(String reason) {
    _dialogShowing = true;
    unawaited(
      showDialog<void>(
        context: context,
        // The barrier must not silently dismiss a safety alarm — Silence is
        // an explicit choice.
        barrierDismissible: false,
        builder: (dialogContext) => AlertDialog(
          icon: Icon(Icons.notifications_active,
              color: Theme.of(dialogContext).colorScheme.error, size: 32),
          title: const Text('SAFETY ALERT'),
          content: Text(reason.isEmpty ? 'Conditions are UNSAFE.' : reason),
          actions: [
            FilledButton(
              style: FilledButton.styleFrom(
                backgroundColor: Theme.of(dialogContext).colorScheme.error,
              ),
              // Only mutates state — the ref.listen branch above owns the
              // pop, so Silence and safety.safe share one dismissal path.
              onPressed: () =>
                  ref.read(safetyAlarmProvider.notifier).silence(),
              child: const Text('Silence alarm'),
            ),
          ],
        ),
      ).whenComplete(() => _dialogShowing = false),
    );
  }
}
