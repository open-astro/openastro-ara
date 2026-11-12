/// Pull to refresh in the platform's own form (DESIGN.md, Platform gestures): the
/// Cupertino sliver on iOS and iPadOS, Material's [RefreshIndicator] elsewhere.
library;

import 'package:flutter/cupertino.dart';
import 'package:flutter/material.dart';

/// A scroll view of [slivers] that calls [onRefresh] when pulled down. The view always
/// scrolls, so the gesture works on a list that is empty or short.
class AdaptiveRefresh extends StatelessWidget {
  const AdaptiveRefresh({
    super.key,
    required this.onRefresh,
    required this.slivers,
  });

  final Future<void> Function() onRefresh;
  final List<Widget> slivers;

  @override
  Widget build(BuildContext context) {
    const physics = AlwaysScrollableScrollPhysics();
    // The same platform check as simple_queue_screen.dart; the control animates nothing
    // beyond the stock spinner, so Reduce Motion needs no branch of its own.
    return switch (Theme.of(context).platform) {
      TargetPlatform.iOS => CustomScrollView(
        physics: physics,
        slivers: [
          CupertinoSliverRefreshControl(onRefresh: onRefresh),
          ...slivers,
        ],
      ),
      _ => RefreshIndicator(
        onRefresh: onRefresh,
        child: CustomScrollView(physics: physics, slivers: slivers),
      ),
    };
  }
}
