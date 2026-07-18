import 'package:flutter/material.dart';

import '../../models/sequence/sequence_summary.dart';
import '../../theme/ara_colors.dart';

/// Small coloured chip for a sequence's current run state; nothing for
/// idle/none. Promoted from the Load dialog (Run-redesign S1) — the run
/// SURFACE is where state colour matters most, so the mapping lives in one
/// shared widget: running/starting amber, paused blue, needs-attention /
/// failed / aborting red, stopped/completed neutral.
class RunStateBadge extends StatelessWidget {
  const RunStateBadge(this.state, {super.key});
  final SequenceRunState? state;

  /// The badge's state→colour mapping, exposed so sibling widgets (progress
  /// band, tree spotlight) colour consistently. Exhaustive (no wildcard) so a
  /// new SequenceRunState forces a compile error rather than silently
  /// defaulting — matching SequenceRunState.isActive.
  static Color colorFor(SequenceRunState s) => switch (s) {
        SequenceRunState.running ||
        SequenceRunState.starting =>
          AraColors.accentBusy,
        SequenceRunState.paused => AraColors.accentInfo,
        // §58.12 awaiting-user reads as urgent (the rig needs a human), not as
        // a leisurely operator pause — error red, though the run is resumable.
        SequenceRunState.pausedAwaitingUser ||
        SequenceRunState.failed ||
        SequenceRunState.aborting =>
          AraColors.accentError,
        SequenceRunState.stopped ||
        SequenceRunState.completed =>
          AraColors.textSecondary,
        SequenceRunState.idle => AraColors.textSecondary,
      };

  /// Human label — the §58.12 multi-word state needs product copy
  /// ("pausedAwaitingUser" is not UI).
  static String labelFor(SequenceRunState s) =>
      s == SequenceRunState.pausedAwaitingUser ? 'needs attention' : s.name;

  @override
  Widget build(BuildContext context) {
    final s = state;
    if (s == null || s == SequenceRunState.idle) return const SizedBox.shrink();
    final color = colorFor(s);
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.18),
        borderRadius: BorderRadius.circular(4),
        border: Border.all(color: color),
      ),
      child: Text(labelFor(s),
          style:
              TextStyle(color: color, fontSize: 11, fontWeight: FontWeight.w600)),
    );
  }
}
