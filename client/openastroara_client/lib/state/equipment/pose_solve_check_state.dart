import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/pose_solve_check.dart';
import '../../models/server.dart';
import '../../services/pose_solve_check_api.dart';
import '../saved_server_state.dart';
import 'mount_state.dart';

/// Builds a [PoseSolveCheckClient] for a server. Overridable in tests.
final poseSolveCheckApiFactoryProvider =
    Provider<PoseSolveCheckClient Function(AraServer)>(
      (ref) => PoseSolveCheckApi.new,
    );

/// [PoseSolveCheckClient] bound to the active server, or null when none is saved.
final poseSolveCheckApiProvider = Provider<PoseSolveCheckClient?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(poseSolveCheckApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// How often the rig is asked again while the position is unverified: a camera
/// that frees up (or gets busy) shows or hides the button.
const poseSolveAvailabilityRefresh = Duration(seconds: 5);

/// #2173 — whether the plate-solve check can run now. Asked only while the
/// mount's position is unverified; otherwise null (no button).
final poseSolveAvailabilityProvider =
    FutureProvider.autoDispose<PoseSolveAvailability?>((ref) async {
      final unverified = ref.watch(
        mountProvider.select((s) => s.value?.positionUnverified ?? false),
      );
      final api = ref.watch(poseSolveCheckApiProvider);
      if (!unverified || api == null) return null;
      final timer = Timer(poseSolveAvailabilityRefresh, ref.invalidateSelf);
      ref.onDispose(timer.cancel);
      return api.availability();
    });

/// One run of the check (or its Sync): [running] while it goes, then its
/// [result] or the [error] it failed with.
class PoseSolveCheckRun {
  final bool running;
  final PoseSolveCheckResult? result;
  final Object? error;

  const PoseSolveCheckRun({
    required this.running,
    required this.result,
    required this.error,
  });

  static const idle = PoseSolveCheckRun(
    running: false,
    result: null,
    error: null,
  );
}

/// #2173 — the user's check and the one-tap Sync after a mismatch.
class PoseSolveCheckNotifier extends Notifier<PoseSolveCheckRun> {
  @override
  PoseSolveCheckRun build() {
    // A new unverified spell starts with no old result on show.
    ref.watch(
      mountProvider.select((s) => s.value?.positionUnverified ?? false),
    );
    return PoseSolveCheckRun.idle;
  }

  /// One plate solve compared with the mount (no motion).
  Future<void> check() => _run((api) => api.check());

  /// Sync the mount to the sky the last check found, then verify it again.
  Future<void> sync() async {
    final id = state.result?.checkId;
    if (id == null) return;
    await _run((api) => api.sync(id));
  }

  Future<void> _run(
    Future<PoseSolveCheckResult> Function(PoseSolveCheckClient api) call,
  ) async {
    final api = ref.read(poseSolveCheckApiProvider);
    if (api == null || state.running) return;
    state = const PoseSolveCheckRun(running: true, result: null, error: null);
    try {
      final result = await call(api);
      if (!ref.mounted) return;
      state = PoseSolveCheckRun(running: false, result: result, error: null);
    } catch (e) {
      if (!ref.mounted) return;
      state = PoseSolveCheckRun(running: false, result: null, error: e);
    }
    // The trust may have changed: show the mount's row and the button anew.
    unawaited(ref.read(mountProvider.notifier).refresh());
    ref.invalidate(poseSolveAvailabilityProvider);
  }
}

final poseSolveCheckProvider =
    NotifierProvider<PoseSolveCheckNotifier, PoseSolveCheckRun>(
      PoseSolveCheckNotifier.new,
    );
