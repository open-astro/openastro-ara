import 'dart:async';
import 'dart:typed_data';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/polar_align.dart';
import '../../services/polar_align_api.dart';
import 'polar_align_state.dart';

/// The Polar Align Routine both presentations render (ADR 0013 rule 3): the
/// routine camera's frame and the panel's busy and error state. The profile's
/// settings are a profile section (polar_align_settings_state.dart, ADR
/// 0015). The live phase itself is [polarAlignLiveProvider]; a panel opening
/// hydrates it through [PolarAlignFrameNotifier.hydrate].

/// An exposure the routine takes: more than 0 and at most 60 seconds.
bool polarExposureValid(double? seconds) =>
    seconds != null && seconds > 0 && seconds <= 60;

/// The ranges the knobs accept (the settings registry's), shared by the
/// Settings panel and the Start card's Advanced row so they refuse alike.
typedef PolarKnobRange = ({double min, double max});
const polarSeedRotationRange = (min: 5.0, max: 30.0);
const polarCheckStepRange = (min: 5.0, max: 30.0);
const polarCoarseExposureRange = (min: 0.1, max: 60.0);

/// #2082 frames averaged at each point: whole numbers only.
const polarFramesPerPointRange = (min: 1, max: 5);

/// A knob's value as its field shows it.
String polarKnobText(double v) =>
    v == v.roundToDouble() ? v.toStringAsFixed(0) : v.toStringAsFixed(2);

/// The routine camera's latest frame, fetched after each finished frame; null
/// until there is one. A failed fetch keeps the previous picture. Fetches are
/// coalesced: a frame that finishes while one is in flight is fetched once
/// after it, or Single would keep showing the older image. Not while the run
/// shoots the main camera (`polar_align.camera` = main, #2082): the guider has
/// none of its frames to show.
class PolarAlignFrameNotifier extends Notifier<Uint8List?> {
  bool _loading = false;
  bool _reload = false;

  @override
  Uint8List? build() {
    ref.listen(polarAlignLiveProvider, (prev, next) {
      if (!next.usesMainCamera &&
          next.lastFrame != null &&
          !identical(prev?.lastFrame, next.lastFrame)) {
        unawaited(load());
      }
    });
    // A frame already finished before anything watched: fetch it too.
    final live = ref.read(polarAlignLiveProvider);
    if (live.lastFrame != null && !live.usesMainCamera) {
      Future.microtask(load);
    }
    return null;
  }

  /// A panel opening: seed the live view from REST, and fetch the frame of a
  /// routine already running.
  Future<void> hydrate() async {
    final status = await ref.read(polarAlignLiveProvider.notifier).hydrate();
    if (status != null &&
        status.isActive &&
        status.camera != PolarAlignCameras.main &&
        ref.mounted) {
      await load();
    }
  }

  Future<void> load() async {
    final api = ref.read(polarAlignApiProvider);
    if (api == null) return;
    if (_loading) {
      _reload = true;
      return;
    }
    _loading = true;
    try {
      final jpeg = await api.getLiveFrame();
      if (ref.mounted && jpeg != null) state = jpeg;
    } catch (_) {
      // Keep showing the last frame.
    } finally {
      _loading = false;
    }
    if (_reload && ref.mounted) {
      _reload = false;
      unawaited(load());
    }
  }
}

final polarAlignFrameProvider =
    NotifierProvider.autoDispose<PolarAlignFrameNotifier, Uint8List?>(
      PolarAlignFrameNotifier.new,
    );

/// A panel's command in flight and the last one's failure, in the user's words.
typedef PolarAlignAction = ({bool busy, String? error});

class PolarAlignActionNotifier extends Notifier<PolarAlignAction> {
  @override
  PolarAlignAction build() => (busy: false, error: null);

  /// Calls in flight: Stop runs alongside another call (#1770), so busy ends
  /// only with the last of them.
  int _inFlight = 0;

  /// Run [op] against the rig; [describe] words a failure, or returns null
  /// when the caller says it elsewhere. True when the rig took it.
  Future<bool> run(
    Future<void> Function(PolarAlignClient api) op, {
    required String? Function(Object error) describe,
  }) async {
    final api = ref.read(polarAlignApiProvider);
    if (api == null) return false;
    _inFlight++;
    state = (busy: true, error: null);
    try {
      await op(api);
      return true;
    } catch (e) {
      if (ref.mounted) state = (busy: state.busy, error: describe(e));
      return false;
    } finally {
      _inFlight--;
      if (ref.mounted) state = (busy: _inFlight > 0, error: state.error);
    }
  }

  /// A refusal a panel finds itself (an exposure out of range).
  void fail(String message) => state = (busy: state.busy, error: message);
}

final polarAlignActionProvider =
    NotifierProvider.autoDispose<PolarAlignActionNotifier, PolarAlignAction>(
      PolarAlignActionNotifier.new,
    );
