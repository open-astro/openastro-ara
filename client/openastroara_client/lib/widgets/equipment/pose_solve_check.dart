import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:intl/intl.dart';

import '../../l10n/l10n.dart';
import '../../models/mount_status.dart' show formatDecDegrees, formatRaHours;
import '../../models/pose_solve_check.dart';
import '../../state/equipment/pose_solve_check_state.dart';
import '../../theme/ara_colors.dart';
import '../../util/friendly_error.dart';

/// #2173 — "Check position with a plate solve" on the unverified-position row
/// of the Mount window (Full) and Simple Mode: the button shows only while the
/// rig says the check can run (unverified, a camera and ASTAP set up, the
/// camera idle). Below it, the last check's result, and after a mismatch the
/// one-tap Sync. [textStyle] is the presentation's caption style.
class PoseSolveCheck extends ConsumerWidget {
  final TextStyle? textStyle;

  const PoseSolveCheck({super.key, this.textStyle});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = context.l10n;
    final run = ref.watch(poseSolveCheckProvider);
    final availability = ref.watch(poseSolveAvailabilityProvider);
    final notifier = ref.read(poseSolveCheckProvider.notifier);
    final canRun = switch (availability) {
      AsyncData(:final value) => value?.available ?? false,
      AsyncLoading() => false,
      AsyncError() => false,
    };
    final result = run.result;
    final error = run.error;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: [
        if (run.running)
          Text(
            l10n.poseCheckRunning,
            key: const Key('pose-check-running'),
            style: textStyle,
          )
        else if (canRun)
          Align(
            alignment: AlignmentDirectional.centerStart,
            child: TextButton(
              key: const Key('pose-check-button'),
              onPressed: notifier.check,
              child: Text(l10n.poseCheckButton),
            ),
          ),
        if (!run.running && result != null)
          Text(
            poseSolveResultText(l10n, result),
            key: const Key('pose-check-result'),
            style: textStyle,
          ),
        if (!run.running &&
            result?.outcome == PoseSolveOutcome.mismatch &&
            result?.checkId != null)
          Align(
            alignment: AlignmentDirectional.centerStart,
            child: TextButton(
              key: const Key('pose-check-sync'),
              onPressed: notifier.sync,
              child: Text(l10n.poseCheckSync),
            ),
          ),
        if (!run.running && error != null)
          Text(
            '${l10n.poseCheckCouldnt}: ${failureReason(l10n, error)}',
            key: const Key('pose-check-error'),
            style: (textStyle ?? const TextStyle()).copyWith(
              color: AraColors.errorText,
            ),
          ),
      ],
    );
  }
}

/// The sentence for one check's result, in the app's language. Pure — tested.
String poseSolveResultText(AppLocalizations l, PoseSolveCheckResult r) {
  final camera = switch (r.camera) {
    PoseSolveCamera.main => l.poseCheckCameraMain,
    PoseSolveCamera.guide => l.poseCheckCameraGuide,
  };
  String position(double? ra, double? dec) =>
      l.poseCheckPosition(formatRaHours(ra), formatDecDegrees(dec));
  return switch (r.outcome) {
    PoseSolveOutcome.trusted => l.poseCheckTrusted(camera),
    PoseSolveOutcome.mismatch => l.poseCheckMismatch(
      position(r.mountRaHours, r.mountDecDegrees),
      position(r.solvedRaHours, r.solvedDecDegrees),
      r.separationDegrees == null
          ? '?'
          : NumberFormat('0.0', l.localeName).format(r.separationDegrees),
      camera,
    ),
    PoseSolveOutcome.pierSideDisagrees => l.poseCheckPierSideDisagrees(camera),
    PoseSolveOutcome.pierSideUnknown => l.poseCheckPierSideUnknown(camera),
    PoseSolveOutcome.solveFailed => l.poseCheckSolveFailed(camera),
    PoseSolveOutcome.mountMoved => l.poseCheckMountMoved,
    PoseSolveOutcome.syncRefused => l.poseCheckSyncRefused,
    PoseSolveOutcome.syncNotTaken => l.poseCheckSyncNotTaken,
  };
}
