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

  /// Red outline; fades after [kSimpleErrorLife] on screen, or on ×.
  error,

  /// Amber outline; stays until dismissed: done, but the rig needs a look (an
  /// Emergency Stop that went through).
  warning,

  /// Amber outline with its buttons; stays until answered.
  question,
}

/// How long a result pill stays.
const kSimpleResultLife = Duration(seconds: 10); // 6 until 2026-11-04 (user: too fast)

/// How long an error pill stays: longer than a result, but it fades too
/// (user, 2026-11-04: not a sticky pill); × closes it sooner.
const kSimpleErrorLife = Duration(seconds: 20);

/// Where something on screen came from: a tap on its status pill or its
/// message opens it (user, 2026-11-04).
enum SimpleStatusOrigin {
  preview,
  plan,
  mount,
  camera,
  focuser,
  filterWheel,
  rotator,
  guiderCamera,
  focus,
  polar,
  guide,
}

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
    this.origin,
    this.sticky = false,
  });

  final int id;
  final SimpleMessageKind kind;
  final String title;
  final String? detail;

  /// A question's buttons, the last one the main answer.
  final List<SimpleMessageAction> actions;

  /// What a tap on the pill opens, when known.
  final SimpleStatusOrigin? origin;

  /// An error that waits to be read (an Emergency Stop's failed rung):
  /// it never fades, only × closes it.
  final bool sticky;
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
    SimpleStatusOrigin? origin,
    bool sticky = false,
  }) {
    final id = _next++;
    state = [
      SimpleMessage(
        id: id,
        kind: kind,
        title: title,
        detail: detail,
        actions: actions,
        origin: origin,
        sticky: sticky,
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
/// snackbar otherwise ("[title]: [detail]", red when [error] unless
/// [snackBarRed] says otherwise, so the full UI keeps its look).
typedef UserNotify = void Function(
  String title, {
  String? detail,
  bool error,
  bool? snackBarRed,
  Duration? snackBarDuration,
  bool warning,
  bool sticky,
});

/// A [UserNotify] for [context], captured before any await (the context may
/// be gone when the answer arrives); its pills open [origin] when tapped.
UserNotify userNotifier(BuildContext context, {SimpleStatusOrigin? origin}) {
  final container = ProviderScope.containerOf(context, listen: false);
  final messenger = ScaffoldMessenger.maybeOf(context);
  return (
    String title, {
    String? detail,
    bool error = false,
    bool? snackBarRed,
    Duration? snackBarDuration,
    bool warning = false,
    bool sticky = false,
  }) {
    // Active, not just chosen: with the Experimental switch off the full UI
    // shows, and a pill there would reach nobody.
    if (container.read(simpleModeActiveProvider)) {
      container
          .read(simpleMessagesProvider.notifier)
          .show(
            error
                ? SimpleMessageKind.error
                : warning
                ? SimpleMessageKind.warning
                : SimpleMessageKind.result,
            title,
            detail: detail,
            origin: origin,
            sticky: sticky,
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
