import 'package:flutter/material.dart';

import '../theme/ara_colors.dart';
import 'status_indicator.dart';

/// One chip in the §25.3 top equipment bar — icon + device name + colored
/// status dot. Below [kEquipmentChipCompactWidth] (phones) the name is dropped
/// and lives on as the tooltip. Tap = open chooser; long-press = disconnect /
/// properties. Per-device wiring lands in Phase 12c (Imaging tab + chooser
/// flows); this widget is the visual primitive used everywhere.
const double kEquipmentChipCompactWidth = 600;

/// Every chip is this wide whatever its name, so the icons sit evenly spaced;
/// a longer name scales down to fit rather than widening it.
const double kEquipmentChipWidth = 76;
const double kEquipmentChipCompactChipWidth = 48;

class EquipmentChip extends StatelessWidget {
  final IconData icon;
  /// Drawn instead of [icon] for a device Material Icons has no glyph for.
  final Widget? glyph;
  final String label;
  final StatusLevel status;
  final VoidCallback? onTap;
  final VoidCallback? onLongPress;

  const EquipmentChip({
    super.key,
    required this.icon,
    this.glyph,
    required this.label,
    required this.status,
    this.onTap,
    this.onLongPress,
  });

  @override
  Widget build(BuildContext context) {
    final compact = MediaQuery.sizeOf(context).width < kEquipmentChipCompactWidth;
    return Semantics(
      label: '$label (${status.name})',
      // Either tap or long-press makes this chip interactive.
      button: onTap != null || onLongPress != null,
      child: Tooltip(
       message: label,
       child: InkWell(
        onTap: onTap,
        onLongPress: onLongPress,
        borderRadius: BorderRadius.circular(6),
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 6),
          width: compact ? kEquipmentChipCompactChipWidth : kEquipmentChipWidth,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Stack(
                clipBehavior: Clip.none,
                children: [
                  glyph ?? Icon(icon, size: 28, color: AraColors.textPrimary),
                  Positioned(
                    top: -2,
                    right: -2,
                    child: _StatusDot(status: status),
                  ),
                ],
              ),
              if (!compact) ...[
                const SizedBox(height: 2),
                FittedBox(
                  fit: BoxFit.scaleDown,
                  child: Text(
                    label,
                    maxLines: 1,
                    style: Theme.of(context).textTheme.labelSmall?.copyWith(
                          color: AraColors.textSecondary,
                          letterSpacing: 0.5,
                        ),
                  ),
                ),
              ],
            ],
          ),
        ),
       ),
      ),
    );
  }
}

/// The chip's status dot: steady, or blinking while the device is
/// [StatusLevel.active] (working in a good state).
class _StatusDot extends StatefulWidget {
  final StatusLevel status;
  const _StatusDot({required this.status});

  @override
  State<_StatusDot> createState() => _StatusDotState();
}

class _StatusDotState extends State<_StatusDot> with SingleTickerProviderStateMixin {
  late final AnimationController _blink =
      AnimationController(vsync: this, duration: const Duration(milliseconds: 700), lowerBound: 0.25);

  @override
  void initState() {
    super.initState();
    _sync();
  }

  @override
  void didUpdateWidget(_StatusDot old) {
    super.didUpdateWidget(old);
    _sync();
  }

  void _sync() {
    if (widget.status == StatusLevel.active) {
      if (!_blink.isAnimating) _blink.repeat(reverse: true);
    } else {
      _blink
        ..stop()
        ..value = 1;
    }
  }

  @override
  void dispose() {
    _blink.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => FadeTransition(
        key: const ValueKey('equipment-chip-dot'),
        opacity: _blink,
        child: Container(
          width: 10,
          height: 10,
          decoration: BoxDecoration(
            color: widget.status.color,
            shape: BoxShape.circle,
            border: Border.all(color: AraColors.bgPanel, width: 2),
          ),
        ),
      );
}
