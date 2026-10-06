import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/settings/panels/imaging_plate_solve_panel.dart';
import 'package:openastroara/services/plate_solve_database_api.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/panel_save_registry.dart';
import 'package:openastroara/widgets/settings/editable_field.dart';

/// A daemon that stores the plate-solve settings and, like the real one,
/// normalises /usr/bin/astap to /usr/bin/astap_cli on write (#1215).
class _NormalisingDaemon implements HttpClientAdapter {
  Map<String, dynamic> stored = {
    'path_or_endpoint': '/usr/bin/astap_cli',
    'index_download_path': '/var/lib/astap',
  };
  final List<String> requests = [];

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    requests.add('${options.method} ${options.path}');
    if (options.method == 'PUT') {
      final sent = Map<String, dynamic>.from(options.data as Map);
      if (sent['path_or_endpoint'] == '/usr/bin/astap') {
        sent['path_or_endpoint'] = '/usr/bin/astap_cli';
      }
      stored = {...stored, ...sent};
    }
    return ResponseBody.fromString(
      jsonEncode(stored),
      200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }
}

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

  testWidgets(
    'Save shows what the daemon stored and re-reads the database status',
    (tester) async {
      // #1215 — the daemon rewrites /usr/bin/astap to /usr/bin/astap_cli on write; the
      // field used to keep showing what was typed, and the status was read once.
      final daemon = _NormalisingDaemon();
      ImagingPlateSolvePanel.apiFactory = (server) =>
          ProfileApi(server, dio: Dio()..httpClientAdapter = daemon);
      addTearDown(() => ImagingPlateSolvePanel.apiFactory = ProfileApi.new);
      var statusReads = 0;
      tester.view.physicalSize = const Size(1600, 1600);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            activeServerProvider.overrideWithValue(
              const AraServer(hostname: 'rig', port: 8080),
            ),
            plateSolveDatabaseStatusProvider.overrideWith((ref) async {
              statusReads++;
              return null;
            }),
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
      expect(statusReads, 1);

      final solverField = find.descendant(
        of: find.ancestor(
          of: find.text('Where to find the solver'),
          matching: find.byType(EditableTextRow),
        ),
        matching: find.byType(TextField),
      );
      await tester.enterText(solverField, '/usr/bin/astap');
      await tester.pump();

      final panel = tester.state(
        find.byType(ImagingPlateSolvePanel),
      ) as PanelSaveRegistration;
      // Dio's pipeline needs real timers; the widget test's fake clock never fires them.
      await tester.runAsync(panel.panelSave);
      await tester.pumpAndSettle();

      expect(daemon.requests, contains('PUT /api/v1/profile/plate-solve'));
      expect(
        tester.widget<TextField>(solverField).controller!.text,
        '/usr/bin/astap_cli',
        reason: 'the field shows what the daemon stored, not what was typed',
      );
      expect(
        statusReads,
        2,
        reason: 'the database status is re-read after Save',
      );
    },
  );
}
