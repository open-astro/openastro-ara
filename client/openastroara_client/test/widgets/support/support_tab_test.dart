import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/log_entry.dart';
import 'package:openastroara/screens/tabs/support_tab.dart';
import 'package:openastroara/services/logs_api.dart';
import 'package:openastroara/state/support/logs_state.dart';
import 'package:openastroara/widgets/support/bug_report_card.dart';
import 'package:openastroara/widgets/support/client_log_card.dart';

class _FakeLogsClient implements LogsClient {
  _FakeLogsClient({this.entries = const [], this.throwOnTail = false});

  List<LogEntry> entries;
  // #1185 — what a `before` (page-back) call returns.
  List<LogEntry> olderEntries = const [];
  bool throwOnTail;
  String? lastMinLevel;
  String? lastSubstring;
  DateTime? lastBefore;
  int tailCalls = 0;
  int downloadCalls = 0;

  @override
  Future<List<LogEntry>> tail({
    int? maxLines,
    String? minLevel,
    String? containsSubstring,
    DateTime? before,
  }) async {
    tailCalls++;
    lastMinLevel = minLevel;
    lastSubstring = containsSubstring;
    lastBefore = before;
    if (throwOnTail) throw Exception('boom');
    return before == null ? entries : olderEntries;
  }

  String? lastSavePath;

  @override
  Future<String> downloadLogTo(String savePath, {String? logFileName}) async {
    downloadCalls++;
    lastSavePath = savePath;
    return 'openastroara-20260620.log';
  }

  @override
  void close() {}
}

LogEntry _entry(String level, String message) => LogEntry(
      timestamp: DateTime.utc(2026, 6, 20, 10, 30, 0),
      level: level,
      source: 'OpenAstroAra.Server',
      message: message,
    );

Widget _host(_FakeLogsClient api) => ProviderScope(
      // overrideWith (not overrideWithValue) so ref.onDispose(api.close) still
      // runs on the fake, matching the real provider's lifecycle.
      overrides: [
        logsApiProvider.overrideWith((ref) {
          ref.onDispose(api.close);
          return api;
        }),
      ],
      child: MaterialApp(
        home: Scaffold(
          // Canned Save-As path — no platform channel under widget tests.
          body: SupportTab(savePathPicker: (_, name) async => '/tmp/$name'),
        ),
      ),
    );

void main() {
  testWidgets('renders log entries from the daemon', (tester) async {
    final api = _FakeLogsClient(entries: [
      _entry('Information', 'camera connected'),
      _entry('Warning', 'mount near meridian'),
    ]);
    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();

    expect(find.text('camera connected'), findsOneWidget);
    expect(find.text('mount near meridian'), findsOneWidget);
    expect(api.tailCalls, 1);
  });

  testWidgets('a full page offers Load older, which appends the page before the oldest entry', (tester) async {
    // #1185 — 200 entries (the page size) → the control shows; a short second page hides it.
    final full = List.generate(200, (i) => LogEntry(
          timestamp: DateTime.utc(2026, 6, 20, 10, 30, 0).subtract(Duration(seconds: i)),
          level: 'Information',
          source: 'OpenAstroAra.Server',
          message: 'entry $i',
        ));
    final api = _FakeLogsClient(entries: full)
      ..olderEntries = [
        LogEntry(
          timestamp: DateTime.utc(2026, 6, 20, 9, 0, 0),
          level: 'Warning',
          source: 'OpenAstroAra.Server',
          message: 'much older',
        ),
      ];
    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();

    final list = find.descendant(of: find.byType(ListView), matching: find.byType(Scrollable)).first;
    await tester.scrollUntilVisible(find.text('Load older'), 400, scrollable: list);
    expect(find.text('Load older'), findsOneWidget, reason: 'a full page may have older entries');
    await tester.tap(find.text('Load older'));
    await tester.pumpAndSettle();

    expect(api.tailCalls, 2);
    expect(api.lastBefore, full.last.timestamp, reason: 'the cursor is the oldest entry held');
    await tester.scrollUntilVisible(find.text('much older'), 400, scrollable: list);
    expect(find.text('much older'), findsOneWidget, reason: 'the older page is appended');
    expect(find.text('Load older'), findsNothing, reason: 'a short page means nothing older is left');
  });

  testWidgets('a short first page never offers Load older', (tester) async {
    final api = _FakeLogsClient(entries: [_entry('Information', 'only one')]);
    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();
    expect(find.text('Load older'), findsNothing);
  });

  testWidgets('empty result shows the no-entries message', (tester) async {
    await tester.pumpWidget(_host(_FakeLogsClient(entries: const [])));
    await tester.pumpAndSettle();

    expect(find.textContaining('No log entries'), findsOneWidget);
  });

  testWidgets('tail failure shows an error with retry', (tester) async {
    await tester.pumpWidget(_host(_FakeLogsClient(throwOnTail: true)));
    await tester.pumpAndSettle();

    expect(find.textContaining("Couldn't load the logs"), findsOneWidget);
    expect(find.text('Retry'), findsOneWidget);
  });

  testWidgets('changing the level filter re-tails with that min level',
      (tester) async {
    final api = _FakeLogsClient(entries: [_entry('Error', 'boom')]);
    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();
    expect(api.lastMinLevel, isNull); // "All" → no filter on first load

    await tester.tap(find.text('All'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Warning').last);
    await tester.pumpAndSettle();

    expect(api.lastMinLevel, 'Warning');
    expect(api.tailCalls, 2);
  });

  testWidgets('a failed refresh keeps the loaded entries with an error banner',
      (tester) async {
    final api = _FakeLogsClient(entries: [_entry('Information', 'kept line')]);
    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();
    expect(find.text('kept line'), findsOneWidget);

    // Next tail fails (e.g. transient transport error) — the prior entries must
    // stay visible, with an inline error rather than a full-screen takeover.
    api.throwOnTail = true;
    await tester.tap(find.byTooltip('Refresh'));
    await tester.pumpAndSettle();

    expect(find.text('kept line'), findsOneWidget);
    expect(find.textContaining("Couldn't load the logs"), findsOneWidget);
  });

  testWidgets('with no server the client log card is still offered',
      (tester) async {
    // #1111 — the client's own log is on this machine; a client-side crash
    // is exactly when there may be no rig to connect to.
    await tester.pumpWidget(ProviderScope(
      overrides: [logsApiProvider.overrideWith((ref) => null)],
      child: const MaterialApp(home: Scaffold(body: SupportTab())),
    ));
    await tester.pump();
    expect(find.text('Connect to your rig to see its logs.'), findsOneWidget);
    expect(find.byType(ClientLogCard), findsOneWidget);
    expect(find.widgetWithText(FilledButton, 'Save client log'), findsOneWidget);
  });

  testWidgets('connected: the client log card sits below the bug report card',
      (tester) async {
    await tester.pumpWidget(_host(_FakeLogsClient(entries: const [])));
    await tester.pumpAndSettle();
    final bug = tester.getTopLeft(find.byType(BugReportCard));
    final client = tester.getTopLeft(find.byType(ClientLogCard));
    expect(client.dy, greaterThan(bug.dy));
  });

  testWidgets('download button is present', (tester) async {
    await tester.pumpWidget(_host(_FakeLogsClient(entries: const [])));
    await tester.pumpAndSettle();

    expect(find.widgetWithText(FilledButton, 'Download log'), findsOneWidget);
  });
}
