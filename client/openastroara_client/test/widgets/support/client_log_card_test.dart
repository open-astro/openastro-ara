import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/client_error_log.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/widgets/support/client_log_card.dart';

/// In-memory stand-in: real file IO never completes inside a widget test's
/// fake-async zone, so exportTo just records the path it was asked for.
class _MemLog extends ClientErrorLog {
  _MemLog()
      : super(
          supportDir: () async => throw const FileSystemException('no disk'),
          appVersion: () async => 'v',
        );
  final exports = <String>[];
  bool failExport = false;
  final statusNotifier = ValueNotifier(const ClientErrorLogStatus());

  @override
  ValueListenable<ClientErrorLogStatus> get status => statusNotifier;

  @override
  Future<int> exportTo(String path) async {
    if (failExport) throw const FileSystemException('disk full');
    exports.add(path);
    return 42;
  }
}

Widget _host(_MemLog log, {String? Function(String name)? picker, bool simple = false}) =>
    ProviderScope(
      overrides: [
        clientErrorLogProvider.overrideWithValue(log),
        if (simple) simpleModeActiveProvider.overrideWithValue(true),
      ],
      child: MaterialApp(
        home: Scaffold(
          body: ClientLogCard(
            savePathPicker: (_, name) async =>
                picker == null ? '/tmp/$name' : picker(name),
          ),
        ),
      ),
    );

void main() {
  testWidgets('the action moves under the text in a phone-width column', (tester) async {
    // Inline in Simple Mode's Settings on a phone, the column is this narrow.
    tester.view.physicalSize = const Size(360, 800);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(_host(_MemLog()));

    expect(tester.takeException(), isNull, reason: 'nothing overflows');
    final action = find.widgetWithText(FilledButton, 'Save client log');
    expect(tester.getTopLeft(action).dy, greaterThan(tester.getBottomLeft(find.text("This app's error log")).dy),
        reason: 'the action sits under the text');
  });

  testWidgets('renders with no server and no errors recorded', (tester) async {
    await tester.pumpWidget(_host(_MemLog()));
    expect(find.text("This app's error log"), findsOneWidget);
    expect(find.textContaining('No errors recorded'), findsOneWidget);
    expect(find.widgetWithText(FilledButton, 'Save client log'), findsOneWidget);
  });

  testWidgets('the status line follows the log', (tester) async {
    final log = _MemLog();
    await tester.pumpWidget(_host(log));
    log.statusNotifier.value = const ClientErrorLogStatus(
      entries: 3,
      sessionEntries: 1,
    );
    await tester.pump();
    expect(find.textContaining('3 errors recorded (1 since launch)'),
        findsOneWidget);
    log.statusNotifier.value = const ClientErrorLogStatus(
      entries: 1,
      sessionEntries: 1,
      suppressedRepeats: 40,
    );
    await tester.pump();
    expect(find.textContaining('1 error recorded (1 since launch, 40 suppressed)'),
        findsOneWidget);
    log.statusNotifier.value = const ClientErrorLogStatus(available: false);
    await tester.pump();
    expect(find.textContaining('could not be written'), findsOneWidget);
  });

  testWidgets('Save exports to the picked path and confirms', (tester) async {
    final log = _MemLog();
    await tester.pumpWidget(_host(log));
    await tester.tap(find.widgetWithText(FilledButton, 'Save client log'));
    await tester.pumpAndSettle();
    expect(log.exports, ['/tmp/${ClientLogCard.suggestedFileName}']);
    expect(find.textContaining('Saved /tmp/openastroara-client.log'),
        findsOneWidget);
  });

  testWidgets('cancelling the picker exports nothing', (tester) async {
    final log = _MemLog();
    await tester.pumpWidget(_host(log, picker: (_) => null));
    await tester.tap(find.widgetWithText(FilledButton, 'Save client log'));
    await tester.pumpAndSettle();
    expect(log.exports, isEmpty);
    expect(find.byType(SnackBar), findsNothing);
  });

  testWidgets('an export failure shows an error, not a crash', (tester) async {
    final log = _MemLog()..failExport = true;
    await tester.pumpWidget(_host(log));
    await tester.tap(find.widgetWithText(FilledButton, 'Save client log'));
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);
    expect(find.byType(SnackBar), findsOneWidget);
    expect(find.textContaining('Saved'), findsNothing);
  });

  testWidgets('in Simple Mode a save and a failure are pills, not snackbars', (tester) async {
    final log = _MemLog();
    await tester.pumpWidget(_host(log, simple: true));
    List<SimpleMessage> pills() =>
        ProviderScope.containerOf(tester.element(find.byType(ClientLogCard))).read(simpleMessagesProvider);
    await tester.tap(find.text('Save client log'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));
    expect(find.byType(SnackBar), findsNothing);
    expect(pills().single.title, 'Saved /tmp/${ClientLogCard.suggestedFileName}');
    expect(pills().single.kind, SimpleMessageKind.result);

    log.failExport = true;
    await tester.tap(find.text('Save client log'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));
    expect(find.byType(SnackBar), findsNothing);
    expect(pills().first.kind, SimpleMessageKind.error);
    await tester.pump(kSimpleResultLife); // the pill's timer
  });
}
