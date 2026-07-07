import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/server_api.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/ws/client_session_state.dart';
import 'package:openastroara/widgets/emergency_stop_button.dart';

/// Records §35.3 emergency-stop calls; the handshake/§27 members are unused
/// by the button.
class _FakeServerApi implements ServerApi {
  int calls = 0;
  EmergencyStopResult result = const EmergencyStopResult(
    alreadyInProgress: false,
    runsAborted: 1,
    exposureAborted: true,
    guidingStopped: true,
    parkRequested: true,
    flatPanelLightOff: true,
  );

  @override
  Future<EmergencyStopResult> emergencyStop() async {
    calls++;
    return result;
  }

  @override
  dynamic noSuchMethod(Invocation invocation) =>
      throw UnimplementedError('${invocation.memberName}');
}

class _FixedServers extends SavedServersNotifier {
  @override
  Future<List<AraServer>> build() async =>
      const [AraServer(hostname: 'pi-test', port: 5555)];
}

void main() {
  late _FakeServerApi fake;

  Future<void> pumpButton(WidgetTester tester) async {
    fake = _FakeServerApi();
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          savedServersProvider.overrideWith(_FixedServers.new),
          serverApiFactoryProvider.overrideWithValue((server) => fake),
        ],
        child: const MaterialApp(
          home: Scaffold(body: EmergencyStopButton()),
        ),
      ),
    );
    // Materialize + settle the async saved-servers load so
    // activeServerProvider resolves to the fixed server at tap time.
    final container = ProviderScope.containerOf(
        tester.element(find.byType(EmergencyStopButton)));
    await container.read(savedServersProvider.future);
    await tester.pumpAndSettle();
  }

  group('EmergencyStopButton (§35.3)', () {
    testWidgets('cancel in the confirm dialog sends nothing', (tester) async {
      await pumpButton(tester);

      await tester.tap(find.text('Emergency Stop'));
      await tester.pumpAndSettle();
      expect(find.text('Stop everything now?'), findsOneWidget);

      await tester.tap(find.text('Cancel'));
      await tester.pumpAndSettle();

      expect(fake.calls, 0, reason: 'the stop must never fire without confirmation');
    });

    testWidgets('confirming posts the stop and reports the honest result',
        (tester) async {
      await pumpButton(tester);

      await tester.tap(find.text('Emergency Stop'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('EMERGENCY STOP'));
      await tester.pumpAndSettle();

      expect(fake.calls, 1);
      expect(find.textContaining('Emergency stop executed'), findsOneWidget);
      expect(find.textContaining('1 sequence run(s) aborted'), findsOneWidget);
      expect(find.textContaining('mount told to park'), findsOneWidget);
    });

    testWidgets('an unreachable mount is called out, not papered over',
        (tester) async {
      await pumpButton(tester);
      fake.result = const EmergencyStopResult(
        alreadyInProgress: false,
        runsAborted: 0,
        exposureAborted: false,
        guidingStopped: true,
        parkRequested: false,
        flatPanelLightOff: false,
      );

      await tester.tap(find.text('Emergency Stop'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('EMERGENCY STOP'));
      await tester.pumpAndSettle();

      expect(find.textContaining('MOUNT NOT REACHED'), findsOneWidget,
          reason: 'a failed park rung must be loud — the user must go look');
    });
  });
}
