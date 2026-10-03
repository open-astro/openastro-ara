import 'dart:async';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/autofocus_run.dart';
import '../../models/ws_event.dart';
import '../equipment/focuser_state.dart';
import '../ws/ws_providers.dart';

/// `autofocus.*` WS event tokens (mirror `WsEventCatalog` on the server).
abstract final class AutofocusWsEvents {
  static const started = 'autofocus.started';
  static const shotComplete = 'autofocus.shot_complete';
  static const fallbackClassic = 'autofocus.fallback_classic';
  static const stepComplete = 'autofocus.step_complete';
  static const curveFit = 'autofocus.curve_fit';
  static const completed = 'autofocus.completed';
  static const failed = 'autofocus.failed';
  static const collimationVerdict = 'autofocus.collimation_verdict';

  static bool isAutofocus(String type) => type.startsWith('autofocus.');
}

/// What the Focusing pane's main-telescope card renders: the daemon's run
/// record, the rendered frame that goes with it, and the client-side bits
/// (a start/cancel in flight, the last request error).
class AutofocusLive {
  final AutofocusRun run;
  final Uint8List? frame;
  final int frameSeq;
  final bool busy;
  final String? error;

  /// True once a run has completed in this app session — the OAG gate for the
  /// guide-camera card. A failed/cancelled run does not count.
  final bool focusedThisSession;

  const AutofocusLive({
    this.run = AutofocusRun.idle,
    this.frame,
    this.frameSeq = 0,
    this.busy = false,
    this.error,
    this.focusedThisSession = false,
  });

  static const idle = AutofocusLive();

  AutofocusLive copyWith({
    AutofocusRun? run,
    Uint8List? frame,
    int? frameSeq,
    bool? busy,
    String? error,
    bool clearError = false,
    bool clearFrame = false,
    bool? focusedThisSession,
  }) =>
      AutofocusLive(
        run: run ?? this.run,
        frame: clearFrame ? null : (frame ?? this.frame),
        frameSeq: clearFrame ? 0 : (frameSeq ?? this.frameSeq),
        busy: busy ?? this.busy,
        error: clearError ? null : (error ?? this.error),
        focusedThisSession: focusedThisSession ?? this.focusedThisSession,
      );
}

/// Pure: fold a run snapshot into the live view (frame handled separately).
/// Exposed for unit tests.
AutofocusLive applyRunSnapshot(AutofocusLive current, AutofocusRun run) =>
    current.copyWith(
      run: run,
      focusedThisSession: current.focusedThisSession || run.isComplete,
      // A new run drops the previous run's picture until its own arrives.
      clearFrame: run.isRunning && !run.hasFrame,
    );

/// Drives the main-telescope card. The daemon's record is the source of truth:
/// every `autofocus.*` event is a change notification that triggers a REST
/// re-read (plus a 2 s poll while a run is in progress, in case the stream
/// skipped), and the frame is fetched whenever its sequence moves. NOT
/// autoDispose — a finished run stays visible when the user comes back.
class AutofocusLiveNotifier extends Notifier<AutofocusLive> {
  Timer? _poll;
  Timer? _debounce;
  bool _refreshing = false;
  int _generation = 0;
  static const _pollInterval = Duration(seconds: 2);
  static const _debounceDelay = Duration(milliseconds: 150);

  @override
  AutofocusLive build() {
    _generation++;
    _poll?.cancel();
    _poll = null;
    _debounce?.cancel();
    _debounce = null;
    ref.onDispose(() {
      _poll?.cancel();
      _debounce?.cancel();
    });
    final api = ref.watch(autofocusApiProvider);
    if (api == null) return AutofocusLive.idle;
    final stream = ref.watch(wsEventStreamProvider);
    if (stream != null) {
      ref.listen(wsEventsProvider, (prev, next) {
        final event = next.asData?.value;
        if (event != null && AutofocusWsEvents.isAutofocus(event.type)) {
          _onEvent(event);
        }
      });
    }
    // Hydrate once the server is known; the pane also calls refresh() on open.
    Future<void>.microtask(refresh);
    return AutofocusLive.idle;
  }

  void _onEvent(WsEvent event) {
    // Coalesce a burst (step_complete + curve_fit + completed) into one read.
    _debounce?.cancel();
    _debounce = Timer(_debounceDelay, () => unawaited(refresh()));
  }

  /// Re-read the run record (and the frame when its sequence moved).
  Future<void> refresh() async {
    if (_refreshing) return;
    final api = ref.read(autofocusApiProvider);
    if (api == null) return;
    final gen = _generation;
    _refreshing = true;
    try {
      final run = await api.state();
      if (!ref.mounted || gen != _generation) return;
      state = applyRunSnapshot(state, run).copyWith(clearError: true);
      if (run.hasFrame && run.frameSeq != state.frameSeq) {
        final frame = await api.fetchFrame();
        if (!ref.mounted || gen != _generation) return;
        if (frame != null) {
          state = state.copyWith(frame: frame.bytes, frameSeq: run.frameSeq);
        }
      }
      _armPoll(run.isRunning);
    } catch (e) {
      if (!ref.mounted || gen != _generation) return;
      state = state.copyWith(error: describeRequestError(e));
      _armPoll(state.run.isRunning);
    } finally {
      _refreshing = false;
    }
  }

  void _armPoll(bool running) {
    _poll?.cancel();
    _poll = null;
    if (!running) return;
    _poll = Timer(_pollInterval, () => unawaited(refresh()));
  }

  /// Start a run (the focuser endpoint's job). The record flips to `running`
  /// on the next refresh.
  Future<void> start() async {
    final api = ref.read(autofocusApiProvider);
    if (api == null || state.busy) return;
    state = state.copyWith(busy: true, clearError: true);
    try {
      await api.start();
      if (!ref.mounted) return;
      state = state.copyWith(busy: false);
      await refresh();
    } catch (e) {
      if (!ref.mounted) return;
      state = state.copyWith(busy: false, error: describeRequestError(e));
    }
  }

  /// Cancel the run in progress (whoever started it).
  Future<void> cancel() async {
    final api = ref.read(autofocusApiProvider);
    if (api == null || state.busy) return;
    state = state.copyWith(busy: true, clearError: true);
    try {
      await api.cancel();
      if (!ref.mounted) return;
      state = state.copyWith(busy: false);
      await refresh();
    } catch (e) {
      if (!ref.mounted) return;
      state = state.copyWith(busy: false, error: describeRequestError(e));
    }
  }

  /// The user says the main telescope is already in focus (manual focus, or a
  /// run from an earlier app session) — satisfies the OAG gate.
  void markFocusedManually() {
    state = state.copyWith(focusedThisSession: true);
  }
}

/// A short, user-facing message for a failed request — the daemon's Problem
/// `detail`/`title` when it sent one, never the DioException dump.
String describeRequestError(Object e) {
  if (e is DioException) {
    final data = e.response?.data;
    if (data is Map) {
      final detail = data['detail'];
      if (detail is String && detail.isNotEmpty) return detail;
      final title = data['title'];
      if (title is String && title.isNotEmpty) return title;
    }
    final code = e.response?.statusCode;
    if (code != null) return 'server returned $code';
    return e.message ?? 'network error';
  }
  return e.toString();
}

final autofocusLiveProvider =
    NotifierProvider<AutofocusLiveNotifier, AutofocusLive>(
        AutofocusLiveNotifier.new);
