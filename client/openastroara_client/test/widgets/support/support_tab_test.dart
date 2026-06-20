import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/log_entry.dart';
import 'package:openastroara/screens/tabs/support_tab.dart';
import 'package:openastroara/services/logs_api.dart';
import 'package:openastroara/state/support/logs_state.dart';

class _FakeLogsClient implements LogsClient {
  _FakeLogsClient({this.entries = const [], this.throwOnTail = false});

  List<LogEntry> entries;
  bool throwOnTail;
  String? lastMinLevel;
  String? lastSubstring;
  int tailCalls = 0;
  int downloadCalls = 0;

  @override
  Future<List<LogEntry>> tail({
    int? maxLines,
    String? minLevel,
    String? containsSubstring,
  }) async {
    tailCalls++;
    lastMinLevel = minLevel;
    lastSubstring = containsSubstring;
    if (throwOnTail) throw Exception('boom');
    return entries;
  }

  @override
  Future<LogDownload> downloadLog({String? logFileName}) async {
    downloadCalls++;
    return (bytes: Uint8List(0), fileName: 'openastroara-20260620.log');
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
      overrides: [logsApiProvider.overrideWithValue(api)],
      child: const MaterialApp(home: Scaffold(body: SupportTab())),
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

  testWidgets('empty result shows the no-entries message', (tester) async {
    await tester.pumpWidget(_host(_FakeLogsClient(entries: const [])));
    await tester.pumpAndSettle();

    expect(find.textContaining('No log entries'), findsOneWidget);
  });

  testWidgets('tail failure shows an error with retry', (tester) async {
    await tester.pumpWidget(_host(_FakeLogsClient(throwOnTail: true)));
    await tester.pumpAndSettle();

    expect(find.textContaining('Could not load logs'), findsOneWidget);
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

  testWidgets('download button is present', (tester) async {
    await tester.pumpWidget(_host(_FakeLogsClient(entries: const [])));
    await tester.pumpAndSettle();

    expect(find.widgetWithText(FilledButton, 'Download log'), findsOneWidget);
  });
}
