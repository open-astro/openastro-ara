import 'package:flutter/material.dart';

import '../../theme/ara_colors.dart';

/// One Overview tile on the §50 Stats dashboard — large value + caption + icon.
/// Width comes from the parent (the sections lay tiles out in a
/// [ResponsiveTileGrid], which sizes every tile to share the row).
class StatTile extends StatelessWidget {
  final IconData icon;
  final String value;
  final String label;
  final String? subtitle;

  const StatTile({
    super.key,
    required this.icon,
    required this.value,
    required this.label,
    this.subtitle,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: AraColors.bgPanel,
        border: Border.all(color: AraColors.border),
        borderRadius: BorderRadius.circular(6),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(icon, size: 18, color: AraColors.textSecondary),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  label,
                  overflow: TextOverflow.ellipsis,
                  style: Theme.of(context).textTheme.labelMedium?.copyWith(
                        color: AraColors.textSecondary,
                      ),
                ),
              ),
            ],
          ),
          const SizedBox(height: 8),
          // One line each, and the subtitle's line kept when there is none,
          // so every tile in a grid is the same size.
          Text(
            value,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: Theme.of(context).textTheme.headlineMedium?.copyWith(
                  color: AraColors.textPrimary,
                  fontWeight: FontWeight.w600,
                ),
          ),
          Padding(
            padding: const EdgeInsets.only(top: 4),
            child: Text(
              subtitle ?? '',
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                    color: AraColors.textSecondary,
                  ),
            ),
          ),
        ],
      ),
    );
  }
}

/// One figure in the Stats dashboard's list (user, 2026-11-13, mock
/// `design/preview/stats-layouts-mock.html` B): the label at the left, the
/// value at the right, a note under them when there is one. Denser than a
/// [StatTile]: a row of these takes the height one tile's value took.
class StatKpi extends StatelessWidget {
  final String label;
  final String value;
  final String? subtitle;

  const StatKpi({
    super.key,
    required this.label,
    required this.value,
    this.subtitle,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context).textTheme;
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
      decoration: BoxDecoration(
        color: AraColors.bgPanel,
        border: Border.all(color: AraColors.border),
        borderRadius: BorderRadius.circular(6),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.baseline,
            textBaseline: TextBaseline.alphabetic,
            children: [
              Expanded(
                child: Text(
                  label,
                  overflow: TextOverflow.ellipsis,
                  style: theme.labelMedium?.copyWith(
                    color: AraColors.textSecondary,
                  ),
                ),
              ),
              const SizedBox(width: 8),
              Text(
                value,
                maxLines: 1,
                style: theme.titleMedium?.copyWith(
                  color: AraColors.textPrimary,
                  fontWeight: FontWeight.w600,
                  fontFeatures: const [FontFeature.tabularFigures()],
                ),
              ),
            ],
          ),
          if (subtitle != null)
            Text(
              subtitle!,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: theme.bodySmall?.copyWith(color: AraColors.textDisabled),
            ),
        ],
      ),
    );
  }
}

/// [StatKpi]s in [columns] equal columns, each row as tall as its tallest.
class StatKpiGrid extends StatelessWidget {
  final List<Widget> children;
  final int columns;

  const StatKpiGrid({super.key, required this.children, this.columns = 2});

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    spacing: 8,
    children: [
      for (var i = 0; i < children.length; i += columns)
        IntrinsicHeight(
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            spacing: 8,
            children: [
              for (var j = i; j < i + columns; j++)
                Expanded(
                  child: j < children.length
                      ? children[j]
                      : const SizedBox.shrink(),
                ),
            ],
          ),
        ),
    ],
  );
}
