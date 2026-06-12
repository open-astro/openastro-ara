import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/plate_solve_result.dart';
import '../../models/server.dart';
import '../../services/plate_solve_api.dart';

/// §18.I — holds the plate-solve outcome for the Imaging tab's Solve action.
///
/// `AsyncData(null)` is the idle state (nothing solved yet); `AsyncLoading`
/// while ASTAP runs; `AsyncData(result)` with the solution (which may itself be
/// `success: false` = solver ran, no solution); `AsyncError` for a transport /
/// configuration failure.
class SolveResult extends Notifier<AsyncValue<PlateSolveResult?>> {
  @override
  AsyncValue<PlateSolveResult?> build() => const AsyncData(null);

  Future<void> solve(AraServer server, String frameId) async {
    state = const AsyncLoading();
    try {
      final result = await PlateSolveApi(server).solve(frameId);
      state = AsyncData(result);
    } catch (e, st) {
      state = AsyncError(e, st);
    }
  }

  /// Reset to idle (e.g. when a new frame is captured).
  void clear() => state = const AsyncData(null);
}

final solveResultProvider =
    NotifierProvider<SolveResult, AsyncValue<PlateSolveResult?>>(
        SolveResult.new);
