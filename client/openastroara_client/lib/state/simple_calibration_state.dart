import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../l10n/l10n.dart';
import '../models/calibration/calibration_models.dart';
import '../util/friendly_error.dart';
import 'app_locale_state.dart';
import 'calibration/calibration_state.dart';
import 'equipment/flat_panel_state.dart';
import 'sequencer/sequence_list_state.dart';
import 'simple_messages_state.dart';

/// What a calibration session still lacks (#1563, #1757).
typedef CalibrationNeed = ({bool flats, bool darks, bool bias});

CalibrationNeed calibrationNeed(CalibrationSession s) => (
  flats: !s.matchingFlatsAvailable,
  darks: !s.matchingDarksAvailable,
  bias: !s.matchingBiasAvailable,
);

extension CalibrationNeedAny on CalibrationNeed {
  bool get any => flats || darks || bias;
}

/// The calibration sessions Simple Mode counts, newest first: a plan's.
/// Preview's shots are snapshots, not lights to calibrate (user, 2026-11-11).
final simpleCalibrationSessionsProvider =
    Provider.autoDispose<List<CalibrationSession>?>(
      (ref) => ref
          .watch(calibrationSessionsProvider)
          .value
          ?.where((s) => !s.manualCapture)
          .toList(growable: false),
    );

/// The newest of [sessions] whose lights fall in [start]–[end] (a run's), or
/// null when none does.
CalibrationSession? calibrationSessionWithin(
  List<CalibrationSession> sessions,
  DateTime start,
  DateTime end,
) {
  CalibrationSession? best;
  for (final s in sessions) {
    if (s.sessionStartUtc.isAfter(end) || s.sessionEndUtc.isBefore(start)) {
      continue;
    }
    if (best == null || s.sessionStartUtc.isAfter(best.sessionStartUtc)) {
      best = s;
    }
  }
  return best;
}

/// The calibration run Simple Mode started: its sequence and how many frames
/// it plans.
typedef SimpleCalibrationRun = ({String sequenceId, int total});

/// Shoots what a session is missing, one run for flats, bias and darks: the
/// Calibration mode's shutter, its tabs and Night done all come here.
class SimpleCalibrationNotifier extends Notifier<SimpleCalibrationRun?> {
  @override
  SimpleCalibrationRun? build() => null;

  /// Generates the run for [need] on [session] and starts it. Flats use the
  /// panel when one is connected, else the twilight sky; darks and bias wait
  /// for the lens cap (a panel's cover closes instead). False when it failed,
  /// with a pill saying why.
  Future<bool> shoot(CalibrationSession session, CalibrationNeed need) async {
    final api = ref.read(calibrationApiProvider);
    final sequences = ref.read(sequenceApiProvider);
    if (api == null || sequences == null) return false;
    final panel = ref.read(flatPanelProvider).value?.isConnected ?? false;
    try {
      final generated = await api.generateMatchingFlats(
        session.id,
        includeFlats: need.flats,
        includeDarks: need.darks,
        includeBias: need.bias,
        pauseForLensCap: true,
        flavor: panel ? 'panel' : 'sky',
      );
      final id = generated.generatedSequenceId;
      if (id == null) throw StateError('the rig saved no calibration run');
      await sequences.start(id);
      state = (
        sequenceId: id,
        total:
            generated.totalFlatFrames +
            generated.totalBiasFrames +
            generated.totalDarkFrames,
      );
      return true;
    } catch (e) {
      _fail(e, (l) => l.calibrationActionShoot);
      return false;
    }
  }

  Future<void> stop() async {
    final run = state;
    final sequences = ref.read(sequenceApiProvider);
    if (run == null || sequences == null) return;
    try {
      await sequences.stop(run.sequenceId);
    } catch (e) {
      _fail(e, (l) => l.calibrationActionStop);
    }
  }

  void _fail(Object e, String Function(AppLocalizations l) action) {
    final l = l10nFor(ref.read(appLocaleProvider));
    ref
        .read(simpleMessagesProvider.notifier)
        .show(
          SimpleMessageKind.error,
          friendlyError(l, e, action: action(l)),
        );
  }
}

final simpleCalibrationProvider =
    NotifierProvider<SimpleCalibrationNotifier, SimpleCalibrationRun?>(
      SimpleCalibrationNotifier.new,
    );
