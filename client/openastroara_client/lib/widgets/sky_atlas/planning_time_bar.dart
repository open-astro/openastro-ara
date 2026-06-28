import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/sky_atlas/planning_time_state.dart';
import '../../theme/ara_colors.dart';

/// §36 Planning — the planetarium time control. Pin the sky to a chosen instant
/// (e.g. tonight) instead of the live clock, so you can see what's up later
/// rather than the current daytime sky. Tap-friendly for remote/VNC use.
class PlanningTimeBar extends ConsumerWidget {
  const PlanningTimeBar({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final pinned = ref.watch(planningTimeProvider);
    final notifier = ref.read(planningTimeProvider.notifier);
    final live = pinned == null;

    return Material(
      color: AraColors.bgPanel,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
        child: Row(
          children: [
            Icon(live ? Icons.schedule : Icons.history_toggle_off,
                size: 18, color: live ? AraColors.accentConnected : AraColors.accentBusy),
            const SizedBox(width: 8),
            Text(
              live ? 'Live sky' : _fmt(pinned.toLocal()),
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                  color: AraColors.textPrimary,
                  fontFeatures: const [FontFeature.tabularFigures()]),
            ),
            const Spacer(),
            _Btn('−1h', () => notifier.shift(const Duration(hours: -1))),
            const SizedBox(width: 4),
            _Btn('+1h', () => notifier.shift(const Duration(hours: 1))),
            const SizedBox(width: 12),
            _Btn('Tonight', notifier.tonight, filled: true),
            const SizedBox(width: 4),
            // "Now" returns to the live clock; disabled when already live.
            _Btn('Now', live ? null : notifier.setNow),
          ],
        ),
      ),
    );
  }

  static String _fmt(DateTime t) {
    const months = [
      'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun',
      'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'
    ];
    final hh = t.hour.toString().padLeft(2, '0');
    final mm = t.minute.toString().padLeft(2, '0');
    return '${months[t.month - 1]} ${t.day}, $hh:$mm';
  }
}

class _Btn extends StatelessWidget {
  const _Btn(this.label, this.onPressed, {this.filled = false});
  final String label;
  final VoidCallback? onPressed;
  final bool filled;

  @override
  Widget build(BuildContext context) {
    final child = Text(label);
    const pad = EdgeInsets.symmetric(horizontal: 10, vertical: 2);
    return filled
        ? FilledButton(
            onPressed: onPressed,
            style: FilledButton.styleFrom(padding: pad, minimumSize: const Size(0, 32)),
            child: child)
        : OutlinedButton(
            onPressed: onPressed,
            style: OutlinedButton.styleFrom(padding: pad, minimumSize: const Size(0, 32)),
            child: child);
  }
}
