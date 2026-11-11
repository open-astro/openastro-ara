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
import 'equipment/camera_state.dart';
import 'imaging/exposure_state.dart';
import 'settings/imaging_defaults_state.dart';

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

  /// Holds the auto-disposed rig clients for [work]: with no widget watching
  /// them (a tab or Night done gone meanwhile) Riverpod would close their Dio
  /// mid-call (the create_imaging_run pin, rig 2026-10-04).
  Future<T> _pinned<T>(Future<T> Function() work) async {
    final a = ref.container.listen(calibrationApiProvider, (_, _) {});
    final b = ref.container.listen(sequenceApiProvider, (_, _) {});
    try {
      return await work();
    } finally {
      a.close();
      b.close();
    }
  }

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
    return _pinned(() async {
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
    });
  }

  /// The camera's library run for [need]: darks for [exposures] at the plan's
  /// gain and temperature (all of them again with [refresh]), bias first when
  /// [bias], and before both the cover closing ([cover]) or the lens-cap
  /// question. Returns the rig's run id and its body, edited on the rig.
  Future<(String, Map<String, dynamic>)> _libraryRun(
    LibraryNeed need,
    List<double> exposures, {
    required bool bias,
    required bool refresh,
    required bool cover,
    required String lensCapPrompt,
  }) async {
    final api = ref.read(calibrationApiProvider)!;
    final sequences = ref.read(sequenceApiProvider)!;
    await api.buildDarkLibrary(
      DarkLibraryBuildRequest(
        exposureSecondsList: exposures,
        gainList: [need.gain],
        targetTemperatureCList: [?need.temperatureC],
        framesPerCombination: kSimpleLibraryFrames,
        reuseExistingFrames: !refresh,
      ),
    );
    final id = (await api.darkLibraryStatus()).generatedSequenceId;
    if (id == null) throw StateError('the rig saved no dark library run');
    final body = (await sequences.getSequenceDetail(id)).body;
    final edited = withChildren(body, [
      if (!cover) withLensCap(lensCapPrompt),
      if (bias)
        biasSet(
          gain: need.gain,
          offset: need.offset,
          frames: kSimpleBiasFrames,
        ),
      ...childrenOf(body),
    ]);
    await sequences.updateSequence(id, body: edited);
    return (id, edited);
  }

  /// Builds the camera's library for [exposures] now (see [_libraryRun]).
  Future<bool> buildLibrary(
    LibraryNeed need,
    List<double> exposures, {
    required bool bias,
    required String lensCapPrompt,
    bool refresh = false,
  }) async {
    return _pinned(() async {
      if (ref.read(calibrationApiProvider) == null ||
          ref.read(sequenceApiProvider) == null) {
        return false;
      }
      try {
        final (id, _) = await _libraryRun(
          need,
          exposures,
          bias: bias,
          refresh: refresh,
          cover: false,
          lensCapPrompt: lensCapPrompt,
        );
        await ref.read(sequenceApiProvider)!.start(id);
        state = (
          sequenceId: id,
          total:
              exposures.length * kSimpleLibraryFrames +
              (bias ? kSimpleBiasFrames : 0),
        );
        return true;
      } catch (e) {
        _fail(e, (l) => l.calibrationActionBuildLibrary);
        return false;
      }
    });
  }

  /// The same library run as the Queue's Calibration block, after the last
  /// target: with a panel cover it closes and needs no one, else it asks for
  /// the lens cap. The run the rig saved goes. True once queued.
  Future<bool> queueLibrary(
    LibraryNeed need,
    List<double> exposures, {
    required bool bias,
    required String lensCapPrompt,
    bool refresh = false,
  }) async {
    return _pinned(() async {
      final sequences = ref.read(sequenceApiProvider);
      if (ref.read(calibrationApiProvider) == null || sequences == null) {
        return false;
      }
      final panel = ref.read(flatPanelProvider).value;
      final cover = (panel?.isConnected ?? false) && panel!.hasCover;
      try {
        final (id, body) = await _libraryRun(
          need,
          exposures,
          bias: bias,
          refresh: refresh,
          cover: cover,
          lensCapPrompt: lensCapPrompt,
        );
        try {
          await sequences.deleteSequence(id);
        } catch (_) {}
        return await ref
            .read(simpleRunProvider.notifier)
            .addCalibration(calibrationBlockFrom(body, closeCover: cover));
      } catch (e) {
        _fail(e, (l) => l.calibrationActionQueue);
        return false;
      }
    });
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
        .show(SimpleMessageKind.error, friendlyError(l, e, action: action(l)));
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

/// Darks per exposure, and bias frames, in a library Simple Mode builds.
const kSimpleLibraryFrames = 25;
const kSimpleBiasFrames = 50;

/// What the queued plan needs from the camera's library (#1757): its light
/// exposures at its gain, offset and sensor temperature (null: uncooled).
typedef LibraryNeed = ({
  List<double> exposures,
  int gain,
  int? offset,
  double? temperatureC,
});

/// A dark set older than this reads Old: a sensor's dark current drifts.
const kSimpleLibraryOldAfter = Duration(days: 182);

/// How the camera's library meets [need]: exposures with no darks, darks
/// older than [kSimpleLibraryOldAfter], and whether bias at the gain is
/// missing. Darks count within 1 °C of the temperature. Pure — unit-tested.
({List<double> missing, List<double> old, bool biasMissing}) libraryCover(
  DarkLibraryState lib,
  LibraryNeed need,
  DateTime now,
) {
  final missing = <double>[], old = <double>[];
  for (final e in need.exposures) {
    final hits = lib.entries.where(
      (d) =>
          d.exposureSeconds == e &&
          (d.gain == null || d.gain == need.gain) &&
          (need.temperatureC == null ||
              (d.temperatureC - need.temperatureC!).abs() <= 1),
    );
    if (hits.isEmpty) {
      missing.add(e);
    } else if (hits.every(
      (d) => now.difference(d.capturedUtc) > kSimpleLibraryOldAfter,
    )) {
      old.add(e);
    }
  }
  final biasMissing = !lib.biasEntries.any(
    (b) =>
        (b.gain == null || b.gain == need.gain) &&
        (need.offset == null || b.offset == null || b.offset == need.offset),
  );
  return (missing: missing, old: old, biasMissing: biasMissing);
}

/// The library the queued plan needs: its targets' light rows (else the
/// sheet's rows), at its gain, the camera's offset and the profile's cooler
/// target when the camera can cool. Null with no rows.
final simpleLibraryNeedProvider = Provider.autoDispose<LibraryNeed?>((ref) {
  final run = ref.watch(simpleRunProvider);
  final steps = run.targets.isNotEmpty
      ? [for (final t in run.targets.where((t) => !t.calibration)) ...t.steps]
      : ref.watch(simpleRunPlanProvider);
  if (steps.isEmpty) return null;
  final exposure = ref.watch(exposureControllerProvider);
  final camera = ref.watch(cameraStatusProvider).value;
  final cools = camera?.capabilities?.canSetTemperature ?? true;
  return (
    exposures: {for (final s in steps) s.exposureSeconds}.toList()..sort(),
    gain:
        run.targets.firstOrNull?.gain ??
        ref.watch(simpleRunGainProvider) ??
        exposure.gain,
    offset: exposure.offset >= 0 ? exposure.offset : null,
    temperatureC: cools
        ? ref.watch(imagingDefaultsProvider).coolerTargetC
        : null,
  );
});

final simpleCalibrationProvider =
    NotifierProvider<SimpleCalibrationNotifier, SimpleCalibrationRun?>(
      SimpleCalibrationNotifier.new,
    );
