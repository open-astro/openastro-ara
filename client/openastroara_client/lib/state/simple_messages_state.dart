import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../theme/ara_colors.dart';
import 'simple_mode_state.dart';

/// Simple Mode's message pills (ALP-4825): no popups, so results, errors and
/// questions show as pills in the top-left corner.
enum SimpleMessageKind {
  /// Green dot; fades after [kSimpleResultLife].
  result,

  /// Red outline; stays until dismissed.
  error,

  /// Amber outline with its buttons; stays until answered.
  question,
}

/// How long a result pill stays.
const kSimpleResultLife = Duration(seconds: 6);

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
  });

  final int id;
  final SimpleMessageKind kind;
  final String title;
  final String? detail;

  /// A question's buttons, the last one the main answer.
  final List<SimpleMessageAction> actions;
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
  }) {
    final id = _next++;
    state = [
      SimpleMessage(
        id: id,
        kind: kind,
        title: title,
        detail: detail,
        actions: actions,
      ),
      ...state,
    ];
    if (kind == SimpleMessageKind.result) {
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
/// snackbar otherwise ("[title]: [detail]", red when [error]).
typedef UserNotify = void Function(
  String title, {
  String? detail,
  bool error,
  Duration? snackBarDuration,
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
    Duration? snackBarDuration,
  }) {
    if (container.read(simpleModeProvider).value ?? false) {
      container
          .read(simpleMessagesProvider.notifier)
          .show(
            error ? SimpleMessageKind.error : SimpleMessageKind.result,
            title,
            detail: detail,
          );
      return;
    }
    messenger?.showSnackBar(
      SnackBar(
        content: Text(detail == null ? title : '$title: $detail'),
        backgroundColor: error ? AraColors.accentError : null,
        duration: snackBarDuration ?? const Duration(milliseconds: 4000),
      ),
    );
  };
}
