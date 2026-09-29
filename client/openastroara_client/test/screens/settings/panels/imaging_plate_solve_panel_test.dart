import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/settings/panels/imaging_plate_solve_panel.dart';
import 'package:openastroara/services/plate_solve_database_api.dart';
import 'package:openastroara/state/saved_server_state.dart';

// #1121 — Settings -> Plate solving shows whether the daemon has a star
// database. Three states: files found, none (the fresh-install exit-32 state),
// and unknown (offline, or a daemon older than GET /platesolve/database).

Future<void> _pump(
  WidgetTester tester,
  PlateSolveDatabaseStatus? status,
) async {
  // The settings pane is a wide desktop surface, and the test's Ahem font
  // renders every glyph at full point size, so the panel's fixed 280px labels
  // overflow in ways real fonts don't (the safety_site_panel tests' approach).
  tester.view.physicalSize = const Size(1600, 1600);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        activeServerProvider.overrideWithValue(null),
        plateSolveDatabaseStatusProvider.overrideWith((ref) async => status),
      ],
      child: MaterialApp(
        builder: (context, child) => MediaQuery(
          data: MediaQuery.of(context)
              .copyWith(textScaler: const TextScaler.linear(0.5)),
          child: child!,
        ),
        home: const Scaffold(body: ImagingPlateSolvePanel()),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('files found: count, path and databases', (tester) async {
    await _pump(
      tester,
      const PlateSolveDatabaseStatus(
        configuredPath: '/var/lib/astap',
        effectivePath: '/var/lib/astap',
        fileCount: 1476,
        databases: ['d80'],
        solverPath: '/usr/bin/astap_cli',
        solverFound: true,
      ),
    );

    expect(find.text('Star database'), findsOneWidget);
    expect(
      find.text('1476 files found in /var/lib/astap (D80)'),
      findsOneWidget,
    );
    expect(find.textContaining('No star database'), findsNothing);
    expect(find.textContaining('Solver not found'), findsNothing);
  });

  testWidgets('no database: says so and points at the install step', (
    tester,
  ) async {
    await _pump(
      tester,
      const PlateSolveDatabaseStatus(
        configuredPath: '/var/lib/astap',
        effectivePath: null,
        fileCount: 0,
        databases: [],
        solverPath: '/usr/bin/astap_cli',
        solverFound: false,
      ),
    );

    expect(find.text('No star database in /var/lib/astap'), findsOneWidget);
    expect(find.textContaining('DEPLOY.md step 3'), findsOneWidget);
    expect(find.text('Solver not found at /usr/bin/astap_cli'), findsOneWidget);
  });

  testWidgets('blank solver path: says none is set, not "not found at "', (
    tester,
  ) async {
    // An imported profile has its solver path stripped to "" (ProfileShareService).
    await _pump(
      tester,
      const PlateSolveDatabaseStatus(
        configuredPath: '/var/lib/astap',
        effectivePath: null,
        fileCount: 0,
        databases: [],
        solverPath: '',
        solverFound: false,
      ),
    );

    expect(find.text('No solver path set'), findsOneWidget);
    expect(find.textContaining('Solver not found at'), findsNothing);
  });

  testWidgets('unknown: offline or an older daemon is not an error', (
    tester,
  ) async {
    await _pump(tester, null);

    expect(
      find.text(
        "Unknown (not connected, or the rig's server is too old to report it)",
      ),
      findsOneWidget,
    );
    expect(find.textContaining('No star database'), findsNothing);
  });

  test('fromJson reads the daemon wire shape', () {
    final s = PlateSolveDatabaseStatus.fromJson({
      'configured_path': '/var/lib/astap',
      'effective_path': null,
      'file_count': 0,
      'databases': <dynamic>[],
      'solver_path': '/usr/bin/astap_cli',
      'solver_found': true,
    });
    expect(s.configuredPath, '/var/lib/astap');
    expect(s.effectivePath, isNull);
    expect(s.hasDatabase, isFalse);
    expect(s.solverFound, isTrue);
  });
}
