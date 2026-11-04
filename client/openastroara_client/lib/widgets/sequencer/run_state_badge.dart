import 'package:flutter/material.dart';

import '../../models/sequence/sequence_summary.dart';
import '../../theme/ara_colors.dart';

/// Small coloured chip for a sequence's current run state; nothing for
/// idle/none. Promoted from the Load dialog (Run-redesign S1) — the run
/// SURFACE is where state colour matters most, so the mapping lives in one
/// shared widget, per the DESIGN.md status rule: running green, starting and
/// paused blue, needs-attention amber, failed / aborting red,
/// stopped/completed neutral.
class RunStateBadge extends StatelessWidget {
  const RunStateBadge(this.state, {super.key});
  final SequenceRunState? state;

  /// The badge's state→colour mapping, exposed so sibling widgets (progress
  /// band, tree spotlight) colour consistently. Exhaustive (no wildcard) so a
  /// new SequenceRunState forces a compile error rather than silently
  /// defaulting — matching SequenceRunState.isActive.
  static Color colorFor(SequenceRunState s) => switch (s) {
        // DESIGN.md status rule: running is working (green), starting is on
        // its way (blue).
        SequenceRunState.running => AraColors.accentConnected,
        SequenceRunState.starting => AraColors.accentInfo,
        SequenceRunState.paused => AraColors.accentInfo,
        // DESIGN.md status rule: awaiting the user needs a look, nothing has
        // failed (the run resumes): amber, as Simple Mode's plan pill.
        SequenceRunState.pausedAwaitingUser => AraColors.accentBusy,
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
