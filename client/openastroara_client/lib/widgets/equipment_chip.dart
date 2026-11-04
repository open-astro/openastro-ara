import 'package:flutter/material.dart';

import '../theme/ara_colors.dart';
import 'status_indicator.dart';

/// One chip in the §25.3 top equipment bar — icon + device name + colored
/// status dot. On a phone (shortest side below [kEquipmentChipCompactWidth],
/// either orientation) the name is dropped and lives on as the tooltip. Tap = open chooser; long-press = disconnect /
/// properties. Per-device wiring lands in Phase 12c (Imaging tab + chooser
/// flows); this widget is the visual primitive used everywhere.
const double kEquipmentChipCompactWidth = 600;

/// Every chip is this wide whatever its name, so the icons sit evenly spaced;
/// a longer name scales down to fit rather than widening it.
const double kEquipmentChipWidth = 76;
const double kEquipmentChipCompactChipWidth = 48;

/// Forces the chips below it to show their icons only (or names), overriding
/// the phone rule: the top bar sets it from the room it actually has, so an
/// iPad in portrait drops the names instead of pushing chips off screen.
class EquipmentChipCompact extends InheritedWidget {
  const EquipmentChipCompact({
    super.key,
    required this.compact,
    required super.child,
  });

  final bool compact;

  static bool? maybeOf(BuildContext context) => context
      .dependOnInheritedWidgetOfExactType<EquipmentChipCompact>()
      ?.compact;

  @override
  bool updateShouldNotify(EquipmentChipCompact oldWidget) =>
      compact != oldWidget.compact;
}

/// Under it, chips for devices that aren't connected draw nothing: Simple
/// Mode's strip shows only the gear that's actually there.
class EquipmentChipHideDisconnected extends InheritedWidget {
  const EquipmentChipHideDisconnected({super.key, required super.child});

  static bool of(BuildContext context) =>
      context
          .dependOnInheritedWidgetOfExactType<
            EquipmentChipHideDisconnected
          >() !=
      null;

  @override
  bool updateShouldNotify(EquipmentChipHideDisconnected oldWidget) => false;
}

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
    if (status == StatusLevel.disconnected &&
        EquipmentChipHideDisconnected.of(context)) {
      return const SizedBox.shrink();
    }
    // shortestSide, not width: a phone in landscape is wide enough for the
    // desktop layout but not for eleven named chips plus the top-bar icons.
    final compact =
        EquipmentChipCompact.maybeOf(context) ??
        MediaQuery.sizeOf(context).shortestSide < kEquipmentChipCompactWidth;
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
            width: compact
                ? kEquipmentChipCompactChipWidth
                : kEquipmentChipWidth,
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
class _StatusDot extends StatelessWidget {
  final StatusLevel status;
  const _StatusDot({required this.status});

  @override
  Widget build(BuildContext context) => StatusBlink(
    key: const ValueKey('equipment-chip-dot'),
    on: status == StatusLevel.active,
    child: Container(
      width: 10,
      height: 10,
      decoration: BoxDecoration(
        color: status.color,
        shape: BoxShape.circle,
        border: Border.all(color: AraColors.bgPanel, width: 2),
      ),
    ),
  );
}
