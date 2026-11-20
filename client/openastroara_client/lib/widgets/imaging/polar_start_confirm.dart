import 'package:flutter/material.dart';

import '../../l10n/l10n.dart';
import '../../models/polar_align.dart';
import '../../services/polar_align_api.dart';
import '../adaptive_dialog.dart';

/// #2048 — Polar Align's confirm step, the one way the app starts the routine
/// (Full UI and Simple Mode alike). The routine starts from where the mount
/// points, like NINA's Three Point Polar Alignment, and never sends it Home or
/// slews it; so before the mount moves the user sees where to point it and
/// which way its RA axis will turn, and confirms. The rig reads the direction
/// from the mount ([PolarAlignClient.startPlan]) and refuses a start that does
/// not carry it back ([PolarAlignClient.start]). Nothing starts when the user
/// cancels. A parked mount is refused by the rig before the dialog opens.
Future<void> confirmAndStartPolarAlign(
  BuildContext context,
  PolarAlignClient api, {
  String? stage,
}) async {
  final plan = await api.startPlan(stage: stage);
  if (!context.mounted) return;
  final confirmed = await showAdaptiveAlert<bool>(
    context: context,
    builder: (_) => PolarStartConfirmDialog(plan: plan),
  );
  if (confirmed != true) return;
  await api.start(plan);
}

/// The confirm step's dialog: where to point the mount, the RA turns and
/// their direction, the most the run turns, and the hand on the power switch.
/// Pops true for "Move the mount", false for Cancel.
class PolarStartConfirmDialog extends StatelessWidget {
  const PolarStartConfirmDialog({super.key, required this.plan});

  final PolarAlignStartPlan plan;

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final direction = plan.direction == PolarAlignDirections.west
        ? l10n.polarDirectionWest
        : l10n.polarDirectionEast;
    String deg(double v) => v.toStringAsFixed(0);
    final offsetDirection = plan.originOffsetDeg < 0
        ? l10n.polarDirectionWest
        : l10n.polarDirectionEast;
    // #2089 — "these turns go back toward it" only when they do: within 2° of
    // the origin the rig turns the origin's first way, which can be away.
    final turnsBack =
        plan.originOffsetDeg.abs() >= 0.5 &&
        (plan.direction == PolarAlignDirections.west) ==
            (plan.originOffsetDeg > 0);
    final lines = [
      l10n.polarConfirmPoint,
      l10n.polarConfirmTurns(
        deg(plan.stepDeg),
        direction,
        deg(plan.stepDeg * plan.turns),
      ),
      // #2089 — where this run stands against the session's first start.
      if (plan.newOrigin)
        l10n.polarConfirmNewOrigin(deg(plan.sweepLimitDeg))
      else if (turnsBack)
        l10n.polarConfirmFromOrigin(
          deg(plan.originOffsetDeg.abs()),
          offsetDirection,
        ),
      // A check-only run has no later step to turn back.
      if (plan.stage != PolarAlignStages.check)
        l10n.polarConfirmBand(deg(plan.maxSweepDeg)),
      l10n.polarConfirmPower,
    ];
    return adaptiveAlertDialog(
      icon: const Icon(Icons.warning_amber_rounded),
      title: Text(l10n.polarConfirmTitle),
      content: Column(
        key: const Key('polar-start-confirm'),
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          for (final line in lines)
            Padding(
              padding: const EdgeInsets.only(bottom: 8),
              child: Text(line),
            ),
        ],
      ),
      actions: [
        adaptiveDialogAction(
          context,
          key: const Key('polar-start-confirm-cancel'),
          onPressed: () => Navigator.of(context).pop(false),
          child: Text(l10n.polarConfirmCancel),
        ),
        adaptiveDialogAction(
          context,
          key: const Key('polar-start-confirm-go'),
          isDefault: true,
          filled: true,
          onPressed: () => Navigator.of(context).pop(true),
          child: Text(l10n.polarConfirmGo),
        ),
      ],
    );
  }
}
