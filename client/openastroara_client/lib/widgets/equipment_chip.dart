import 'package:flutter/material.dart';

import '../theme/ara_colors.dart';
import 'status_indicator.dart';

/// One chip in the §25.3 top equipment bar — icon + small-caps device label
/// + colored status dot. Tap = open chooser; long-press = disconnect /
/// properties. Per-device wiring lands in Phase 12c (Imaging tab + chooser
/// flows); this widget is the visual primitive used everywhere.
class EquipmentChip extends StatelessWidget {
  final IconData icon;
  final String label;
  final StatusLevel status;
  final VoidCallback? onTap;
  final VoidCallback? onLongPress;

  const EquipmentChip({
    super.key,
    required this.icon,
    required this.label,
    required this.status,
    this.onTap,
    this.onLongPress,
  });

  @override
  Widget build(BuildContext context) {
    return Semantics(
      label: '$label (${status.name})',
      // Either tap or long-press makes this chip interactive.
      button: onTap != null || onLongPress != null,
      child: InkWell(
        onTap: onTap,
        onLongPress: onLongPress,
        borderRadius: BorderRadius.circular(6),
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 6),
          width: 64,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Stack(
                clipBehavior: Clip.none,
                children: [
                  Icon(icon, size: 28, color: AraColors.textPrimary),
                  Positioned(
                    top: -2,
                    right: -2,
                    child: Container(
                      width: 10,
                      height: 10,
                      decoration: BoxDecoration(
                        color: status.color,
                        shape: BoxShape.circle,
                        border: Border.all(color: AraColors.bgPanel, width: 2),
                      ),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 2),
              Text(
                label.toUpperCase(),
                style: Theme.of(context).textTheme.labelSmall?.copyWith(
                      color: AraColors.textSecondary,
                      letterSpacing: 0.5,
                    ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
