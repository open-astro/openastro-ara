import 'dart:async';
import 'dart:typed_data';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/bahtinov_focus.dart';
import '../../models/server.dart';
import '../../services/bahtinov_focus_api.dart';
import '../saved_server_state.dart';
import 'autofocus_live_state.dart';

/// How the main telescope is focused on Setup → Smart Focus (#1299).
enum MainFocusMethod { autofocus, bahtinov }

/// The user's pick for this app session; null until they pick, when the card
/// follows the rig: autofocus with a focuser connected, Bahtinov without.
class MainFocusMethodNotifier extends Notifier<MainFocusMethod?> {
  @override
  MainFocusMethod? build() => null;

  void pick(MainFocusMethod method) => state = method;
}

final mainFocusMethodProvider =
    NotifierProvider<MainFocusMethodNotifier, MainFocusMethod?>(MainFocusMethodNotifier.new);

/// What the Bahtinov card renders: the readout's status, the latest frame, a
/// start/stop in flight, the last request error, and whether the user has
/// confirmed the mask is off since the last start.
class BahtinovFocusLive {
  final BahtinovFocusStatus status;
  final Uint8List? frame;
  final int frameSeq;
  /// The fitted lines for [frame] — the overlay of the sample the frame
  /// belongs to, null when it is a whole frame (nothing measured).
  final BahtinovOverlay? overlay;
  final bool busy;
  final String? error;
  final bool finished;

  const BahtinovFocusLive({
    this.status = BahtinovFocusStatus.idle,
    this.frame,
    this.frameSeq = 0,
    this.overlay,
    this.busy = false,
    this.error,
    this.finished = false,
  });

  static const idle = BahtinovFocusLive();

  BahtinovFocusLive copyWith({
    BahtinovFocusStatus? status,
    Uint8List? frame,
    int? frameSeq,
    BahtinovOverlay? overlay,
    bool? busy,
    String? error,
    bool? finished,
    bool clearError = false,
    bool clearFrame = false,
  }) =>
      BahtinovFocusLive(
        status: status ?? this.status,
        frame: clearFrame ? null : (frame ?? this.frame),
        frameSeq: clearFrame ? 0 : (frameSeq ?? this.frameSeq),
        overlay: clearFrame ? null : (overlay ?? this.overlay),
        busy: busy ?? this.busy,
        error: clearError ? null : (error ?? this.error),
        finished: finished ?? this.finished,
      );
}

/// Builds a [BahtinovFocusClient] for a server. Overridable in tests.
final bahtinovFocusApiFactoryProvider =
    Provider<BahtinovFocusClient Function(AraServer)>((ref) => BahtinovFocusApi.new);

/// Client bound to the active server, or null when none.
final bahtinovFocusApiProvider = Provider<BahtinovFocusClient?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(bahtinovFocusApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// Drives the Bahtinov readout the way [GuideFocusNotifier] drives the guide
/// loop: POST start, poll status + frame on a short timer while active, POST
/// stop. The status is re-read once on build so a readout left running by
/// another client shows up. NOT autoDispose — a stopped readout's last frame
/// stays visible.
class BahtinovFocusNotifier extends Notifier<BahtinovFocusLive> {
  Timer? _timer;
  int _generation = 0;
  int _consecutiveErrors = 0;
  static const _pollInterval = Duration(milliseconds: 500);
  static const _idlePollInterval = Duration(seconds: 5);

  @override
  BahtinovFocusLive build() {
    _generation++;
    _timer?.cancel();
    _timer = null;
    ref.onDispose(() => _timer?.cancel());
    final api = ref.watch(bahtinovFocusApiProvider);
    if (api == null) return BahtinovFocusLive.idle;
    Future<void>.microtask(refresh);
    return BahtinovFocusLive.idle;
  }

  Future<void> refresh() async {
    final api = ref.read(bahtinovFocusApiProvider);
    if (api == null) return;
    final gen = _generation;
    try {
      final status = await api.status();
      if (!ref.mounted || gen != _generation) return;
      _consecutiveErrors = 0;
      state = state.copyWith(status: status, clearError: true);
      // Against the frame's own sample, not the latest one: a sample whose
      // picture failed to render would otherwise re-fetch the same old frame
      // on every poll until a render succeeded.
      if (status.hasFrame && status.frameSeq != state.frameSeq) {
        final frame = await api.fetchFrame();
        if (!ref.mounted || gen != _generation) return;
        if (frame != null) {
          // The lines belong to one sample: draw them only over that sample's
          // crop. A frame that raced ahead of the status shows bare until the
          // next poll brings its sample.
          final latest = status.latest;
          state = BahtinovFocusLive(
            status: state.status,
            frame: frame.bytes,
            frameSeq: frame.seq,
            overlay: latest != null && latest.seq == frame.seq ? latest.overlay : null,
            busy: state.busy,
            error: state.error,
            finished: state.finished,
          );
        }
      }
    } catch (e) {
      if (!ref.mounted || gen != _generation) return;
      _consecutiveErrors++;
      state = state.copyWith(error: describeRequestError(e));
    }
    _schedule();
  }

  void _schedule() {
    _timer?.cancel();
    final base = state.status.active ? _pollInterval : _idlePollInterval;
    final delay = _consecutiveErrors == 0
        ? base
        : Duration(milliseconds: (base.inMilliseconds * (1 << _consecutiveErrors.clamp(1, 4))).clamp(500, 5000));
    _timer = Timer(delay, () => unawaited(refresh()));
  }

  Future<void> start({required double exposureSec}) async {
    final api = ref.read(bahtinovFocusApiProvider);
    if (api == null || state.busy) return;
    // A Bahtinov session keeps the card on Bahtinov after it stops, so the
    // result (and the mask-off confirmation) stays in view even with a
    // focuser connected, where the card would otherwise default to autofocus.
    ref.read(mainFocusMethodProvider.notifier).pick(MainFocusMethod.bahtinov);
    state = state.copyWith(busy: true, finished: false, clearError: true, clearFrame: true);
    try {
      await api.start(exposureSec: exposureSec);
      if (!ref.mounted) return;
      _consecutiveErrors = 0;
      state = state.copyWith(busy: false);
      await refresh();
    } catch (e) {
      if (!ref.mounted) return;
      state = state.copyWith(busy: false, error: describeRequestError(e));
    }
  }

  Future<void> stop() async {
    final api = ref.read(bahtinovFocusApiProvider);
    if (api == null || state.busy) return;
    state = state.copyWith(busy: true, clearError: true);
    _timer?.cancel();
    try {
      await api.stop();
    } catch (e) {
      if (!ref.mounted) return;
      state = state.copyWith(error: describeRequestError(e));
    }
    if (!ref.mounted) return;
    state = state.copyWith(busy: false);
    await refresh();
  }

  /// The user confirmed the mask is off: stop the readout if it still runs,
  /// and count the main telescope as focused when the last measurement sat
  /// inside the focus zone.
  Future<void> finish() async {
    // The last MEASURED frame decides: by the time the user presses Finish the
    // mask may already be off, and those frames show no spikes.
    final measured = [...state.status.recent, ?state.status.latest].where((s) => s.detected);
    final focused = measured.isNotEmpty && measured.last.withinZone;
    if (state.status.active) {
      await stop();
      if (!ref.mounted) return;
    }
    if (focused) {
      ref.read(autofocusLiveProvider.notifier).markFocusedManually();
    }
    state = state.copyWith(finished: true);
  }
}

final bahtinovFocusProvider =
    NotifierProvider<BahtinovFocusNotifier, BahtinovFocusLive>(BahtinovFocusNotifier.new);
