import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/bug_report_preparation.dart';
import 'package:openastroara/services/bug_report_api.dart';
import 'package:openastroara/state/support/bug_report_state.dart';
import 'package:openastroara/widgets/support/bug_report_card.dart';

class _FakeBugReportClient implements BugReportClient {
  _FakeBugReportClient({this.throwOnPrepare = false});

  bool throwOnPrepare;
  int prepareCalls = 0;
  int downloadCalls = 0;
  String? lastDownloadId;

  @override
  Future<BugReportPreparation> prepare() async {
    prepareCalls++;
    if (throwOnPrepare) throw Exception('prepare boom');
    return const BugReportPreparation(
      preparationId: 'abc-123',
      status: 'ready',
      estimatedSizeBytes: 262144,
    );
  }

  String? lastSavePath;

  @override
  Future<String> downloadTo(String savePath, String preparationId) async {
    downloadCalls++;
    lastDownloadId = preparationId;
    lastSavePath = savePath;
    return 'bugreport-x.zip';
  }

  @override
  void close() {}
}

Widget _host(_FakeBugReportClient api) => ProviderScope(
      overrides: [
        bugReportApiProvider.overrideWith((ref) {
          ref.onDispose(api.close);
          return api;
        }),
      ],
      child: MaterialApp(
        home: Scaffold(
          // A canned Save-As path stands in for the OS dialog (no platform
          // channel under widget tests); the streaming download receives it.
          body: BugReportCard(
            savePathPicker: (_, name) async => '/tmp/$name',
          ),
        ),
      ),
    );

void main() {
  testWidgets('renders the prepare action', (tester) async {
    await tester.pumpWidget(_host(_FakeBugReportClient()));
    expect(find.text('Send me a bug report'), findsOneWidget);
    expect(find.widgetWithText(FilledButton, 'Prepare & download'), findsOneWidget);
  });

  testWidgets('prepare then cancel does not download', (tester) async {
    final api = _FakeBugReportClient();
    await tester.pumpWidget(_host(api));

    await tester.tap(find.widgetWithText(FilledButton, 'Prepare & download'));
    // Explicit pumps (not pumpAndSettle) — the busy spinner animates while the
    // dialog is open, so pumpAndSettle would never settle.
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));

    // The PII disclosure dialog is shown before any download.
    expect(find.text('Before you share this bug report'), findsOneWidget);
    expect(find.textContaining('full profile'), findsOneWidget);
    expect(api.prepareCalls, 1);
    expect(api.downloadCalls, 0);

    await tester.tap(find.widgetWithText(TextButton, 'Cancel'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));

    expect(api.downloadCalls, 0); // cancel → no download
  });

  testWidgets('confirming the disclosure downloads with the prepared id',
      (tester) async {
    final api = _FakeBugReportClient();
    await tester.pumpWidget(_host(api));

    await tester.tap(find.widgetWithText(FilledButton, 'Prepare & download'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));
    await tester.tap(find.widgetWithText(FilledButton, 'Download anyway'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));

    expect(api.downloadCalls, 1);
    expect(api.lastDownloadId, 'abc-123');
    expect(api.lastSavePath, '/tmp/openastroara-bug-report.zip',
        reason: 'the picked path is chosen BEFORE the download and streamed to');
  });

  testWidgets('a prepare failure shows an error and never opens the dialog',
      (tester) async {
    final api = _FakeBugReportClient(throwOnPrepare: true);
    await tester.pumpWidget(_host(api));

    await tester.tap(find.widgetWithText(FilledButton, 'Prepare & download'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));

    expect(find.text('Before you share this bug report'), findsNothing);
    expect(find.textContaining('Bug report failed'), findsOneWidget);
    expect(api.downloadCalls, 0);
  });
}
