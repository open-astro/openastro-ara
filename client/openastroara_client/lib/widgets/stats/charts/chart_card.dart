import 'package:flutter/material.dart';

import '../../../theme/ara_colors.dart';

/// Common card wrapper for §50 chart visualizations — fixed height, title +
/// subtitle row at the top, chart body filling the rest. Phase 12g.2 uses
/// this for all four charts so they share padding + border treatment.
class ChartCard extends StatelessWidget {
  final String title;
  final String subtitle;
  final Widget child;
  final double height;

  const ChartCard({
    super.key,
    required this.title,
    required this.subtitle,
    required this.child,
    this.height = 240,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      height: height,
      margin: const EdgeInsets.only(bottom: 12),
      decoration: BoxDecoration(
        color: AraColors.bgPanel,
        border: Border.all(color: AraColors.border),
        borderRadius: BorderRadius.circular(6),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 0),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(title, style: Theme.of(context).textTheme.titleSmall),
                const SizedBox(height: 2),
                Text(
                  subtitle,
                  style: Theme.of(context).textTheme.bodySmall?.copyWith(
                        color: AraColors.textSecondary,
                      ),
                ),
              ],
            ),
          ),
          Expanded(child: child),
        ],
      ),
    );
  }
}
