import 'dart:async';
import 'dart:typed_data';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/guider_live.dart';
import '../../models/server.dart';
import '../../services/guider_live_api.dart';
import '../focus/autofocus_live_state.dart' show describeRequestError;
import '../saved_server_state.dart';

/// Builds a [GuiderLiveClient] for a server. Overridable in tests.
final guiderLiveApiFactoryProvider =
    Provider<GuiderLiveClient Function(AraServer)>((ref) => GuiderLiveApi.new);

/// [GuiderLiveClient] bound to the active server, or null when none.
final guiderLiveApiProvider = Provider<GuiderLiveClient?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(guiderLiveApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// What the guide-camera panel shows: the daemon's view, the latest picture,
/// an in-flight action, and the last error.
class GuiderLiveState {
  final GuiderLive live;
  final Uint8List? frame;
  final bool busy;
  final String? error;
  const GuiderLiveState({
    this.live = GuiderLive.disconnected,
    this.frame,
    this.busy = false,
    this.error,
  });

  GuiderLiveState copyWith({
    GuiderLive? live,
    Uint8List? frame,
    bool? busy,
    String? error,
    bool clearError = false,
    bool clearFrame = false,
  }) => GuiderLiveState(
    live: live ?? this.live,
    frame: clearFrame ? null : (frame ?? this.frame),
    busy: busy ?? this.busy,
    error: clearError ? null : (error ?? this.error),
  );
}

/// Polls the live view and its picture about once a second while the panel is
/// open (autoDispose: closing the panel stops the polling), and runs the
/// loop / stop / select / deselect actions, re-reading right after each.
class GuiderLiveNotifier extends Notifier<GuiderLiveState> {
  Timer? _timer;
  int _generation = 0;
  static const pollInterval = Duration(milliseconds: 1000);
  static const idlePollInterval = Duration(seconds: 3);

  @override
  GuiderLiveState build() {
    _generation++;
    _timer?.cancel();
    _timer = null;
    ref.onDispose(() => _timer?.cancel());
    final api = ref.watch(guiderLiveApiProvider);
    if (api == null) return const GuiderLiveState();
    Future<void>.microtask(refresh);
    return const GuiderLiveState();
  }

  Future<void> refresh() async {
    final api = ref.read(guiderLiveApiProvider);
    if (api == null) return;
    final gen = _generation;
    try {
      final live = await api.getLive();
      if (!ref.mounted || gen != _generation) return;
      Uint8List? frame;
      if (live.hasFrame) {
        frame = await api.getFrame();
        if (!ref.mounted || gen != _generation) return;
      }
      state = state.copyWith(
        live: live,
        frame: frame,
        clearFrame: !live.hasFrame && frame == null,
        clearError: true,
      );
    } catch (e) {
      if (!ref.mounted || gen != _generation) return;
      state = state.copyWith(error: describeRequestError(e));
    }
    _schedule();
  }

  void _schedule() {
    _timer?.cancel();
    final delay = state.live.isActive ? pollInterval : idlePollInterval;
    _timer = Timer(delay, () => unawaited(refresh()));
  }

  Future<void> _act(Future<void> Function(GuiderLiveClient api) op) async {
    final api = ref.read(guiderLiveApiProvider);
    if (api == null || state.busy) return;
    state = state.copyWith(busy: true, clearError: true);
    try {
      await op(api);
      if (!ref.mounted) return;
      state = state.copyWith(busy: false);
      await refresh();
    } catch (e) {
      if (!ref.mounted) return;
      state = state.copyWith(busy: false, error: describeRequestError(e));
    }
  }

  Future<void> loop() => _act((api) => api.loop());
  Future<void> stop() => _act((api) => api.stop());
  Future<void> selectStar(double x, double y) =>
      _act((api) => api.selectStar(x, y));
  Future<void> deselectStar() => _act((api) => api.deselectStar());
}

final guiderLiveProvider =
    NotifierProvider.autoDispose<GuiderLiveNotifier, GuiderLiveState>(
  GuiderLiveNotifier.new,
);
