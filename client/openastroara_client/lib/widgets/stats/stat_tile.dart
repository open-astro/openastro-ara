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
