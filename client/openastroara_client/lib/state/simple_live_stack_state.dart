import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/server.dart';
import '../services/session_live_stack_api.dart';
import '../services/simple_mode_prefs_service.dart';
import 'saved_server_state.dart';

/// One status request per this while the stack is on.
const kSimpleLiveStackPoll = Duration(seconds: 4);

/// While the camera is absent (`no_camera`) or the first probe failed: how
/// often to ask again, so a camera that connects later shows the row.
const kSimpleLiveStackReprobe = Duration(seconds: 30);

/// Builds the client for the active rig; overridden in tests.
final sessionLiveStackApiFactoryProvider =
    Provider<SessionLiveStackClient Function(AraServer)>(
      (ref) =>
          (server) => SessionLiveStackApi(server),
    );

/// The view choice, per client: its flag is "Last light", so a missing file
/// reads as Live Stack. Overridden in tests.
final simpleLiveStackViewPrefsProvider = Provider<SimpleModePrefsService>(
  (ref) =>
      SimpleModePrefsService(fileName: 'simple_live_stack_last_light.json'),
);

/// What the Queue screen fills the screen with while the stack is on.
enum SimpleLiveStackView { stack, lastLight }

/// The Session Live Stack as Simple Mode sees it: the daemon's status, the
/// stacked frame it holds, the view choice and the last error. A null
/// [status] is "unknown": nothing answered yet.
class SimpleLiveStack {
  const SimpleLiveStack({
    this.status,
    this.frame,
    this.view = SimpleLiveStackView.stack,
    this.error,
  });

  final SessionLiveStackStatus? status;
  final StackFrame? frame;
  final SimpleLiveStackView view;
  final Object? error;

  bool get supported => status?.supported ?? false;

  /// The daemon's switch, as the daemon reports it.
  bool get on => supported && status!.enabled;

  SimpleLiveStack copyWith({
    SessionLiveStackStatus? status,
    StackFrame? Function()? frame,
    SimpleLiveStackView? view,
    Object? Function()? error,
  }) => SimpleLiveStack(
    status: status ?? this.status,
    frame: frame != null ? frame() : this.frame,
    view: view ?? this.view,
    error: error != null ? error() : this.error,
  );
}

/// The stacked frame fills the screen only while the stack is on, chosen and
/// holds a frame; otherwise the shell shows the last light.
bool simpleStackShown(SimpleLiveStack s) =>
    s.on && s.view == SimpleLiveStackView.stack && s.frame != null;

/// The daemon owns the switch: this reads `GET live-stack` and fetches the
/// frame when `stack_seq` changes; it keeps no on/off flag of its own. The
/// timer lives here, so it ends with the last watcher.
class SimpleLiveStackController extends Notifier<SimpleLiveStack> {
  SessionLiveStackClient? _client;
  Timer? _timer;
  Duration? _armed;
  bool _inFlight = false;
  bool _disposed = false;
  bool _viewTouched = false;

  @override
  SimpleLiveStack build() {
    final server = ref.watch(activeServerProvider);
    _disposed = false;
    // No watcher left: the timer goes at once, not when the provider is
    // disposed a frame later. A watcher coming back re-arms it.
    ref.onCancel(_disarm);
    ref.onResume(_arm);
    ref.onDispose(() {
      _disposed = true;
      _disarm();
      _client?.close();
    });
    if (server == null) return const SimpleLiveStack();
    _client = ref.read(sessionLiveStackApiFactoryProvider)(server);
    unawaited(_loadView());
    Future.microtask(_poll);
    return const SimpleLiveStack();
  }

  Future<void> _loadView() async {
    final last = await ref.read(simpleLiveStackViewPrefsProvider).load();
    if (_disposed || _viewTouched) return;
    state = state.copyWith(
      view: last ? SimpleLiveStackView.lastLight : SimpleLiveStackView.stack,
    );
  }

  Future<void> setView(SimpleLiveStackView v) async {
    _viewTouched = true;
    state = state.copyWith(view: v);
    await ref
        .read(simpleLiveStackViewPrefsProvider)
        .save(v == SimpleLiveStackView.lastLight);
  }

  Future<void> setOn(bool on) => _control(on ? _client!.start : _client!.stop);

  Future<void> reset() => _control(_client!.reset);

  /// A control call, then the daemon's next status: a failed call leaves the
  /// state as the daemon last reported it and records the error.
  Future<void> _control(Future<void> Function() call) async {
    if (_client == null) return;
    try {
      await call();
    } catch (e) {
      if (!_disposed) state = state.copyWith(error: () => e);
      return;
    }
    await _poll();
  }

  /// One status read; a timer tick skips while the previous one is in flight.
  Future<void> _poll({bool tick = false}) async {
    final client = _client;
    if (client == null || _disposed || (tick && _inFlight)) return;
    _inFlight = true;
    try {
      final status = await client.status();
      var frame = state.frame;
      if (!status.supported || !status.enabled || status.lightsStacked == 0) {
        frame = null;
      } else if (frame == null || frame.seq != status.stackSeq) {
        frame = await client.frame() ?? frame;
      }
      if (_disposed) return;
      state = state.copyWith(
        status: status,
        frame: () => frame,
        error: () => null,
      );
    } catch (e) {
      if (!_disposed) state = state.copyWith(error: () => e);
    } finally {
      _inFlight = false;
    }
    if (!_disposed) _arm();
  }

  void _disarm() {
    _timer?.cancel();
    _timer = null;
    _armed = null;
  }

  /// Polls while on; a slow re-probe while the camera is absent or nothing has
  /// answered yet; no timer otherwise (one probe is enough).
  void _arm() {
    final s = state;
    final st = s.status;
    final Duration? period = st == null || st.unsupported == 'no_camera'
        ? kSimpleLiveStackReprobe
        : s.on
        ? kSimpleLiveStackPoll
        : null;
    if (period == _armed) return;
    _disarm();
    _armed = period;
    _timer = period == null
        ? null
        : Timer.periodic(period, (_) => _poll(tick: true));
  }
}

final simpleLiveStackProvider =
    NotifierProvider.autoDispose<SimpleLiveStackController, SimpleLiveStack>(
      SimpleLiveStackController.new,
    );
