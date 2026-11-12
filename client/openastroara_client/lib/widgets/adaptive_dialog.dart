import 'package:flutter/cupertino.dart';
import 'package:flutter/material.dart';

import 'adaptive_controls.dart';

/// Opens an alert in the theme's platform look: `showAdaptiveDialog` (iOS
/// alert route) on iPhone and iPad, `showDialog` elsewhere, macOS included.
Future<T?> showAdaptiveAlert<T>({
  required BuildContext context,
  required WidgetBuilder builder,
}) => useCupertinoControls(context)
    ? showAdaptiveDialog<T>(context: context, builder: builder)
    : showDialog<T>(context: context, builder: builder);

/// An alert dialog in the theme's platform look: the iOS alert on iPhone and
/// iPad, Material elsewhere. Open it with [showAdaptiveAlert]. The content
/// sits on a transparent [Material] because the iOS alert has none, and the
/// buttons and fields inside need one.
Widget adaptiveAlertDialog({
  Widget? icon,
  Widget? title,
  Widget? content,
  List<Widget>? actions,
  bool scrollable = false,
  Color? backgroundColor,
}) {
  final wrapped = content == null
      ? null
      : Material(type: MaterialType.transparency, child: content);
  return Builder(
    builder: (context) => useCupertinoControls(context)
        ? AlertDialog.adaptive(
            icon: icon,
            title: title,
            content: wrapped,
            actions: actions,
            scrollable: scrollable,
            backgroundColor: backgroundColor,
          )
        : AlertDialog(
            icon: icon,
            title: title,
            content: wrapped,
            actions: actions,
            scrollable: scrollable,
            backgroundColor: backgroundColor,
          ),
  );
}

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
  if (useCupertinoControls(context)) {
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
