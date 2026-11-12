import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../l10n/l10n.dart';
import '../models/calibration/calibration_models.dart';
import '../models/sequence/imaging_run_body.dart';
import '../models/sequence/instruction_catalog.dart' show instructionForType, sequentialContainerType;
import '../models/sequence/sequence_summary.dart';
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
        // The rig's run becomes a block in the queue (a plan square, like
        // everything that takes a while: user, 2026-11-12); its own copy goes.
        var body = (await sequences.getSequenceDetail(id)).body;
        if (need.flats && !sky && !panel) {
          // A light held over the scope by hand: asked for before the first flat.
          final l = l10nFor(ref.read(appLocaleProvider));
          body = withChildren(body, [
            withLensCap(l.calibrationLightPrompt),
            ...childrenOf(body),
          ]);
        }
        try {
          await sequences.deleteSequence(id);
        } catch (_) {}
        return await _throughQueue(
          calibrationBlockFrom(body),
          total:
              generated.totalFlatFrames +
              generated.totalBiasFrames +
              generated.totalDarkFrames,
        );
      } catch (e) {
        _fail(e, (l) => l.calibrationActionShoot);
        return false;
      }
    });
  }

  /// Runs [block] through the queue; when it started a queue of its own,
  /// this run's progress follows it. False when nothing was queued.
  Future<bool> _throughQueue(
    Map<String, dynamic> block, {
    required int total,
  }) async {
    final queue = ref.read(simpleRunProvider.notifier);
    final before = ref.read(simpleRunProvider).targets.length;
    final own = await queue.runCalibration(block);
    if (own != null) state = (sequenceId: own, total: total);
    return own != null || ref.read(simpleRunProvider).targets.length > before;
  }

  /// The camera's library run for [need]: darks for [exposures] at the plan's
  /// gain and temperature (all of them again with [refresh]), bias first when
  /// [bias], and before both the cover closing ([cover]) or the lens-cap
  /// question. Returns the rig's run id (null when the rig had no darks to
  /// shoot) and the body, edited on the rig; null when there is nothing to
  /// shoot at all.
  Future<(String?, Map<String, dynamic>)?> _libraryRun(
    LibraryNeed need,
    List<double> exposures, {
    required bool bias,
    required bool refresh,
    required bool cover,
    required String lensCapPrompt,
  }) async {
    final api = ref.read(calibrationApiProvider)!;
    final sequences = ref.read(sequenceApiProvider)!;
    // Bias alone has no darks to ask the rig for: it refuses an empty
    // exposure list (422). And a reuse build it already covers saves no run.
    String? id;
    if (exposures.isNotEmpty) {
      await api.buildDarkLibrary(
        DarkLibraryBuildRequest(
          exposureSecondsList: exposures,
          gainList: [need.gain],
          targetTemperatureCList: [?need.temperatureC],
          framesPerCombination: kSimpleLibraryFrames,
          reuseExistingFrames: !refresh,
        ),
      );
      id = (await api.darkLibraryStatus()).generatedSequenceId;
    }
    if (id == null && !bias) return null;
    final body = id == null
        ? instructionForType(sequentialContainerType)!.build()
        : (await sequences.getSequenceDetail(id)).body;
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
    if (id != null) await sequences.updateSequence(id, body: edited);
    return (id, edited);
  }

  /// The rig's library run [id] goes once its body is in the queue.
  Future<void> _dropRun(String? id) async {
    if (id == null) return;
    try {
      await ref.read(sequenceApiProvider)!.deleteSequence(id);
    } catch (_) {}
  }

  void _nothingMissing() {
    final l = l10nFor(ref.read(appLocaleProvider));
    ref
        .read(simpleMessagesProvider.notifier)
        .show(SimpleMessageKind.result, l.calibrationNothingMissing);
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
        final run = await _libraryRun(
          need,
          exposures,
          bias: bias,
          refresh: refresh,
          cover: false,
          lensCapPrompt: lensCapPrompt,
        );
        if (run == null) {
          _nothingMissing();
          return false;
        }
        final (id, body) = run;
        await _dropRun(id);
        return await _throughQueue(
          calibrationBlockFrom(body),
          total:
              exposures.length * kSimpleLibraryFrames +
              (bias ? kSimpleBiasFrames : 0),
        );
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
        final run = await _libraryRun(
          need,
          exposures,
          bias: bias,
          refresh: refresh,
          cover: cover,
          lensCapPrompt: lensCapPrompt,
        );
        if (run == null) {
          _nothingMissing();
          return false;
        }
        final (id, body) = run;
        await _dropRun(id);
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

/// The calibration run's state, read from the rig every 2 s until it ends.
/// The run state provider follows the Plan's selected sequence, so it never
/// sees this run: the lens-cap pause, progress and Stop read it here
/// (rig 2026-11-12: Build picked started runs no screen showed).
final simpleCalibrationRunStateProvider =
    AsyncNotifierProvider<
      SimpleCalibrationRunStateNotifier,
      SequenceRunStateInfo?
    >(SimpleCalibrationRunStateNotifier.new);

class SimpleCalibrationRunStateNotifier
    extends AsyncNotifier<SequenceRunStateInfo?> {
  Timer? _poll;

  @override
  Future<SequenceRunStateInfo?> build() async {
    ref.onDispose(() => _poll?.cancel());
    final run = ref.watch(simpleCalibrationProvider);
    final api = ref.watch(sequenceApiProvider);
    if (run == null || api == null) return null;
    _poll = Timer.periodic(const Duration(seconds: 2), (_) async {
      try {
        final next = await api.getRunState(run.sequenceId);
        if (!ref.mounted) return;
        state = AsyncData(next);
        if (!(next?.state?.isActive ?? false)) _poll?.cancel();
      } catch (_) {
        // A missed read is retried on the next tick.
      }
    });
    return api.getRunState(run.sequenceId);
  }
}

final simpleCalibrationProvider =
    NotifierProvider<SimpleCalibrationNotifier, SimpleCalibrationRun?>(
      SimpleCalibrationNotifier.new,
    );
