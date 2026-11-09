import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'dart:io';

import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/bug_report_preparation.dart';
import 'package:openastroara/services/bug_report_api.dart';
import 'package:openastroara/services/client_error_log.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/support/bug_report_state.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/widgets/support/bug_report_card.dart';
import 'package:openastroara/widgets/support/support_card_row.dart';

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
        {ClientErrorLog? log, Directory? dir, Duration packingTimeout = const Duration(minutes: 15), bool simple = false, Locale? locale}) =>
    ProviderScope(
      overrides: [
        if (simple) simpleModeActiveProvider.overrideWithValue(true),
        bugReportApiProvider.overrideWith((ref) {
          ref.onDispose(api.close);
          return api;
        }),
        clientErrorLogProvider.overrideWithValue(log ?? _MemLog()),
      ],
      child: MaterialApp(
        locale: locale,
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          // A canned Save-As path stands in for the OS dialog (no platform
          // channel under widget tests); the streaming download receives it.
          body: SingleChildScrollView(child: BugReportCard(
            savePathPicker: (_, name) async => '${(dir ?? _freshDir()).path}/$name',
            pollInterval: const Duration(milliseconds: 10),
            packingTimeout: packingTimeout,
          )),
        ),
      ),
    );

void main() {
  testWidgets('large text moves the action under the text in a column that fits it at 1x', (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        home: MediaQuery(
          data: const MediaQueryData(size: Size(800, 600), textScaler: TextScaler.linear(2)),
          child: Scaffold(
            body: Align(
              alignment: Alignment.topLeft,
              child: SizedBox(
                width: 640,
                child: SupportCardRow(
                  icon: const Icon(Icons.bug_report),
                  body: (_) => const Text('Body', key: Key('body')),
                  action: FilledButton(onPressed: () {}, child: const Text('Act', key: Key('act'))),
                ),
              ),
            ),
          ),
        ),
      ),
    );
    expect(tester.getTopLeft(find.byKey(const Key('act'))).dy, greaterThan(tester.getBottomLeft(find.byKey(const Key('body'))).dy));
  });

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

  testWidgets("the full UI's Sharing does not offer the app's error log", (tester) async {
    await tester.pumpWidget(_host(_FakeBugReportClient()));
    await tester.tap(find.byType(DropdownButton<String>));
    await tester.pumpAndSettle();
    expect(find.text("This app's error log"), findsNothing);
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

  group('Simple Mode', () {
    void expectNoPopup(WidgetTester tester) {
      expect(tester.state<NavigatorState>(find.byType(Navigator)).canPop(), isFalse, reason: 'no dialog or dropdown route');
      expect(find.byType(SnackBar), findsNothing);
    }

    List<SimpleMessage> pills(WidgetTester tester) =>
        ProviderScope.containerOf(tester.element(find.byType(BugReportCard))).read(simpleMessagesProvider);

    Future<void> settle(WidgetTester tester) async {
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 400));
    }

    Future<void> tapPrepare(WidgetTester tester) async {
      tester.view.physicalSize = const Size(800, 1600);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      await tester.tap(find.widgetWithText(FilledButton, 'Prepare & download'));
      await settle(tester);
    }

    testWidgets('the review opens scrolled in, focused and announced, in Brazilian Portuguese', (tester) async {
      tester.view.physicalSize = const Size(800, 300);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(_host(_FakeBugReportClient(), simple: true, locale: const Locale('pt', 'BR')));
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, 'Preparar e baixar'));
      await settle(tester);
      const title = 'Antes de compartilhar este relatório de erro';
      expect(find.text(title), findsOneWidget);
      expect(find.text('Baixar mesmo assim'), findsOneWidget);
      expect(tester.takeAnnouncements().map((a) => a.message), contains(title));
      final top = tester.getRect(find.text(title)).top;
      expect(top, inInclusiveRange(0, 300), reason: 'taller than the view, it keeps its title in sight');
      await tester.tap(find.byKey(const Key('bug-report-review-cancel')));
      await settle(tester);
    });

    testWidgets('the review is inline and Download anyway downloads, ending in a pill', (tester) async {
      final api = _FakeBugReportClient();
      await tester.pumpWidget(_host(api, simple: true));
      await tapPrepare(tester);
      expectNoPopup(tester);
      expect(find.text('Before you share this bug report'), findsOneWidget);
      expect(api.downloadCalls, 0, reason: 'nothing before the review is answered');
      await tester.ensureVisible(find.byKey(const Key('bug-report-review-confirm')));
      await settle(tester);
      await tester.tap(find.byKey(const Key('bug-report-review-confirm')));
      await settle(tester);
      expectNoPopup(tester);
      expect(api.downloadCalls, 1);
      expect(find.text('Before you share this bug report'), findsNothing);
      expect(pills(tester).single.title, startsWith('Saved bugreport-x.zip'));
      await tester.pump(kSimpleResultLife); // the pill's timer
    });

    testWidgets('Cancel on the inline review downloads nothing', (tester) async {
      final api = _FakeBugReportClient();
      await tester.pumpWidget(_host(api, simple: true));
      await tapPrepare(tester);
      await tester.ensureVisible(find.byKey(const Key('bug-report-review-cancel')));
      await settle(tester);
      await tester.tap(find.byKey(const Key('bug-report-review-cancel')));
      await settle(tester);
      expect(api.downloadCalls, 0);
      expect(find.text('Before you share this bug report'), findsNothing);
    });

    testWidgets('In Simple Mode Covers and Sharing are combo boxes, and no radio rows', (tester) async {
      tester.view.physicalSize = const Size(800, 1600);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      final api = _FakeBugReportClient();
      await tester.pumpWidget(_host(api, simple: true));
      expect(find.byType(DropdownButton<String>), findsOneWidget);
      expect(find.byType(DropdownButton<int>), findsOneWidget);
      expect(find.byKey(const Key('bug-report-sharing-0')), findsNothing);
      expect(find.byKey(const Key('bug-report-range-0')), findsNothing);
    });

    testWidgets("Sharing offers this app's error log, and picking it saves that log", (tester) async {
      tester.view.physicalSize = const Size(800, 1600);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      final log = _MemLog();
      await tester.pumpWidget(_host(_FakeBugReportClient(), simple: true, log: log));
      await tester.tap(find.byType(DropdownButton<String>));
      await tester.pumpAndSettle();
      await tester.tap(find.text("This app's error log").last);
      await tester.pumpAndSettle();

      expect(find.byType(DropdownButton<int>), findsNothing, reason: 'Covers is about the rig logs');
      await tester.tap(find.widgetWithText(FilledButton, 'Save client log'));
      await settle(tester);
      expect(log.exports, hasLength(1));
    });

    testWidgets('a failure is an error pill', (tester) async {
      await tester.pumpWidget(_host(_FakeBugReportClient(throwOnPrepare: true), simple: true));
      await tapPrepare(tester);
      expectNoPopup(tester);
      final pill = pills(tester).single;
      expect(pill.kind, SimpleMessageKind.error);
      expect(pill.title, startsWith('Bug report failed'));
    });
  });

  testWidgets('the Full UI still reviews in a dialog', (tester) async {
    await tester.pumpWidget(_host(_FakeBugReportClient()));
    await tester.tap(find.widgetWithText(FilledButton, 'Prepare & download'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));
    expect(find.byType(AlertDialog), findsOneWidget);
    expect(find.byType(DropdownButton<String>), findsOneWidget);
  });

  testWidgets("the card's own words are in Brazilian Portuguese", (tester) async {
    await tester.pumpWidget(_host(_FakeBugReportClient(), simple: true, locale: const Locale('pt', 'BR')));
    await tester.pumpAndSettle();
    for (final words in ['Enviar um relatório de erro', 'Abrange: ', 'Compartilhar: ', 'Última noite (setup inteiro)', 'Incluir tudo', 'Preparar e baixar']) {
      expect(find.text(words), findsOneWidget, reason: words);
    }
    expect(find.text('Send me a bug report'), findsNothing);
  });

  for (final (simple, looks) in const [(true, 'grey in Simple Mode'), (false, 'theme blue in the full UI')]) {
    testWidgets('the card icon is $looks', (tester) async {
      await tester.pumpWidget(_host(_FakeBugReportClient(), simple: simple));
      await tester.pumpAndSettle();
      final icon = tester.widget<Icon>(find.byIcon(Icons.bug_report_outlined));
      final primary = Theme.of(tester.element(find.byIcon(Icons.bug_report_outlined))).colorScheme.primary;
      expect(icon.color, simple ? AraColors.textSecondary : primary);
    });
  }

  testWidgets("Simple Mode's Covers and Sharing are at least 44 px tall", (tester) async {
    await tester.pumpWidget(_host(_FakeBugReportClient(), simple: true));
    await tester.pumpAndSettle();
    for (final d in [find.byType(DropdownButton<int>), find.byType(DropdownButton<String>)]) {
      expect(tester.getSize(d).height, greaterThanOrEqualTo(44));
    }
  });
}
