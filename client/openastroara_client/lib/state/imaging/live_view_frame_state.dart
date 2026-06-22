import 'dart:async';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../services/live_view_api.dart';
import '../saved_server_state.dart';

/// The live frame the §64 viewer renders, plus the loop's run state. [jpeg] is
/// null until the first frame of the current session arrives; gate "live" on
/// [active] (a stopped/never-started loop reports active=false).
class LiveFrameState {
  final Uint8List? jpeg;
  final int seq;
  final int session;
  final bool active;
  final String? error;

  const LiveFrameState({
    this.jpeg,
    this.seq = 0,
    this.session = 0,
    this.active = false,
    this.error,
  });

  static const idle = LiveFrameState();

  LiveFrameState copyWith({
    Uint8List? jpeg,
    int? seq,
    int? session,
    bool? active,
    String? error,
    bool clearError = false,
    bool clearJpeg = false,
  }) =>
      LiveFrameState(
        jpeg: clearJpeg ? null : (jpeg ?? this.jpeg),
        seq: seq ?? this.seq,
        session: session ?? this.session,
        active: active ?? this.active,
        error: clearError ? null : (error ?? this.error),
      );
}

/// Builds a [LiveViewClient] for a server. Overridable in tests with a fake.
final liveViewApiFactoryProvider =
    Provider<LiveViewClient Function(AraServer)>((ref) => LiveViewApi.new);

/// Drives the §64 Live View loop from the client: POST start, poll the frame
/// endpoint on a short timer (change-detecting on session+seq so an unchanged
/// frame isn't re-published), POST stop. Single in-flight timer; cancelled on
/// stop and dispose.
class LiveViewFrameNotifier extends Notifier<LiveFrameState> {
  Timer? _timer;
  LiveViewClient? _api;
  int _consecutiveErrors = 0;
  static const _pollInterval = Duration(milliseconds: 250);
  static const _maxBackoff = Duration(seconds: 5);

  @override
  LiveFrameState build() {
    ref.onDispose(_teardown);
    return LiveFrameState.idle;
  }

  void _teardown() {
    _timer?.cancel();
    _timer = null;
    _api?.close();
    _api = null;
  }

  AraServer? _activeServer() =>
      ref.read(savedServersProvider).maybeWhen(
            data: (list) => list.isEmpty ? null : list.last,
            orElse: () => null,
          );

  /// Start the loop with the given exposure/gain/binning, then begin polling.
  Future<void> start({
    required double exposureSec,
    int? gain,
    int binX = 2,
    int binY = 2,
  }) async {
    final server = _activeServer();
    if (server == null) {
      state = const LiveFrameState(error: 'Not connected to a server.');
      return;
    }
    _timer?.cancel();
    _api?.close();
    _consecutiveErrors = 0;
    final api = ref.read(liveViewApiFactoryProvider)(server);
    _api = api;
    try {
      await api.start(exposureSec: exposureSec, gain: gain, binX: binX, binY: binY);
    } catch (e) {
      _api = null;
      api.close();
      state = LiveFrameState(error: _describe(e));
      return;
    }
    state = const LiveFrameState(active: true);
    _scheduleNext();
  }

  /// Stop the loop and clear the live frame.
  Future<void> stop() async {
    _timer?.cancel();
    _timer = null;
    final api = _api;
    _api = null;
    state = LiveFrameState.idle;
    if (api != null) {
      try {
        await api.stop();
      } catch (_) {
        // Best-effort: the server self-stops on disconnect anyway.
      }
      api.close();
    }
  }

  void _scheduleNext() {
    // Back off after consecutive errors so a sustained server failure (camera
    // dropped mid-session) doesn't hammer the endpoint — and re-render the
    // viewer — at the full 4 Hz; capped at _maxBackoff.
    final delay = _consecutiveErrors == 0
        ? _pollInterval
        : Duration(
            milliseconds: (_pollInterval.inMilliseconds * (1 << _consecutiveErrors.clamp(1, 5)))
                .clamp(_pollInterval.inMilliseconds, _maxBackoff.inMilliseconds));
    _timer = Timer(delay, () async {
      final api = _api;
      if (api == null || !state.active) return;
      try {
        final f = await api.fetchFrame();
        // A stop() (or a new start) can race in during the await — don't write a
        // stale frame onto the idle/next-session state.
        if (_api != api || !state.active) return;
        _consecutiveErrors = 0;
        if (f != null && (f.session != state.session || f.seq != state.seq)) {
          state = state.copyWith(
              jpeg: f.bytes, seq: f.seq, session: f.session, clearError: true);
        }
      } catch (e) {
        if (_api != api || !state.active) return;
        _consecutiveErrors++;
        state = state.copyWith(error: _describe(e));
      }
      // Re-arm only while still live (stop() nulls _api and flips active).
      if (state.active && _api != null) {
        _scheduleNext();
      }
    });
  }

  /// A short, user-facing message — never the multi-line DioException dump.
  static String _describe(Object e) {
    if (e is DioException) {
      final code = e.response?.statusCode;
      if (code != null) return 'server returned $code';
      return e.message ?? 'network error';
    }
    return e.toString();
  }
}

final liveViewFrameProvider =
    NotifierProvider<LiveViewFrameNotifier, LiveFrameState>(
        LiveViewFrameNotifier.new);
