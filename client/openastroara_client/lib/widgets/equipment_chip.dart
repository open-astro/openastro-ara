import 'package:flutter/material.dart';

import '../theme/ara_colors.dart';
import 'status_indicator.dart';

/// One chip in the §25.3 top equipment bar — icon + small-caps device label
/// + colored status dot. Tap = open chooser; long-press = disconnect /
/// properties. Per-device wiring lands in Phase 12c (Imaging tab + chooser
/// flows); this widget is the visual primitive used everywhere.
///
/// The dot's shape carries meaning too: [StatusLevel.busy] (an exposure, a
/// slew, a settle) is an amber RING; every other level is a solid dot, so a
/// standing [StatusLevel.warning] (solid amber) is told apart from a device
/// that is merely working. On 2026-10-16 the two were one dot.
class EquipmentChip extends StatelessWidget {
  final IconData icon;
  final String label;
  final StatusLevel status;
  final VoidCallback? onTap;
  final VoidCallback? onLongPress;

  /// What the dot means right now ("degraded: … EFW_ERROR_REMOVED", "Not
  /// guiding while a sequence runs"); shown on hover / long-press and read by
  /// screen readers. Null = nothing to explain.
  final String? tooltip;

  const EquipmentChip({
    super.key,
    required this.icon,
    required this.label,
    required this.status,
    this.onTap,
    this.onLongPress,
    this.tooltip,
  });

  /// `busy` is activity: an amber ring. Everything else is a solid dot.
  static bool isRing(StatusLevel status) => status == StatusLevel.busy;

  @override
  Widget build(BuildContext context) {
    final ring = isRing(status);
    final chip = Semantics(
      label: tooltip == null
          ? '$label (${status.name})'
          : '$label (${status.name}): $tooltip',
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
                      key: ValueKey('chip-dot-$label-${status.name}'),
                      width: 10,
                      height: 10,
                      decoration: BoxDecoration(
                        color: ring ? AraColors.bgPanel : status.color,
                        shape: BoxShape.circle,
                        border: Border.all(
                            color: ring ? status.color : AraColors.bgPanel,
                            width: 2),
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
    final message = tooltip;
    return message == null ? chip : Tooltip(message: message, child: chip);
  }
}
