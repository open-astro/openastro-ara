import 'package:flutter/widgets.dart';

/// True when the user asked the OS for less motion: Android's "Remove
/// animations" (`MediaQuery.disableAnimations`) or iOS Reduce Motion
/// (`AccessibilityFeatures.reduceMotion`, which MediaQuery does not carry).
/// Rebuilds the caller when either changes, given a [ReduceMotionScope] above
/// (the app root has one); without it the value is read once per build.
bool reduceMotionOf(BuildContext context) {
  final scope = context.dependOnInheritedWidgetOfExactType<_ReduceMotion>();
  final ios =
      scope?.reduceMotion ??
      View.of(context).platformDispatcher.accessibilityFeatures.reduceMotion;
  return ios || MediaQuery.disableAnimationsOf(context);
}

/// Listens for accessibility changes so [reduceMotionOf] callers rebuild on a
/// `reduceMotion`-only change, which MediaQuery's equality ignores.
class ReduceMotionScope extends StatefulWidget {
  const ReduceMotionScope({super.key, required this.child});
  final Widget child;

  @override
  State<ReduceMotionScope> createState() => _ReduceMotionScopeState();
}

class _ReduceMotionScopeState extends State<ReduceMotionScope>
    with WidgetsBindingObserver {
  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    super.dispose();
  }

  @override
  void didChangeAccessibilityFeatures() => setState(() {});

  @override
  Widget build(BuildContext context) => _ReduceMotion(
    reduceMotion: View.of(context)
        .platformDispatcher
        .accessibilityFeatures
        .reduceMotion,
    child: widget.child,
  );
}

class _ReduceMotion extends InheritedWidget {
  const _ReduceMotion({required this.reduceMotion, required super.child});
  final bool reduceMotion;

  @override
  bool updateShouldNotify(_ReduceMotion old) =>
      reduceMotion != old.reduceMotion;
}
