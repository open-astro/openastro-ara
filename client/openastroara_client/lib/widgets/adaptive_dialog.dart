import 'package:flutter/cupertino.dart';
import 'package:flutter/material.dart';

/// An alert dialog in the theme's platform look: the iOS alert on iPhone and
/// iPad, Material elsewhere. Open it with `showAdaptiveDialog`. The content
/// sits on a transparent [Material] because the iOS alert has none, and the
/// buttons and fields inside need one.
Widget adaptiveAlertDialog({
  Widget? icon,
  Widget? title,
  Widget? content,
  List<Widget>? actions,
  bool scrollable = false,
  Color? backgroundColor,
}) => AlertDialog.adaptive(
  icon: icon,
  title: title,
  content: content == null
      ? null
      : Material(type: MaterialType.transparency, child: content),
  actions: actions,
  scrollable: scrollable,
  backgroundColor: backgroundColor,
);

/// One action of an [adaptiveAlertDialog]: the iOS dialog action on iOS
/// (bold when [isDefault], red when [isDestructive]), else a text button, or a
/// filled one when [filled] (red when [isDestructive]).
Widget adaptiveDialogAction(
  BuildContext context, {
  required VoidCallback? onPressed,
  required Widget child,
  Key? key,
  bool isDefault = false,
  bool isDestructive = false,
  bool filled = false,
}) {
  final ios = switch (Theme.of(context).platform) {
    TargetPlatform.iOS || TargetPlatform.macOS => true,
    _ => false,
  };
  if (ios) {
    return CupertinoDialogAction(
      key: key,
      onPressed: onPressed,
      isDefaultAction: isDefault,
      isDestructiveAction: isDestructive,
      child: child,
    );
  }
  final error = Theme.of(context).colorScheme.error;
  if (filled) {
    return FilledButton(
      key: key,
      style: isDestructive
          ? FilledButton.styleFrom(backgroundColor: error)
          : null,
      onPressed: onPressed,
      child: child,
    );
  }
  return TextButton(
    key: key,
    style: isDestructive ? TextButton.styleFrom(foregroundColor: error) : null,
    onPressed: onPressed,
    child: child,
  );
}
