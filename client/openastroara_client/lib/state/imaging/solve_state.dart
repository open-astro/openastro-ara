import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/plate_solve_result.dart';
import '../../models/server.dart';
import '../../services/plate_solve_api.dart';

/// §18.I — holds the plate-solve outcome for the Imaging tab's Solve action.
///
/// `AsyncData(null)` is the idle state (nothing solved yet); `AsyncLoading`
/// while ASTAP runs; `AsyncData(result)` with the solution (which may itself be
/// `success: false` = solver ran, no solution); `AsyncError` for a transport /
/// configuration failure. Uses [AsyncNotifier] for consistency with the rest of
/// the app's async state.
class SolveResult extends AsyncNotifier<PlateSolveResult?> {
  @override
  FutureOr<PlateSolveResult?> build() => null;

  Future<void> solve(AraServer server, String frameId) async {
    state = const AsyncLoading();
    try {
      state = AsyncData(await PlateSolveApi(server).solve(frameId));
    } catch (e, st) {
      state = AsyncError(e, st);
    }
  }

  /// Reset to idle (e.g. when a new frame is captured).
  void clear() => state = const AsyncData(null);
}

final solveResultProvider =
    AsyncNotifierProvider<SolveResult, PlateSolveResult?>(SolveResult.new);
