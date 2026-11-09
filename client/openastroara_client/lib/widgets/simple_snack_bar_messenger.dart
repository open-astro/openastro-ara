import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../state/simple_messages_state.dart';
import '../state/simple_mode_state.dart';
import '../theme/ara_colors.dart';

/// The words of a SnackBar's content: Text and rich text, read through the
/// layout widgets around them. Null when there are none to read.
String? simpleSnackBarText(Widget content) {
  final text = switch (content) {
    Text(:final data, :final textSpan) =>
      // U+FFFC marks a widget inside the rich text: not words.
      data ?? textSpan?.toPlainText().replaceAll('￼', ''),
    ProxyWidget(:final child) => simpleSnackBarText(child),
    SingleChildRenderObjectWidget(:final child?) => simpleSnackBarText(child),
    MultiChildRenderObjectWidget(:final children) =>
      children.map(simpleSnackBarText).nonNulls.join(' '),
    _ => null,
  };
  return text == null || text.trim().isEmpty ? null : text.trim();
}

final _failure = RegExp(
  r"\b(failed|failure|couldn.?t|could not|can.?t|cannot|unable|error|falhou|falha|erro|não foi possível)\b",
  caseSensitive: false,
);

/// What pill a SnackBar makes: a red background or words of failure are an
/// error, anything else a result.
SimpleMessageKind simpleSnackBarKind(Color? background, String text) =>
    background == AraColors.accentError ||
        background == AraColors.errorText ||
        _failure.hasMatch(text)
    ? SimpleMessageKind.error
    : SimpleMessageKind.result;

/// Above the Navigator, so every route's snackbar lands here: while Simple
/// Mode is on it shows as a message pill (no popups, ALP-4825).
class SimpleScaffoldMessenger extends ScaffoldMessenger {
  const SimpleScaffoldMessenger({super.key, required super.child});

  @override
  ScaffoldMessengerState createState() => _SimpleScaffoldMessengerState();
}

class _SimpleScaffoldMessengerState extends ScaffoldMessengerState {
  @override
  ScaffoldFeatureController<SnackBar, SnackBarClosedReason> showSnackBar(
    SnackBar snackBar, {
    AnimationStyle? snackBarAnimationStyle,
  }) {
    final container = ProviderScope.containerOf(context, listen: false);
    final simple = container.read(simpleModeActiveProvider);
    final text = simple ? simpleSnackBarText(snackBar.content) : null;
    if (text == null) {
      if (simple) {
        debugPrint(
          '[simple] a snackbar with no text to pill: ${snackBar.content}',
        );
      }
      return super.showSnackBar(
        snackBar,
        snackBarAnimationStyle: snackBarAnimationStyle,
      );
    }
    final action = snackBar.action;
    container
        .read(simpleMessagesProvider.notifier)
        .show(
          simpleSnackBarKind(snackBar.backgroundColor, text),
          text,
          actions: [
            if (action != null)
              SimpleMessageAction(action.label, action.onPressed),
          ],
        );
    // The caller still gets a controller whose `closed` completes; only the
    // queue's head can be removed, so nothing else may be ahead of this one.
    clearSnackBars();
    removeCurrentSnackBar();
    final controller = super.showSnackBar(snackBar);
    removeCurrentSnackBar();
    return controller;
  }
}
