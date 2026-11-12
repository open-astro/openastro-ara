/// A long-press menu in iOS's form (DESIGN.md, Platform gestures): on iOS and iPadOS a
/// [CupertinoContextMenu] that lifts [child] as its own preview, which takes no touches; elsewhere
/// [child] as it is.
library;

import 'package:flutter/cupertino.dart';
import 'package:flutter/material.dart';

import '../util/reduce_motion.dart';

/// One action of a [PlatformContextMenu].
class ContextMenuItem {
  const ContextMenuItem(
    this.label,
    this.icon,
    this.onPressed, {
    this.destructive = false,
  });
  final String label;
  final IconData icon;
  final VoidCallback onPressed;
  final bool destructive;
}

class PlatformContextMenu extends StatelessWidget {
  const PlatformContextMenu({
    super.key,
    required this.items,
    required this.child,
  });

  /// Empty: no menu.
  final List<ContextMenuItem> items;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    if (items.isEmpty || Theme.of(context).platform != TargetPlatform.iOS) {
      return child;
    }
    // The menu zooms the preview up; under Reduce Motion a long press opens the same actions
    // as a plain sheet instead.
    if (reduceMotionOf(context)) {
      return GestureDetector(
        behavior: HitTestBehavior.translucent,
        onLongPress: () => showCupertinoModalPopup<void>(
          context: context,
          builder: (sheet) => CupertinoActionSheet(
            actions: [
              for (final i in items)
                CupertinoActionSheetAction(
                  isDestructiveAction: i.destructive,
                  onPressed: () {
                    Navigator.of(sheet).pop();
                    // The item may have gone while the sheet was up.
                    if (context.mounted) i.onPressed();
                  },
                  child: Text(i.label),
                ),
            ],
            cancelButton: CupertinoActionSheetAction(
              onPressed: () => Navigator.of(sheet).pop(),
              child: Text(MaterialLocalizations.of(sheet).cancelButtonLabel),
            ),
          ),
        ),
        child: child,
      );
    }
    // The preview is laid out loose in the overlay: give it the size it has in the page, or a
    // child that fills its slot (a Card) has no height to fill.
    return LayoutBuilder(
      builder: (context, box) {
        // Read now: the item may be gone (removed elsewhere) by the time an action is tapped.
        final menu = Navigator.of(context, rootNavigator: true);
        final page = ModalRoute.of(context);
        return CupertinoContextMenu.builder(
          actions: [
            for (final i in items)
              CupertinoContextMenuAction(
                isDestructiveAction: i.destructive,
                trailingIcon: i.icon,
                onPressed: () {
                  menu.pop();
                  if (context.mounted) i.onPressed();
                },
                child: Text(i.label),
              ),
          ],
          builder: (lifted, animation) => SizedBox(
            width: box.hasTightWidth ? box.maxWidth : null,
            height: box.hasTightHeight ? box.maxHeight : null,
            // The overlay is outside any Scaffold: ink needs its own Material.
            child: Material(
              type: MaterialType.transparency,
              // Only the item in the page takes touches. The lifted copy is a preview, not a
              // second control: a swipe or a button on it would act under the open menu.
              child: AbsorbPointer(
                absorbing: ModalRoute.of(lifted) != page,
                // A row in a list has no tight height; the preview shrinks to its content.
                child: box.hasTightHeight
                    ? child
                    : IntrinsicHeight(child: child),
              ),
            ),
          ),
        );
      },
    );
  }
}
