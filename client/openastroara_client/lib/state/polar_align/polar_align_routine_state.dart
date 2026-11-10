import 'dart:async';
import 'dart:typed_data';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/polar_align.dart';
import '../../services/polar_align_api.dart';
import 'polar_align_state.dart';

/// The Polar Align Routine both presentations render (ADR 0013 rule 3): the
/// profile's settings, the routine camera's frame and the panel's busy and
/// error state. The live phase itself is [polarAlignLiveProvider]; a panel
/// opening hydrates it through [PolarAlignFrameNotifier.hydrate].

/// An exposure the routine takes: more than 0 and at most 60 seconds.
bool polarExposureValid(double? seconds) =>
    seconds != null && seconds > 0 && seconds <= 60;

/// The profile's polar-align settings (exposure, Loop/Single, tolerance). The
/// running routine re-reads them before every frame, so a save applies to the
/// next one; a failed save rolls back, so a panel never shows an exposure or
/// mode the rig is not using.
class PolarAlignSettingsNotifier extends AsyncNotifier<PolarAlignSettings> {
  @override
  Future<PolarAlignSettings> build() async {
    final api = ref.watch(polarAlignApiProvider);
    if (api == null) return const PolarAlignSettings();
    try {
      return await api.getSettings();
    } catch (_) {
      return const PolarAlignSettings();
    }
  }

  Future<void> save(PolarAlignSettings next) async {
    final api = ref.read(polarAlignApiProvider);
    final previous = state.value ?? const PolarAlignSettings();
    state = AsyncData(next);
    if (api == null) return;
    try {
      state = AsyncData(await api.putSettings(next));
    } catch (_) {
      state = AsyncData(previous);
      rethrow;
    }
  }
}

final polarAlignSettingsProvider =
    AsyncNotifierProvider<PolarAlignSettingsNotifier, PolarAlignSettings>(
      PolarAlignSettingsNotifier.new,
    );

/// The routine camera's latest frame, fetched after each finished frame; null
/// until there is one. A failed fetch keeps the previous picture. Fetches are
/// coalesced: a frame that finishes while one is in flight is fetched once
/// after it, or Single would keep showing the older image. Not in the rough
/// stage: its frames are the main camera's, and the guider has none to show.
class PolarAlignFrameNotifier extends Notifier<Uint8List?> {
  bool _loading = false;
  bool _reload = false;

  @override
  Uint8List? build() {
    ref.listen(polarAlignLiveProvider, (prev, next) {
      if (next.stage != PolarAlignStages.coarse &&
          next.lastFrame != null &&
          !identical(prev?.lastFrame, next.lastFrame)) {
        unawaited(load());
      }
    });
    // A frame already finished before anything watched: fetch it too.
    final live = ref.read(polarAlignLiveProvider);
    if (live.lastFrame != null && live.stage != PolarAlignStages.coarse) {
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
        status.stage != PolarAlignStages.coarse &&
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

  /// Run [op] against the rig; [describe] words a failure. True when the rig
  /// took it.
  Future<bool> run(
    Future<void> Function(PolarAlignClient api) op, {
    required String Function(Object error) describe,
  }) async {
    final api = ref.read(polarAlignApiProvider);
    if (api == null) return false;
    state = (busy: true, error: null);
    try {
      await op(api);
      return true;
    } catch (e) {
      if (ref.mounted) state = (busy: state.busy, error: describe(e));
      return false;
    } finally {
      if (ref.mounted) state = (busy: false, error: state.error);
    }
  }

  /// A refusal a panel finds itself (an exposure out of range).
  void fail(String message) => state = (busy: state.busy, error: message);
}

final polarAlignActionProvider =
    NotifierProvider.autoDispose<PolarAlignActionNotifier, PolarAlignAction>(
      PolarAlignActionNotifier.new,
    );
