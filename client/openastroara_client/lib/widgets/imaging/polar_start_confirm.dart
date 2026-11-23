import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import '../../l10n/l10n.dart';
import '../../models/polar_align.dart';
import '../../services/polar_align_api.dart';
import '../../services/refusal.dart';
import '../adaptive_dialog.dart';

/// #2048 — Polar Align's confirm step, the one way the app starts the routine
/// (Full UI and Simple Mode alike). The routine starts from where the mount
/// points, like NINA's Three Point Polar Alignment, and never sends it Home or
/// slews it; so before the mount moves the user sees where to point it and
/// which way its RA axis will turn, and confirms. The rig reads the direction
/// from the mount ([PolarAlignClient.startPlan]) and refuses a start that does
/// not carry it back ([PolarAlignClient.start]). Nothing starts when the user
/// cancels. A parked mount is refused by the rig before the dialog opens.
/// #2170 — RA always turns with the mount's motor: there is no hand-turn
/// choice.
///
/// #2081 — a mount whose RA motor direction is not saved for its driver first
/// turns 0.5° to learn it: the dialog says so, and the start confirms it.
///
/// #2068 — while the mount's clearance is not confirmed the rig refuses the
/// plan, and that refusal reaches the caller: no dialog opens.
///
/// #2165 — with [confirmClearance] the step instead asks the clearance
/// question for the refusal's `reach_deg`, the rig's reach for this stage (a
/// Check turns by the Check step, which can be bigger than the turn size the
/// Start card's list confirmed), and once that is saved asks the rig for the
/// plan again. Cancel there just closes: nothing starts. When it cannot ask
/// ([confirmClearance] returns null) the refusal reaches the caller.
Future<void> confirmAndStartPolarAlign(
  BuildContext context,
  PolarAlignClient api, {
  String? stage,
  Future<bool?> Function(double reachDeg)? confirmClearance,
}) async {
  PolarAlignStartPlan plan;
  try {
    plan = await api.startPlan(stage: stage);
  } on Refusal catch (refusal) {
    final reach = refusal.parameters['reach_deg'];
    if (refusal.cause != RefusalCause.polarAlignClearanceNotConfirmed ||
        confirmClearance == null ||
        reach is! num ||
        !context.mounted) {
      rethrow;
    }
    final confirmed = await confirmClearance(reach.toDouble());
    if (confirmed == null) rethrow;
    if (!confirmed) return;
    plan = await api.startPlan(stage: stage);
  }
  if (!context.mounted) return;
  final go = await showAdaptiveAlert<bool>(
    context: context,
    builder: (_) => PolarStartConfirmDialog(plan: plan),
  );
  if (go == true) await api.start(plan);
}

/// #2166 the motion confirm before "Move to start position": the Dec axis
/// alone turns until the scope points [PolarAlignMoveToStartOffer.targetOffPoleDeg]
/// from the pole, a small probe first, the RA axis and counterweights stay;
/// the hand on the power switch. Pops true for "Move the mount", null for
/// Cancel. Nothing moves before it pops true.
class PolarMoveToStartConfirmDialog extends StatelessWidget {
  const PolarMoveToStartConfirmDialog({super.key, required this.offer});

  final PolarAlignMoveToStartOffer offer;

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final format = NumberFormat('0.#', Localizations.localeOf(context).toString());
    return adaptiveAlertDialog(
      scrollable: true,
      icon: const Icon(Icons.warning_amber_rounded),
      title: Text(l10n.polarMoveToStartTitle),
      content: Column(
        key: const Key('polar-move-to-start-confirm'),
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Padding(
            padding: const EdgeInsets.only(bottom: 8),
            child: Text(
              l10n.polarMoveToStartConfirmBody(format.format(offer.targetOffPoleDeg)),
            ),
          ),
          Text(l10n.polarConfirmPower),
        ],
      ),
      actions: [
        adaptiveDialogAction(
          context,
          key: const Key('polar-move-to-start-confirm-cancel'),
          onPressed: () => Navigator.of(context).pop(),
          child: Text(l10n.polarConfirmCancel),
        ),
        adaptiveDialogAction(
          context,
          key: const Key('polar-move-to-start-confirm-go'),
          isDefault: true,
          filled: true,
          onPressed: () => Navigator.of(context).pop(true),
          child: Text(l10n.polarConfirmGo),
        ),
      ],
    );
  }
}

/// The confirm step's dialog: where to point the mount (with NINA's guidance,
/// #2083), the RA turns and their direction, the most the run turns, and the
/// hand on the power switch. Pops true for "Move the mount", null for Cancel.
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
      // #2083 — NINA's guidance on the start field.
      l10n.polarConfirmGuideField,
      l10n.polarConfirmGuideMeridian,
      // #2081 — the motor-direction test, the run's first move.
      if (plan.directionTestDeg > 0)
        l10n.polarConfirmDirectionTest(
          NumberFormat(
            '0.0',
            Localizations.localeOf(context).toString(),
          ).format(plan.directionTestDeg),
        ),
      l10n.polarConfirmTurns(
        deg(plan.stepDeg),
        direction,
        deg(plan.stepDeg * plan.turns),
      ),
      // #2082 Rough first, or the profile's camera set to main.
      if (plan.camera == PolarAlignCameras.main) l10n.polarConfirmMainCamera,
      // #2089 — where this run stands against the session's first start.
      if (plan.newOrigin)
        l10n.polarConfirmNewOrigin(deg(plan.sweepLimitDeg))
      else if (turnsBack)
        l10n.polarConfirmFromOrigin(
          deg(plan.originOffsetDeg.abs()),
          offsetDirection,
        ),
      l10n.polarConfirmPower,
    ];
    return adaptiveAlertDialog(
      // The guidance lines (#2083) make it tall: scroll on a short screen.
      scrollable: true,
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
          onPressed: () => Navigator.of(context).pop(),
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
