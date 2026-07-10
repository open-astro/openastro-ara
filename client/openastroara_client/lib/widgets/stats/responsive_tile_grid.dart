import 'dart:math' as math;

import 'package:flutter/material.dart';

/// §50 dashboard tile layout — a [Wrap] whose tiles share the available width
/// instead of sitting at a fixed 200 px. The column count comes from how many
/// [minTileWidth] tiles (plus [spacing]) fit; every tile in the grid then gets
/// the same computed width, so a row always spans the section edge-to-edge
/// with no ragged right-hand gap. On a narrow window this degrades to one
/// full-width column rather than clipping; on an ultra-wide one [maxTileWidth]
/// stops the tiles ballooning (the row then left-aligns like a plain Wrap).
class ResponsiveTileGrid extends StatelessWidget {
  final List<Widget> children;
  final double minTileWidth;
  final double maxTileWidth;
  final double spacing;

  const ResponsiveTileGrid({
    super.key,
    required this.children,
    this.minTileWidth = 180,
    this.maxTileWidth = 280,
    this.spacing = 12,
  });

  /// The shared tile width for [available] horizontal space. Exposed for
  /// tests: pure math, no layout pass needed.
  double tileWidthFor(double available) {
    if (!available.isFinite || available <= minTileWidth) {
      // Unbounded (shouldn't happen on the dashboard) or too narrow to fit
      // even one minimum tile: one column at whatever space there is, floored
      // at the minimum so the tile content lays out sanely and lets the
      // parent scroll/clip instead of squeezing to zero.
      return available.isFinite ? math.max(available, minTileWidth) : minTileWidth;
    }
    final columns =
        math.max(1, (available + spacing) ~/ (minTileWidth + spacing));
    final width = (available - (columns - 1) * spacing) / columns;
    return math.min(width, maxTileWidth);
  }

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(
      builder: (context, constraints) {
        final width = tileWidthFor(constraints.maxWidth);
        return Wrap(
          spacing: spacing,
          runSpacing: spacing,
          children: [
            for (final child in children) SizedBox(width: width, child: child),
          ],
        );
      },
    );
  }
}
