import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../theme/ara_colors.dart';
import 'simple_mode_state.dart';

/// Simple Mode's message pills (ALP-4825): no popups, so results, errors and
/// questions show as pills in the top-left corner.
enum SimpleMessageKind {
  /// Green dot; fades after [kSimpleResultLife], unless it [SimpleMessage.stays].
  result,

  /// Red outline; stays until dismissed.
  error,

  /// Amber outline with its buttons; stays until answered.
  question,
}

/// How long a result pill stays.
const kSimpleResultLife = Duration(seconds: 10); // 6 until 2026-11-04 (user: too fast)

class SimpleMessageAction {
  const SimpleMessageAction(this.label, this.onPressed);
  final String label;
  final VoidCallback onPressed;
}

class SimpleMessage {
  const SimpleMessage({
    required this.id,
    required this.kind,
    required this.title,
    this.detail,
    this.actions = const [],
    this.stays = false,
  });

  final int id;
  final SimpleMessageKind kind;
  final String title;
  final String? detail;

  /// A question's buttons, the last one the main answer.
  final List<SimpleMessageAction> actions;

  /// A result that waits to be read (closed with ×) instead of fading: an
  /// Emergency Stop's summary.
  final bool stays;
}

/// The pills showing, newest first.
class SimpleMessagesNotifier extends Notifier<List<SimpleMessage>> {
  var _next = 0;
  final _timers = <int, Timer>{};

  @override
  List<SimpleMessage> build() {
    ref.onDispose(() {
      for (final t in _timers.values) {
        t.cancel();
      }
    });
    return const [];
  }

  int show(
    SimpleMessageKind kind,
    String title, {
    String? detail,
    List<SimpleMessageAction> actions = const [],
    bool stays = false,
  }) {
    final id = _next++;
    state = [
      SimpleMessage(
        id: id,
        kind: kind,
        title: title,
        detail: detail,
        actions: actions,
        stays: stays,
      ),
      ...state,
    ];
    if (kind == SimpleMessageKind.result && !stays) {
      _timers[id] = Timer(kSimpleResultLife, () => dismiss(id));
    }
    return id;
  }

  void dismiss(int id) {
    _timers.remove(id)?.cancel();
    state = [
      for (final m in state)
        if (m.id != id) m,
    ];
  }
}

final simpleMessagesProvider =
    NotifierProvider<SimpleMessagesNotifier, List<SimpleMessage>>(
      SimpleMessagesNotifier.new,
    );

/// Tells the user what happened: a pill in Simple Mode, the full UI's
/// snackbar otherwise ("[title]: [detail]", red when [error] unless
/// [snackBarRed] says otherwise, so the full UI keeps its look).
typedef UserNotify = void Function(
  String title, {
  String? detail,
  bool error,
  bool? snackBarRed,
  Duration? snackBarDuration,
  bool stays,
});

/// A [UserNotify] for [context], captured before any await (the context may
/// be gone when the answer arrives).
UserNotify userNotifier(BuildContext context) {
  final container = ProviderScope.containerOf(context, listen: false);
  final messenger = ScaffoldMessenger.maybeOf(context);
  return (
    String title, {
    String? detail,
    bool error = false,
    bool? snackBarRed,
    Duration? snackBarDuration,
    bool stays = false,
  }) {
    // Active, not just chosen: with the Experimental switch off the full UI
    // shows, and a pill there would reach nobody.
    if (container.read(simpleModeActiveProvider)) {
      container
          .read(simpleMessagesProvider.notifier)
          .show(
            error ? SimpleMessageKind.error : SimpleMessageKind.result,
            title,
            detail: detail,
            stays: stays,
          );
      return;
    }
    messenger?.showSnackBar(
      SnackBar(
        content: Text(detail == null ? title : '$title: $detail'),
        backgroundColor: (snackBarRed ?? error) ? AraColors.accentError : null,
        duration: snackBarDuration ?? const Duration(milliseconds: 4000),
      ),
    );
  };
}
