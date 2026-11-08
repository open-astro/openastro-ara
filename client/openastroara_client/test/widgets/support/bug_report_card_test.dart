import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'dart:io';

import 'package:openastroara/models/bug_report_preparation.dart';
import 'package:openastroara/services/bug_report_api.dart';
import 'package:openastroara/services/client_error_log.dart';
import 'package:openastroara/state/support/bug_report_state.dart';
import 'package:openastroara/widgets/support/bug_report_card.dart';

/// In-memory client log: exportTo records the path instead of touching disk
/// (real file IO never completes inside a widget test's fake-async zone).
class _MemLog extends ClientErrorLog {
  _MemLog()
      : super(
          supportDir: () async => throw const FileSystemException('no disk'),
          appVersion: () async => 'v',
        );
  final exports = <String>[];
  bool fail = false;

  @override
  Future<int> exportTo(String path) async {
    if (fail) throw const FileSystemException('disk full');
    exports.add(path);
    return 1;
  }
}

class _FakeBugReportClient implements BugReportClient {
  _FakeBugReportClient({this.throwOnPrepare = false});

  bool throwOnPrepare;
  int prepareCalls = 0;
  int downloadCalls = 0;
  int statusCalls = 0;
  String? lastDownloadId;
  String? lastMode;
  DateTime? lastSince;
  bool sinceSent = false;

  /// What prepare answers first and then each status poll, in order; empty =
  /// ready at once.
  List<BugReportPreparation> script = [];

  @override
  Future<BugReportPreparation> prepare({String mode = 'full', DateTime? since}) async {
    prepareCalls++;
    lastMode = mode;
    lastSince = since;
    sinceSent = since != null;
    if (throwOnPrepare) throw Exception('prepare boom');
    if (script.isNotEmpty) return script.removeAt(0);
    return BugReportPreparation(
      preparationId: 'abc-123',
      status: 'ready',
      estimatedSizeBytes: 262144,
      sharingMode: mode, // the daemon echoes the mode it built with
    );
  }

  @override
  Future<BugReportPreparation> status(String preparationId) async {
    statusCalls++;
    return script.removeAt(0);
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

/// A fresh directory per host, so `uniquePathIn` (which checks the real
/// filesystem) can't be steered by a leftover file in a shared temp dir.
Directory _freshDir() {
  final d = Directory.systemTemp.createTempSync('bug_report_card');
  addTearDown(() => d.deleteSync(recursive: true));
  return d;
}

Widget _host(_FakeBugReportClient api,
        {ClientErrorLog? log, Directory? dir, Duration packingTimeout = const Duration(minutes: 15)}) =>
    ProviderScope(
      overrides: [
        bugReportApiProvider.overrideWith((ref) {
          ref.onDispose(api.close);
          return api;
        }),
        clientErrorLogProvider.overrideWithValue(log ?? _MemLog()),
      ],
      child: MaterialApp(
        home: Scaffold(
          // A canned Save-As path stands in for the OS dialog (no platform
          // channel under widget tests); the streaming download receives it.
          body: BugReportCard(
            savePathPicker: (_, name) async => '${(dir ?? _freshDir()).path}/$name',
            pollInterval: const Duration(milliseconds: 10),
            packingTimeout: packingTimeout,
          ),
        ),
      ),
    );

void main() {
  testWidgets('the prepare action moves under the text in a phone-width column', (tester) async {
    // Inline in Simple Mode's Settings on a phone, the column is this narrow.
    tester.view.physicalSize = const Size(360, 800);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(_host(_FakeBugReportClient()));

    expect(tester.takeException(), isNull, reason: 'nothing overflows');
    final action = find.widgetWithText(FilledButton, 'Prepare & download');
    expect(tester.getTopLeft(action).dy, greaterThan(tester.getBottomLeft(find.text('Send me a bug report')).dy),
        reason: 'the action sits under the text');
  });

  testWidgets('renders the prepare action', (tester) async {
    await tester.pumpWidget(_host(_FakeBugReportClient()));
    expect(find.text('Send me a bug report'), findsOneWidget);
    expect(find.widgetWithText(FilledButton, 'Prepare & download'), findsOneWidget);
  });

  testWidgets('the sharing mode is sent to prepare; everything is the default', (tester) async {
    // §54.3 — the mode is chosen before the bundle is built.
    final api = _FakeBugReportClient();
    await tester.pumpWidget(_host(api));
    expect(find.text('Include everything'), findsOneWidget, reason: 'the spec default');

    // Explicit pumps, as in the test below: the busy spinner animates while the dialog is open.
    await tester.tap(find.widgetWithText(FilledButton, 'Prepare & download'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));
    expect(api.lastMode, 'full');
    await tester.tap(find.widgetWithText(TextButton, 'Cancel'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));

    await tester.tap(find.byType(DropdownButton<String>));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Redact all sensitive items').last);
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Prepare & download'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));
    expect(api.lastMode, 'redacted');
    expect(api.prepareCalls, 2);
    // The PII gate's wording follows the mode the daemon built with.
    expect(find.textContaining('scrubbed'), findsNWidgets(2), reason: 'profile and system-info bullets say what was scrubbed');
    expect(find.textContaining('username scrubbed from its path'), findsOneWidget);
    expect(find.textContaining('full profile'), findsNothing);
    await tester.tap(find.widgetWithText(TextButton, 'Cancel'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));
  });

  testWidgets('coarse GPS mode says the site is rounded', (tester) async {
    final api = _FakeBugReportClient();
    await tester.pumpWidget(_host(api));
    await tester.tap(find.byType(DropdownButton<String>));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Coarse GPS only').last);
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Prepare & download'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));
    expect(api.lastMode, 'coarse_gps');
    expect(find.textContaining('rounded to ~111 km'), findsOneWidget);
    expect(find.textContaining('full profile'), findsNothing);
    await tester.tap(find.widgetWithText(TextButton, 'Cancel'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));
  });

  testWidgets('prepare then cancel does not download', (tester) async {
    final api = _FakeBugReportClient();
    await tester.pumpWidget(_host(api));

    await tester.tap(find.widgetWithText(FilledButton, 'Prepare & download'));
    // Explicit pumps (not pumpAndSettle) — the busy spinner animates while the
    // dialog is open, so pumpAndSettle would never settle.
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));

    // The PII disclosure dialog is shown before any download; full mode names the full profile.
    expect(find.text('Before you share this bug report'), findsOneWidget);
    expect(find.textContaining('full profile'), findsOneWidget);
    expect(find.textContaining('scrubbed'), findsNothing);
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
    expect(api.lastSavePath, endsWith('/openastroara-bug-report.zip'),
        reason: 'the picked path is chosen BEFORE the download and streamed to');
  });

  testWidgets('the client log is saved beside the ZIP and named in the toast',
      (tester) async {
    // #1111 — the bundle is built on the daemon, which has never seen this
    // app's own failures; the client log rides along as a sibling file.
    final api = _FakeBugReportClient();
    final log = _MemLog();
    final dir = _freshDir();
    await tester.pumpWidget(_host(api, log: log, dir: dir));

    await tester.tap(find.widgetWithText(FilledButton, 'Prepare & download'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));
    expect(find.textContaining("this app's own error log"), findsOneWidget);
    await tester.tap(find.widgetWithText(FilledButton, 'Download anyway'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));

    // uniquePathIn joins with the platform separator (backslash on Windows).
    expect(log.exports, hasLength(1));
    expect(File(log.exports.single).parent.path, dir.path);
    expect(log.exports.single, endsWith('openastroara-client.log'));
    expect(
      find.text('Saved bugreport-x.zip and openastroara-client.log'),
      findsOneWidget,
    );
  });

  testWidgets('a client-log export failure does not fail the bug report',
      (tester) async {
    final api = _FakeBugReportClient();
    final log = _MemLog()..fail = true;
    await tester.pumpWidget(_host(api, log: log));

    await tester.tap(find.widgetWithText(FilledButton, 'Prepare & download'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));
    await tester.tap(find.widgetWithText(FilledButton, 'Download anyway'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));

    expect(api.downloadCalls, 1);
    expect(find.text('Saved bugreport-x.zip'), findsOneWidget);
    expect(find.textContaining('failed'), findsNothing);
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

  testWidgets('last night is the default: the whole rig since 24 h ago; Ara-only sends no range',
      (tester) async {
    final api = _FakeBugReportClient();
    await tester.pumpWidget(_host(api));
    expect(find.text('Last night (whole rig)'), findsOneWidget);

    await tester.tap(find.widgetWithText(FilledButton, 'Prepare & download'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));
    expect(api.sinceSent, isTrue);
    final ago = DateTime.now().toUtc().difference(api.lastSince!);
    expect(ago.inMinutes, closeTo(24 * 60, 2));
    await tester.tap(find.widgetWithText(TextButton, 'Cancel'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));

    await tester.tap(find.byType(DropdownButton<int>));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Ara logs only').last);
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Prepare & download'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));
    expect(api.sinceSent, isFalse);
    await tester.tap(find.widgetWithText(TextButton, 'Cancel'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));
  });

  testWidgets('a whole-rig bundle is polled while it packs, then the disclosure lists every source',
      (tester) async {
    final api = _FakeBugReportClient()
      ..script = [
        const BugReportPreparation(preparationId: 'rig-1', status: 'preparing', estimatedSizeBytes: 0),
        const BugReportPreparation(preparationId: 'rig-1', status: 'preparing', estimatedSizeBytes: 50 * 1024 * 1024),
        const BugReportPreparation(
          preparationId: 'rig-1',
          status: 'ready',
          estimatedSizeBytes: 12 * 1024 * 1024,
          sources: [
            BugReportSourceSummary(source: 'ara', files: 2, bytes: 3 * 1024 * 1024),
            BugReportSourceSummary(source: 'alpacabridge', files: 2, bytes: 40 * 1024 * 1024),
            BugReportSourceSummary(source: 'guider', files: 3, bytes: 128 * 1024 * 1024, truncated: true),
            BugReportSourceSummary(source: 'journal', files: 5, bytes: 2 * 1024 * 1024,
                note: 'journalctl reported limited permissions'),
          ],
        ),
      ];
    await tester.pumpWidget(_host(api));

    await tester.tap(find.widgetWithText(FilledButton, 'Prepare & download'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 5));
    expect(find.textContaining('Packing the logs on the rig'), findsOneWidget);
    await tester.pump(const Duration(milliseconds: 50));
    await tester.pump(const Duration(milliseconds: 50));
    await tester.pump(const Duration(milliseconds: 400));

    expect(api.statusCalls, 2, reason: 'polled until ready');
    expect(find.text('Before you share this bug report'), findsOneWidget);
    expect(find.textContaining('AlpacaBridge logs'), findsOneWidget);
    expect(find.textContaining('guider (PHD2)'), findsOneWidget);
    expect(find.textContaining('cut to their newest part'), findsOneWidget);
    expect(find.textContaining('limited permissions'), findsOneWidget);
    await tester.tap(find.widgetWithText(FilledButton, 'Download anyway'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));
    expect(api.lastDownloadId, 'rig-1');
  });

  testWidgets('a bundle that fails to pack says why and never offers a download', (tester) async {
    final api = _FakeBugReportClient()
      ..script = [
        const BugReportPreparation(preparationId: 'rig-2', status: 'preparing', estimatedSizeBytes: 0),
        const BugReportPreparation(preparationId: 'rig-2', status: 'failed', estimatedSizeBytes: 0, error: 'the drive is full'),
      ];
    await tester.pumpWidget(_host(api));

    await tester.tap(find.widgetWithText(FilledButton, 'Prepare & download'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 50));
    await tester.pump(const Duration(milliseconds: 400));

    expect(find.text('Before you share this bug report'), findsNothing);
    expect(find.textContaining('the drive is full'), findsOneWidget);
    expect(api.downloadCalls, 0);
  });

  testWidgets('a bundle still packing past the timeout says so and never offers a download', (tester) async {
    final api = _FakeBugReportClient()
      ..script = List.filled(
          20, const BugReportPreparation(preparationId: 'rig-3', status: 'preparing', estimatedSizeBytes: 0),
          growable: true);
    await tester.pumpWidget(_host(api, packingTimeout: Duration.zero));

    await tester.tap(find.widgetWithText(FilledButton, 'Prepare & download'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 50));
    await tester.pump(const Duration(milliseconds: 400));

    expect(find.text('Before you share this bug report'), findsNothing);
    expect(find.textContaining('the rig is still packing the logs'), findsOneWidget);
    expect(api.downloadCalls, 0);
  });
}
