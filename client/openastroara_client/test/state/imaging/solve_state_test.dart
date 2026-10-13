import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/plate_solve_result.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/plate_solve_api.dart';
import 'package:openastroara/state/imaging/solve_state.dart';

/// §18.I / #1150 — the solve state machine: idle → loading → data / error, and
/// the generation guard that drops a late result after clear() or a newer solve.
class _ScriptedSolver extends PlateSolveApi {
  _ScriptedSolver(this.answers)
    : super(const AraServer(hostname: 'x', port: 1));
  final Map<String, Completer<PlateSolveResult>> answers;
  @override
  Future<PlateSolveResult> solve(String id) =>
      (answers[id] ??= Completer<PlateSolveResult>()).future;
}

void main() {
  const server = AraServer(hostname: 'x', port: 1);

  ProviderContainer make(_ScriptedSolver solver) {
    final c = ProviderContainer(
      overrides: [
        plateSolveApiFactoryProvider.overrideWithValue((_) => solver),
      ],
    );
    addTearDown(c.dispose);
    return c;
  }

  test('idle, then loading, then the solution', () async {
    final solver = _ScriptedSolver({});
    final c = make(solver);
    expect(c.read(solveResultProvider).value, isNull);
    final done = c.read(solveResultProvider.notifier).solve(server, 'f1');
    expect(c.read(solveResultProvider).isLoading, isTrue);
    solver.answers['f1']!.complete(
      const PlateSolveResult(success: true, ra: 10, dec: 20),
    );
    await done;
    expect(c.read(solveResultProvider).value?.ra, 10);
  });

  test('a failure lands as AsyncError', () async {
    final solver = _ScriptedSolver({});
    final c = make(solver);
    final done = c.read(solveResultProvider.notifier).solve(server, 'f1');
    solver.answers['f1']!.completeError(
      const PlateSolveException('not configured'),
    );
    await done;
    expect(c.read(solveResultProvider).hasError, isTrue);
    expect(c.read(solveResultProvider).error, isA<PlateSolveException>());
  });

  test('clear() drops a late result from a superseded solve', () async {
    final solver = _ScriptedSolver({});
    final c = make(solver);
    final slow = c.read(solveResultProvider.notifier).solve(server, 'old');
    c.read(solveResultProvider.notifier).clear();
    expect(c.read(solveResultProvider).value, isNull);
    solver.answers['old']!.complete(
      const PlateSolveResult(success: true, ra: 1, dec: 2),
    );
    await slow;
    expect(
      c.read(solveResultProvider).value,
      isNull,
      reason: 'the result belongs to a frame that is no longer current',
    );
  });

  test('a newer solve wins over an older one that finishes later', () async {
    final solver = _ScriptedSolver({});
    final c = make(solver);
    final first = c.read(solveResultProvider.notifier).solve(server, 'a');
    final second = c.read(solveResultProvider.notifier).solve(server, 'b');
    solver.answers['b']!.complete(
      const PlateSolveResult(success: true, ra: 2, dec: 2),
    );
    await second;
    solver.answers['a']!.complete(
      const PlateSolveResult(success: true, ra: 1, dec: 1),
    );
    await first;
    expect(c.read(solveResultProvider).value?.ra, 2);
  });
}
