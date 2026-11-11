import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../l10n/l10n.dart';
import '../models/calibration/calibration_models.dart';
import '../models/sequence/imaging_run_body.dart';
import '../models/sequence/nina_dom.dart' show childrenOf, withChildren;
import '../util/friendly_error.dart';
import 'app_locale_state.dart';
import 'calibration/calibration_state.dart';
import 'equipment/flat_panel_state.dart';
import 'sequencer/sequence_list_state.dart';
import 'simple_messages_state.dart';
import 'simple_run_state.dart';

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

  /// Generates the run for [need] on [session] and starts it. Flats come
  /// from [simpleFlatLightProvider]'s light: in front of the scope (a panel
  /// lights itself; a screen or light box is put there by hand, and the run
  /// asks for it first) or the twilight sky. Darks and bias wait for the lens
  /// cap (a panel's cover closes instead). [reshoot] takes flats for filters
  /// already covered too. False when it failed, with a pill saying why.
  Future<bool> shoot(
    CalibrationSession session,
    CalibrationNeed need, {
    bool reshoot = false,
  }) async {
    final api = ref.read(calibrationApiProvider);
    final sequences = ref.read(sequenceApiProvider);
    if (api == null || sequences == null) return false;
    final panel = ref.read(flatPanelProvider).value?.isConnected ?? false;
    final sky = ref.read(simpleFlatLightProvider) == SimpleFlatLight.sky;
    try {
      final generated = await api.generateMatchingFlats(
        session.id,
        includeFlats: need.flats,
        includeDarks: need.darks,
        includeBias: need.bias,
        pauseForLensCap: true,
        includeCoveredFilters: reshoot,
        // The rig's panel flats solve the exposure with or without a panel.
        flavor: sky ? 'sky' : 'panel',
      );
      final id = generated.generatedSequenceId;
      if (id == null) throw StateError('the rig saved no calibration run');
      if (need.flats && !sky && !panel) {
        // A light held over the scope by hand: asked for before the first flat.
        final l = l10nFor(ref.read(appLocaleProvider));
        final body = (await sequences.getSequenceDetail(id)).body;
        await sequences.updateSequence(
          id,
          body: withChildren(body, [
            withLensCap(l.calibrationLightPrompt),
            ...childrenOf(body),
          ]),
        );
      }
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

  /// [session]'s missing darks and bias as the Queue's Calibration block,
  /// after the last target. With a panel cover the block closes it and needs
  /// no one; without one it asks for the lens cap. True once queued.
  Future<bool> queue(CalibrationSession session, CalibrationNeed need) async {
    final api = ref.read(calibrationApiProvider);
    final sequences = ref.read(sequenceApiProvider);
    if (api == null || sequences == null) return false;
    final panel = ref.read(flatPanelProvider).value;
    final cover = (panel?.isConnected ?? false) && panel!.hasCover;
    try {
      // The generator builds the sets (each at its lights' temperature, filed
      // with them); the block takes its steps and the run it saved goes.
      final generated = await api.generateMatchingFlats(
        session.id,
        includeFlats: false,
        includeDarks: need.darks,
        includeBias: need.bias,
        pauseForLensCap: !cover,
        parkMountAfter: false,
      );
      final id = generated.generatedSequenceId;
      if (id == null) throw StateError('the rig saved no calibration run');
      final Map<String, dynamic> body;
      try {
        body = (await sequences.getSequenceDetail(id)).body;
      } finally {
        try {
          await sequences.deleteSequence(id);
        } catch (_) {}
      }
      return await ref
          .read(simpleRunProvider.notifier)
          .addCalibration(calibrationBlockFrom(body, closeCover: cover));
    } catch (e) {
      _fail(e, (l) => l.calibrationActionQueue);
      return false;
    }
  }

  /// No plan lights yet: a dark library for [exposures] at [gain] and
  /// [temperatureC], reused for weeks. The rig's library run gets the
  /// lens-cap question first ([lensCapPrompt]) and starts.
  Future<bool> buildLibrary({
    required List<double> exposures,
    required int gain,
    required double temperatureC,
    required String lensCapPrompt,
    int frames = kSimpleLibraryFrames,
  }) async {
    final api = ref.read(calibrationApiProvider);
    final sequences = ref.read(sequenceApiProvider);
    if (api == null || sequences == null) return false;
    try {
      await api.buildDarkLibrary(
        DarkLibraryBuildRequest(
          exposureSecondsList: exposures,
          gainList: [gain],
          targetTemperatureCList: [temperatureC],
          framesPerCombination: frames,
          reuseExistingFrames: true,
        ),
      );
      final id = (await api.darkLibraryStatus()).generatedSequenceId;
      if (id == null) throw StateError('the rig saved no dark library run');
      final body = (await sequences.getSequenceDetail(id)).body;
      await sequences.updateSequence(
        id,
        body: withChildren(body, [
          withLensCap(lensCapPrompt),
          ...childrenOf(body),
        ]),
      );
      await sequences.start(id);
      state = (sequenceId: id, total: exposures.length * frames);
      return true;
    } catch (e) {
      _fail(e, (l) => l.calibrationActionBuildLibrary);
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

/// Where flats get their light: something in front of the scope (a flat
/// panel, or a tablet or light box held there) or the twilight sky. Not
/// assumed from the rig: a light box is no device (user, 2026-11-11).
enum SimpleFlatLight { front, sky }

final simpleFlatLightProvider =
    NotifierProvider<SimpleFlatLightNotifier, SimpleFlatLight>(
      SimpleFlatLightNotifier.new,
    );

class SimpleFlatLightNotifier extends Notifier<SimpleFlatLight> {
  @override
  SimpleFlatLight build() => SimpleFlatLight.front;
  void set(SimpleFlatLight v) => state = v;
}

/// Darks per exposure in a dark library Simple Mode builds.
const kSimpleLibraryFrames = 25;

final simpleCalibrationProvider =
    NotifierProvider<SimpleCalibrationNotifier, SimpleCalibrationRun?>(
      SimpleCalibrationNotifier.new,
    );
