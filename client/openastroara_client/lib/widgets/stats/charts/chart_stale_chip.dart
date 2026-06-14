import 'package:flutter/material.dart';

import '../../../theme/ara_colors.dart';

/// Compact "Stale" indicator overlaid on a §50 chart when a manual refresh
/// failed but the previous data is still shown. Charts have no header row for a
/// banner (unlike the tile sections), so they use this chip instead.
class ChartStaleChip extends StatelessWidget {
  const ChartStaleChip({super.key, required this.tooltip});

  /// The hover tooltip, e.g. "Couldn’t refresh — showing the last loaded …".
  final String tooltip;

  @override
  Widget build(BuildContext context) {
    return Tooltip(
      message: tooltip,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
        decoration: BoxDecoration(
          color: AraColors.bgPanel,
          borderRadius: BorderRadius.circular(4),
          border: Border.all(color: AraColors.accentBusy, width: 0.5),
        ),
        child: const Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.sync_problem, size: 12, color: AraColors.accentBusy),
            SizedBox(width: 4),
            Text('Stale',
                style: TextStyle(fontSize: 10, color: AraColors.accentBusy)),
          ],
        ),
      ),
    );
  }
}
