import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/plate_solve_result.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/plate_solve_api.dart';
import 'package:openastroara/state/imaging/last_frame_state.dart';
import 'package:openastroara/state/imaging/solve_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/widgets/imaging/solve_panel.dart';

/// §18.I / #1150 — the Imaging tab's Solve panel: disabled without a frame,
/// busy while ASTAP runs, then the solution or the daemon's readable failure.
class _ScriptedSolver extends PlateSolveApi {
  _ScriptedSolver() : super(const AraServer(hostname: 'x', port: 1));
  final completer = Completer<PlateSolveResult>();
  @override
  Future<PlateSolveResult> solve(String id) => completer.future;
}

Future<ProviderContainer> _pump(
  WidgetTester tester,
  _ScriptedSolver solver, {
  String? frameId,
}) async {
  final container = ProviderContainer(
    overrides: [
      activeServerProvider.overrideWithValue(
        const AraServer(hostname: 'x', port: 1),
      ),
      plateSolveApiFactoryProvider.overrideWithValue((_) => solver),
      // The last-frame notifier follows the WS event stream; inert it so no
      // reconnect timer outlives the test.
      wsEventStreamProvider.overrideWithValue(null),
    ],
  );
  addTearDown(container.dispose);
  if (frameId != null) {
    container.read(lastCapturedFrameIdProvider.notifier).set(frameId);
  }
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: SolvePanel())),
    ),
  );
  await tester.pump();
  return container;
}

void main() {
  testWidgets(
    'without a captured frame the button is disabled and the hint says capture first',
    (tester) async {
      await _pump(tester, _ScriptedSolver());
      expect(find.text('Capture a frame, then solve it.'), findsOneWidget);
      final button = tester.widget<FilledButton>(find.byType(FilledButton));
      expect(button.onPressed, isNull);
    },
  );

  testWidgets('a solve shows busy, then the solution line', (tester) async {
    final solver = _ScriptedSolver();
    await _pump(tester, solver, frameId: 'frame-1');
    expect(
      find.text('Solve the captured frame for its sky coordinates.'),
      findsOneWidget,
    );

    await tester.tap(find.text('Solve frame'));
    await tester.pump();
    expect(find.text('Solving…'), findsOneWidget);
    expect(find.text('Solving with ASTAP…'), findsOneWidget);

    solver.completer.complete(
      const PlateSolveResult(
        success: true,
        ra: 5.588,
        dec: -5.391,
        orientation: 12.3,
        pixelScale: 3.11,
      ),
    );
    await tester.pump();
    expect(find.text('Solve frame'), findsOneWidget);
    // The RA/Dec formatting is coord_format's own test; here it is enough that
    // a solution line replaced the hint and carries the numbers.
    expect(find.textContaining('RA '), findsOneWidget);
    expect(find.textContaining('Dec '), findsOneWidget);
    expect(find.textContaining('Rot 12.3°'), findsOneWidget);
    expect(find.textContaining('Scale 3.11"/px'), findsOneWidget);
  });

  testWidgets('no solution gets its own line', (tester) async {
    final solver = _ScriptedSolver();
    await _pump(tester, solver, frameId: 'frame-1');
    await tester.tap(find.text('Solve frame'));
    await tester.pump();
    solver.completer.complete(const PlateSolveResult(success: false));
    await tester.pump();
    expect(find.textContaining('No solution'), findsOneWidget);
  });

  testWidgets('a configuration failure shows the daemon\'s message verbatim', (
    tester,
  ) async {
    final solver = _ScriptedSolver();
    await _pump(tester, solver, frameId: 'frame-1');
    await tester.tap(find.text('Solve frame'));
    await tester.pump();
    solver.completer.completeError(
      const PlateSolveException('Set the focal length first.'),
    );
    await tester.pump();
    expect(find.text('Set the focal length first.'), findsOneWidget);
    expect(find.text('Solve frame'), findsOneWidget, reason: 'retryable');
  });
}
