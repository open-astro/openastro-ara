import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/screens/wizard/wizard_screens.dart';
import 'package:openastroara/services/guider_api.dart';
import 'package:openastroara/state/guider/guider_state.dart';

/// The connection under test is DAEMON→PHD2 (the SBC's network), so the fake
/// records what host:port the daemon was asked to reach and scripts the
/// resulting link state.
class _FakeGuider implements GuiderClient {
  String? lastHost;
  int? lastPort;
  bool connectedAfter = true;

  @override
  Future<void> connect(
      {String host = kDefaultGuiderHost, int port = kDefaultGuiderPort}) async {
    lastHost = host;
    lastPort = port;
  }

  @override
  Future<GuiderStatus?> getStatus() async => GuiderStatus(
        name: 'PHD2',
        connectionState: connectedAfter
            ? GuiderConnectionState.connected
            : GuiderConnectionState.disconnected,
        runtimeState: GuiderRuntimeState.stopped,
      );

  @override
  void noSuchMethod(Invocation invocation) =>
      throw UnimplementedError('${invocation.memberName}');
}

void main() {
  Future<void> pump(WidgetTester tester, GuiderClient? api) async {
    final container = ProviderContainer(overrides: [
      guiderApiProvider.overrideWithValue(api),
    ]);
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
          home: Scaffold(body: Builder(builder: (c) => wizardScreenBuilders[10]!(c)))),
    ));
    await tester.pump();
  }

  testWidgets('Test connection asks the DAEMON to reach the entered host:port',
      (tester) async {
    final api = _FakeGuider();
    await pump(tester, api);

    // The user's SBC case: PHD2 on another machine, non-default port.
    await tester.enterText(find.byType(TextField).first, 'sbc.local:8080');
    await tester.ensureVisible(find.text('Test connection'));
    await tester.tap(find.text('Test connection'));
    await tester.pump(const Duration(milliseconds: 600));
    await tester.pumpAndSettle();

    expect(api.lastHost, 'sbc.local');
    expect(api.lastPort, 8080);
    expect(find.textContaining('Connected to PHD2 at sbc.local:8080'),
        findsOneWidget);
  });

  testWidgets('an unreachable PHD2 reports a hint instead of hanging',
      (tester) async {
    final api = _FakeGuider()..connectedAfter = false;
    await pump(tester, api);

    await tester.ensureVisible(find.text('Test connection'));
    await tester.tap(find.text('Test connection'));
    // The poll gives up after 20 × 500 ms.
    await tester.pump(const Duration(seconds: 11));
    await tester.pumpAndSettle();

    expect(api.lastPort, 4400, reason: 'blank field falls back to the default');
    expect(find.textContaining('No PHD2 answered'), findsOneWidget);
    expect(find.textContaining('Enable Server'), findsOneWidget);
  });

  testWidgets('offline explains the server does the talking', (tester) async {
    await pump(tester, null);
    await tester.ensureVisible(find.text('Test connection'));
    await tester.tap(find.text('Test connection'));
    await tester.pumpAndSettle();
    expect(find.textContaining('the server is what talks to PHD2'),
        findsOneWidget);
  });
}
