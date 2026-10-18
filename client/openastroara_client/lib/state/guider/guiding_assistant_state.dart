import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/guiding_assistant.dart';
import '../../models/server.dart';
import '../../services/guiding_assistant_api.dart';
import '../focus/autofocus_live_state.dart' show describeRequestError;
import '../saved_server_state.dart';

/// Builds a [GuidingAssistantClient] for a server. Overridable in tests.
final guidingAssistantApiFactoryProvider =
    Provider<GuidingAssistantClient Function(AraServer)>(
      (ref) => GuidingAssistantApi.new,
    );

/// The suggestion keys Accept wrote on the last completed run. Root-scoped
/// (the dialog's own state is autoDispose) so closing and reopening the
/// dialog keeps the "· applied" marks and does not re-tick what was already
/// written; a new Start clears it (review of #1387).
class GuidingAssistantAppliedNotifier extends Notifier<List<String>> {
  @override
  List<String> build() {
    // A different server has a different guider: nothing applied there yet.
    ref.watch(activeServerProvider);
    return const [];
  }

  void set(List<String> keys) => state = keys;
  void clear() => state = const [];
}

final guidingAssistantAppliedProvider =
    NotifierProvider<GuidingAssistantAppliedNotifier, List<String>>(
      GuidingAssistantAppliedNotifier.new,
    );

final guidingAssistantApiProvider = Provider<GuidingAssistantClient?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(guidingAssistantApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// What the Guiding Assistant dialog shows: the guider's status, an in-flight
/// action and the last error. What Apply wrote lives in
/// [guidingAssistantAppliedProvider], which outlives the dialog.
class GuidingAssistantState {
  final GuidingAssistantStatus status;
  final bool loaded;
  final bool busy;
  final String? error;
  const GuidingAssistantState({
    this.status = GuidingAssistantStatus.disconnected,
    this.loaded = false,
    this.busy = false,
    this.error,
  });

  GuidingAssistantState copyWith({
    GuidingAssistantStatus? status,
    bool? loaded,
    bool? busy,
    String? error,
    bool clearError = false,
  }) => GuidingAssistantState(
    status: status ?? this.status,
    loaded: loaded ?? this.loaded,
    busy: busy ?? this.busy,
    error: clearError ? null : (error ?? this.error),
  );
}

/// Polls the assistant's status once a second while it measures and every
/// few seconds otherwise (autoDispose: closing the dialog stops it), and runs
/// start / stop / apply, re-reading after each.
class GuidingAssistantNotifier extends Notifier<GuidingAssistantState> {
  Timer? _timer;
  int _generation = 0;
  static const runningPollInterval = Duration(seconds: 1);
  static const idlePollInterval = Duration(seconds: 4);

  @override
  GuidingAssistantState build() {
    _generation++;
    _timer?.cancel();
    _timer = null;
    ref.onDispose(() => _timer?.cancel());
    final api = ref.watch(guidingAssistantApiProvider);
    if (api == null) return const GuidingAssistantState();
    Future<void>.microtask(refresh);
    return const GuidingAssistantState();
  }

  Future<void> refresh() async {
    final api = ref.read(guidingAssistantApiProvider);
    if (api == null) return;
    final gen = _generation;
    try {
      final status = await api.getStatus();
      if (!ref.mounted || gen != _generation) return;
      // A run seen in progress is a new run, whoever started it (another
      // client, the guider's page): what Accept wrote belongs to the last one.
      if (status.isRunning) {
        ref.read(guidingAssistantAppliedProvider.notifier).clear();
      }
      state = state.copyWith(status: status, loaded: true, clearError: true);
    } catch (e) {
      if (!ref.mounted || gen != _generation) return;
      state = state.copyWith(loaded: true, error: describeRequestError(e));
    }
    _schedule();
  }

  void _schedule() {
    _timer?.cancel();
    final delay = state.status.isRunning
        ? runningPollInterval
        : idlePollInterval;
    _timer = Timer(delay, () => unawaited(refresh()));
  }

  Future<void> _act(
    Future<GuidingAssistantStatus?> Function(GuidingAssistantClient api) op,
  ) async {
    final api = ref.read(guidingAssistantApiProvider);
    if (api == null || state.busy) return;
    // A timer poll already in flight must not land after the action's status
    // and show the old state (idle right after Start) or re-arm the idle timer.
    _generation++;
    _timer?.cancel();
    state = state.copyWith(busy: true, clearError: true);
    try {
      final status = await op(api);
      if (!ref.mounted) return;
      state = state.copyWith(busy: false, status: status, loaded: true);
      await refresh();
    } catch (e) {
      if (!ref.mounted) return;
      state = state.copyWith(busy: false, error: describeRequestError(e));
      // The timer was cancelled above; a refused action (Stop on a run that
      // just finished, a Start that timed out client-side) must not end the
      // polling, or the guider's real state never reaches the screen.
      _schedule();
    }
  }

  Future<void> start({double? durationSec, required bool measureBacklash}) =>
      _act((api) async {
        ref.read(guidingAssistantAppliedProvider.notifier).clear();
        return api.start(
          durationSec: durationSec,
          measureBacklash: measureBacklash,
        );
      });

  Future<void> stop() => _act((api) => api.stop());

  Future<void> apply(List<String> which) => _act((api) async {
    // Taken before the await: the marks belong to the root provider, so a
    // dialog closed while Accept is in flight still records what was written
    // (this notifier is gone by then, its ref unusable).
    final marks = ref.read(guidingAssistantAppliedProvider.notifier);
    marks.set(await api.apply(which));
    return null;
  });
}

final guidingAssistantProvider =
    NotifierProvider.autoDispose<
      GuidingAssistantNotifier,
      GuidingAssistantState
    >(GuidingAssistantNotifier.new);
