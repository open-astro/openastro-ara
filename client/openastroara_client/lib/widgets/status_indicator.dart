import 'package:flutter/material.dart';

import '../theme/ara_colors.dart';

/// Health pill per playbook §51 + §53 a11y — always visible somewhere in the
/// app shell, summarizes overall daemon state with a single colored dot +
/// label. Tap opens the diagnostic panel (Phase 12 sub-PR follow-up).
class StatusIndicator extends StatelessWidget {
  final StatusLevel level;
  final String label;
  final VoidCallback? onTap;

  const StatusIndicator({
    super.key,
    required this.level,
    required this.label,
    this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return Semantics(
      label: 'Status: $label (${level.name})',
      button: onTap != null,
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              StatusDot.of(level),
              const SizedBox(width: 8),
              // Flexible so a long label ("1 issue — critical") shrinks with
              // ellipsis in a tight row instead of overflowing it.
              Flexible(
                child: Text(label,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: Theme.of(context).textTheme.bodySmall),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// A status dot. A changing one (blue, [ring]) is a ring, every other a
/// filled dot: in Night mode only brightness is left, and blue and green
/// land close (DESIGN.md status rule; `design/preview/night-status-cue-mock.html`
/// B). The ring is the level's, not the colour's: [StatusLevel.info] shares
/// the blue and stays filled. [outline] rims a filled dot in the surface
/// colour (the top-bar chips).
class StatusDot extends StatelessWidget {
  const StatusDot({
    super.key,
    required this.color,
    this.ring = false,
    this.size = 10,
    this.outline,
  });

  StatusDot.of(StatusLevel level, {super.key, this.size = 10, this.outline})
    : color = level.color,
      ring = level == StatusLevel.changing;

  final Color color;
  final bool ring;
  final double size;
  final Color? outline;

  @override
  Widget build(BuildContext context) {
    final changing = ring;
    return Container(
      width: size,
      height: size,
      decoration: BoxDecoration(
        color: changing ? null : color,
        shape: BoxShape.circle,
        border: changing
            ? Border.all(color: color, width: 2.5)
            : outline == null
            ? null
            : Border.all(color: outline!, width: 2),
      ),
    );
  }
}

enum StatusLevel {
  connected(AraColors.accentConnected),
  /// Connected and working (exposing, slewing, moving): green, shown blinking
  /// on the top-bar chips. Amber ([busy]) is kept for states that need a look.
  active(AraColors.accentConnected),
  /// Changing: on its way between two states (connecting, starting,
  /// calibrating, settling, cooling, parking). Blue (DESIGN.md status rule).
  changing(AraColors.accentInfo),
  /// A warning: needs a look, nothing has failed. Amber; an in-between state
  /// is [changing], never this (DESIGN.md status rule, 2026-11-04).
  busy(AraColors.accentBusy),
  error(AraColors.accentError),
  info(AraColors.accentInfo),
  disconnected(AraColors.accentDisconnected);

  final Color color;
  const StatusLevel(this.color);
}

/// Blinks its child while [on]: a device working normally (exposing,
/// slewing, moving, guiding) blinks its green status (DESIGN.md status
/// rule). Steady with Reduce Motion on.
class StatusBlink extends StatefulWidget {
  const StatusBlink({super.key, required this.on, required this.child});

  final bool on;
  final Widget child;

  @override
  State<StatusBlink> createState() => _StatusBlinkState();
}

class _StatusBlinkState extends State<StatusBlink>
    with SingleTickerProviderStateMixin {
  late final AnimationController _blink = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 700),
    lowerBound: 0.25,
    value: 1,
  );

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _sync();
  }

  @override
  void didUpdateWidget(StatusBlink old) {
    super.didUpdateWidget(old);
    _sync();
  }

  void _sync() {
    if (widget.on && !MediaQuery.disableAnimationsOf(context)) {
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
  Widget build(BuildContext context) =>
      FadeTransition(opacity: _blink, child: widget.child);
}
