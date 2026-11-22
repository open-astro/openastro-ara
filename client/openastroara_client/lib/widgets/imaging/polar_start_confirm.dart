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
///
/// #2083 — on a rig that takes it, the user can also choose to turn RA by
/// hand (NINA's manual mode): the start then asks the rig to move nothing.
///
/// #2081 — a mount whose RA motor direction is not saved for its driver first
/// turns 0.5° to learn it: the dialog says so, and the start confirms it.
///
/// #2068 — while the mount's clearance is not confirmed the rig refuses the
/// motor start's plan; the step then offers Turn RA by hand alone, which moves
/// nothing (Joey, 2026-11-21).
Future<void> confirmAndStartPolarAlign(
  BuildContext context,
  PolarAlignClient api, {
  String? stage,
}) async {
  PolarAlignStartPlan plan;
  var motorAllowed = true;
  try {
    plan = await api.startPlan(stage: stage);
  } on Refusal catch (refusal) {
    if (refusal.cause != RefusalCause.polarAlignClearanceNotConfirmed) rethrow;
    plan = await api.startPlan(stage: stage, handTurn: true);
    if (!plan.handTurnAvailable) rethrow;
    motorAllowed = false;
  }
  if (!context.mounted) return;
  final choice = await showAdaptiveAlert<PolarStartChoice>(
    context: context,
    builder: (_) =>
        PolarStartConfirmDialog(plan: plan, motorAllowed: motorAllowed),
  );
  switch (choice) {
    case PolarStartChoice.motor:
      await api.start(plan);
    case PolarStartChoice.hand:
      await api.start(plan.byHand());
    case null:
      return;
  }
}

/// The confirm step's answer: the mount's motor turns RA, or the user does.
enum PolarStartChoice { motor, hand }

/// The confirm step's dialog: where to point the mount (with NINA's guidance,
/// #2083), the RA turns and their direction, the most the run turns, and the
/// hand on the power switch. Pops [PolarStartChoice.motor] for "Move the
/// mount", [PolarStartChoice.hand] for "Turn RA by hand" (shown only when the
/// rig takes it), null for Cancel.
class PolarStartConfirmDialog extends StatelessWidget {
  const PolarStartConfirmDialog({
    super.key,
    required this.plan,
    this.motorAllowed = true,
  });

  final PolarAlignStartPlan plan;

  /// False while the mount's clearance is not confirmed (#2068): "Move the
  /// mount" is off and the dialog says why; Turn RA by hand stays.
  final bool motorAllowed;

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
      // #2081 — the motor-direction test, the run's first move: a motor run's
      // only (a hand-turn run makes no test).
      if (motorAllowed && !plan.handTurn && plan.directionTestDeg > 0)
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
      if (plan.handTurnAvailable) l10n.polarConfirmHandTurnHint,
      if (!motorAllowed) l10n.polarConfirmMotorNeedsClearance,
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
        if (plan.handTurnAvailable)
          adaptiveDialogAction(
            context,
            key: const Key('polar-start-confirm-hand'),
            onPressed: () => Navigator.of(context).pop(PolarStartChoice.hand),
            child: Text(l10n.polarConfirmHandTurn),
          ),
        adaptiveDialogAction(
          context,
          key: const Key('polar-start-confirm-go'),
          isDefault: true,
          filled: true,
          onPressed: motorAllowed
              ? () => Navigator.of(context).pop(PolarStartChoice.motor)
              : null,
          child: Text(l10n.polarConfirmGo),
        ),
      ],
    );
  }
}
