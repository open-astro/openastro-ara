import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/client_error_log.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/widgets/help_dialog.dart';
import 'package:package_info_plus/package_info_plus.dart';

class _FakeSavedServerService implements SavedServerService {
  @override
  Future<List<AraServer>> loadAll() async =>
      const [AraServer(hostname: 'h', port: 5555)];
  @override
  Future<void> saveAll(List<AraServer> s) async {}
  @override
  Future<void> add(AraServer server) async {}
}

/// A log whose status is set by hand (no disk under a widget test).
class _MemLog extends ClientErrorLog {
  _MemLog(ClientErrorLogStatus s)
      : statusNotifier = ValueNotifier(s),
        super(
          supportDir: () async => throw const FileSystemException('no disk'),
          appVersion: () async => 'v',
        );
  final ValueNotifier<ClientErrorLogStatus> statusNotifier;
  @override
  ValueListenable<ClientErrorLogStatus> get status => statusNotifier;
}

void main() {
  setUp(() {
    PackageInfo.setMockInitialValues(
      appName: 'openastroara',
      packageName: 'org.openastro.openastroara',
      version: '0.0.1',
      buildNumber: '37',
      buildSignature: '',
    );
  });

  Future<void> open(WidgetTester tester, ClientErrorLog log) async {
    await tester.pumpWidget(ProviderScope(
      overrides: [
        savedServerServiceProvider.overrideWithValue(_FakeSavedServerService()),
        clientErrorLogProvider.overrideWithValue(log),
      ],
      child: MaterialApp(
        home: Scaffold(
          body: Builder(
            builder: (context) => TextButton(
              onPressed: () => showHelpDialog(context),
              child: const Text('help'),
            ),
          ),
        ),
      ),
    ));
    await tester.tap(find.text('help'));
    await tester.pumpAndSettle();
    expect(find.text('Help / Report a bug'), findsOneWidget);
  }

  testWidgets('shows the client log line in the dialog', (tester) async {
    final log = _MemLog(ClientErrorLogStatus(
      entries: 2,
      sessionEntries: 1,
      lastKind: 'flutter_error',
      lastAt: DateTime(2026, 9, 27, 14, 11),
      lastMessage: 'Failed assertion: descendant',
    ));
    await open(tester, log);
    expect(find.text('Client log'), findsOneWidget);
    expect(
      find.text('2 entries, last 2026-09-27 14:11 flutter_error: '
          'Failed assertion: descendant'),
      findsOneWidget,
    );
  });

  testWidgets('with nothing recorded the row says so', (tester) async {
    await open(tester, _MemLog(const ClientErrorLogStatus()));
    expect(find.text('no errors recorded'), findsOneWidget);
  });

  testWidgets('Copy diagnostics puts the client log summary in the payload',
      (tester) async {
    String? copied;
    tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(
      SystemChannels.platform,
      (call) async {
        if (call.method == 'Clipboard.setData') {
          copied = (call.arguments as Map)['text'] as String?;
        }
        return null;
      },
    );
    addTearDown(() => tester.binding.defaultBinaryMessenger
        .setMockMethodCallHandler(SystemChannels.platform, null));

    final log = _MemLog(ClientErrorLogStatus(
      entries: 1,
      lastKind: 'uncaught',
      lastAt: DateTime(2026, 9, 27, 14, 12),
      lastMessage: 'boom',
    ));
    await open(tester, log);
    await tester.tap(find.text('Copy diagnostics'));
    await tester.pumpAndSettle();

    expect(copied, isNotNull);
    expect(copied, contains('app version: 0.0.1a+37'));
    expect(copied,
        contains('\n  client log: 1 entry, last 2026-09-27 14:12 uncaught: boom\n'));
    expect(find.text('Diagnostics copied to clipboard.'), findsOneWidget);
  });
}
