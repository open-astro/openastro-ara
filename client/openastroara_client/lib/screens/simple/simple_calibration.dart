import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../models/calibration/calibration_models.dart';
import '../../models/sequence/sequence_summary.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../state/simple_calibration_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';

TextStyle get _dim => kSimpleText.copyWith(color: AraColors.textSecondary);

/// The button's words for what [need] holds.
String calibrationTakeLabel(AppLocalizations l, CalibrationNeed need) =>
    need.flats && (need.darks || need.bias)
    ? l.calibrationTakeFlatsAndDarks
    : need.flats
    ? l.calibrationTakeFlats
    : l.calibrationTakeDarks;

/// What [session] still lacks, in a line: the filters without flats, then
/// darks and bias.
String calibrationMissingLine(
  AppLocalizations l,
  CalibrationSession session,
  CalibrationNeed need,
) {
  final noFlats = [
    for (final f in session.filtersUsed)
      if (f.matchingFlatFrames == 0) f.filterName,
  ];
  return [
    if (need.flats)
      noFlats.isEmpty ? l.calibrationFlats : l.calibrationFlatsFor(noFlats.join(', ')),
    if (need.darks) l.calibrationDarks,
    if (need.bias) l.calibrationBias,
  ].join(' · ');
}

/// Night done's calibration row (#1563): what tonight's lights are missing
/// and one button that shoots it, flats first; its progress while it runs.
/// Nothing when the rig has no calibration session for the run.
class SimpleNightCalibration extends ConsumerWidget {
  const SimpleNightCalibration({
    super.key,
    required this.startUtc,
    required this.endUtc,
  });

  /// The run's start and end: the session whose lights fall in them counts.
  final DateTime startUtc;
  final DateTime endUtc;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l = context.l10n;
    final sessions = ref.watch(simpleCalibrationSessionsProvider);
    final session = sessions == null
        ? null
        : calibrationSessionWithin(sessions, startUtc, endUtc);
    if (session == null) return const SizedBox.shrink();
    final calibration = ref.read(simpleCalibrationProvider.notifier);
    final run = ref.watch(simpleCalibrationProvider);
    final info = ref.watch(sequenceRunStateProvider).value;
    final ours = run != null && info?.sequenceId == run.sequenceId;
    final need = calibrationNeed(session);

    Widget take() => FilledButton.tonal(
      key: const Key('simple-calibration-take'),
      onPressed: () => calibration.shoot(session, need),
      child: Text(calibrationTakeLabel(l, need)),
    );

    final (String line, String? sub, Widget? action) = switch (info?.state) {
      final s? when ours && s.isActive => (
        l.calibrationTaking(info!.framesCaptured, run.total),
        null,
        OutlinedButton(
          key: const Key('simple-calibration-stop'),
          onPressed: calibration.stop,
          child: Text(l.deviceStop),
        ),
      ),
      SequenceRunState.completed when ours => (l.calibrationDone, null, null),
      _ when ours => (
        l.calibrationStopped(info!.framesCaptured, run.total),
        need.any ? calibrationMissingLine(l, session, need) : null,
        need.any ? take() : null,
      ),
      _ when need.any => (
        l.calibrationMissingTonight,
        calibrationMissingLine(l, session, need),
        take(),
      ),
      _ => (l.calibrationOnFile, null, null),
    };
    return Row(
      key: const Key('simple-calibration'),
      spacing: AraSpace.s12,
      children: [
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(line, style: kSimpleText),
              if (sub != null) Text(sub, style: _dim),
            ],
          ),
        ),
        ?action,
      ],
    );
  }
}
