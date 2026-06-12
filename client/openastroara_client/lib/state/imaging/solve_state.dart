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
  // Bumped on each solve() and on clear(); a solve only writes its result if it
  // is still the current generation, so a slow in-flight solve (up to the 120 s
  // timeout) that was superseded by a new capture's clear() — or by a newer
  // solve — can't overwrite the state with a result for the wrong frame.
  int _generation = 0;

  @override
  FutureOr<PlateSolveResult?> build() => null;

  Future<void> solve(AraServer server, String frameId) async {
    final gen = ++_generation;
    state = const AsyncLoading();
    try {
      final result = await PlateSolveApi(server).solve(frameId);
      if (gen == _generation) state = AsyncData(result);
    } catch (e, st) {
      if (gen == _generation) state = AsyncError(e, st);
    }
  }

  /// Reset to idle (e.g. when a new frame is captured). Also invalidates any
  /// in-flight solve so its late result is dropped.
  void clear() {
    _generation++;
    state = const AsyncData(null);
  }
}

final solveResultProvider =
    AsyncNotifierProvider<SolveResult, PlateSolveResult?>(SolveResult.new);
