import 'dart:async';
import 'dart:typed_data';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/guide_focus.dart';
import '../../models/server.dart';
import '../../services/guide_focus_api.dart';
import '../saved_server_state.dart';
import 'autofocus_live_state.dart';

/// What the guide-camera card renders: the loop's status, the latest frame,
/// a start/stop in flight and the last request error.
class GuideFocusLive {
  final GuideFocusStatus status;
  final Uint8List? frame;
  final int frameSeq;
  final bool busy;
  final String? error;

  const GuideFocusLive({
    this.status = GuideFocusStatus.idle,
    this.frame,
    this.frameSeq = 0,
    this.busy = false,
    this.error,
  });

  static const idle = GuideFocusLive();

  GuideFocusLive copyWith({
    GuideFocusStatus? status,
    Uint8List? frame,
    int? frameSeq,
    bool? busy,
    String? error,
    bool clearError = false,
    bool clearFrame = false,
  }) =>
      GuideFocusLive(
        status: status ?? this.status,
        frame: clearFrame ? null : (frame ?? this.frame),
        frameSeq: clearFrame ? 0 : (frameSeq ?? this.frameSeq),
        busy: busy ?? this.busy,
        error: clearError ? null : (error ?? this.error),
      );
}

/// Builds a [GuideFocusClient] for a server. Overridable in tests.
final guideFocusApiFactoryProvider =
    Provider<GuideFocusClient Function(AraServer)>((ref) => GuideFocusApi.new);

/// Client bound to the active server, or null when none.
final guideFocusApiProvider = Provider<GuideFocusClient?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(guideFocusApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// Drives the guide-camera focus loop: POST start, poll status + frame on a
/// short timer while active (the way §64 Live View polls), POST stop. The
/// status is re-read once on build so a loop left running by another client
/// shows up. NOT autoDispose — a stopped loop's last readout stays visible.
class GuideFocusNotifier extends Notifier<GuideFocusLive> {
  Timer? _timer;
  int _generation = 0;
  int _consecutiveErrors = 0;
  static const _pollInterval = Duration(milliseconds: 500);
  static const _idlePollInterval = Duration(seconds: 5);

  @override
  GuideFocusLive build() {
    _generation++;
    _timer?.cancel();
    _timer = null;
    ref.onDispose(() => _timer?.cancel());
    final api = ref.watch(guideFocusApiProvider);
    if (api == null) return GuideFocusLive.idle;
    Future<void>.microtask(refresh);
    return GuideFocusLive.idle;
  }

  Future<void> refresh() async {
    final api = ref.read(guideFocusApiProvider);
    if (api == null) return;
    final gen = _generation;
    try {
      final status = await api.status();
      if (!ref.mounted || gen != _generation) return;
      _consecutiveErrors = 0;
      state = state.copyWith(status: status, clearError: true);
      if (status.hasFrame && status.seq != state.frameSeq) {
        final frame = await api.fetchFrame();
        if (!ref.mounted || gen != _generation) return;
        if (frame != null) {
          state = state.copyWith(frame: frame.bytes, frameSeq: frame.seq);
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
    final active = state.status.active;
    // Keep polling through an outage (backed off): a daemon restart must not
    // leave a stale error and a frozen status on the card.
    final base = active ? _pollInterval : _idlePollInterval;
    final delay = _consecutiveErrors == 0
        ? base
        : Duration(milliseconds: (base.inMilliseconds * (1 << _consecutiveErrors.clamp(1, 4))).clamp(500, 5000));
    _timer = Timer(delay, () => unawaited(refresh()));
  }

  Future<void> start({required double exposureSec, int? binning}) async {
    final api = ref.read(guideFocusApiProvider);
    if (api == null || state.busy) return;
    state = state.copyWith(busy: true, clearError: true, clearFrame: true);
    try {
      await api.start(exposureSec: exposureSec, binning: binning);
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
    final api = ref.read(guideFocusApiProvider);
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
}

final guideFocusProvider =
    NotifierProvider<GuideFocusNotifier, GuideFocusLive>(GuideFocusNotifier.new);
