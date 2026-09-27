import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/state/settings/equipment_connection_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/widgets/equipment/equipment_connection_card.dart';

/// Minimal status for the card: the shared header only needs state + name.
class _Status extends EquipmentDeviceStatus {
  _Status(this.connectionState);
  @override
  final EquipmentConnectionState connectionState;
  @override
  String get name => 'Nightcrawler';
}

class _Calls {
  final List<String> log = [];
  Future<bool> connect(DiscoveredDevice d) async {
    log.add('connect:${d.name}');
    return true;
  }

  Future<bool> disconnect() async {
    log.add('disconnect');
    return true;
  }

  Future<bool> reconnect() async {
    log.add('reconnect');
    return true;
  }

  Future<bool> forget() async {
    log.add('forget');
    return true;
  }
}

Future<_Calls> _pump(
  WidgetTester tester,
  _Status? status, {
  bool withForget = true,
  bool withReconnect = true,
}) async {
  final calls = _Calls();
  await tester.pumpWidget(
    ProviderScope(
      overrides: [serverLinkUpProvider.overrideWith((ref) => true)],
      child: MaterialApp(
        home: Scaffold(
          body: EquipmentConnectionCard<_Status>(
            status: AsyncData<_Status?>(status),
            deviceType: EquipmentDeviceType.rotator,
            deviceTypeLabel: 'rotator',
            emptyLabel: 'No rotator connected.',
            onConnect: calls.connect,
            onDisconnect: calls.disconnect,
            onReconnect: withReconnect ? calls.reconnect : null,
            onForget: withForget ? calls.forget : null,
            onRetry: () {},
            connectedBody: (context, s) => const Text('BODY'),
          ),
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
  return calls;
}

void main() {
  testWidgets('nothing known: empty row with Reconnect + Connect…, no Remove', (
    tester,
  ) async {
    await _pump(tester, null);
    expect(find.text('No rotator connected.'), findsOneWidget);
    expect(find.widgetWithText(TextButton, 'Reconnect'), findsOneWidget);
    expect(find.widgetWithText(TextButton, 'Connect…'), findsOneWidget);
    expect(find.byTooltip('Remove this rotator'), findsNothing);
    expect(find.byTooltip('Connect'), findsNothing);
  });

  testWidgets('connected: Disconnect only, body shown', (tester) async {
    final calls = await _pump(
      tester,
      _Status(EquipmentConnectionState.connected),
    );
    expect(find.text('Nightcrawler'), findsOneWidget);
    expect(find.text('BODY'), findsOneWidget);
    expect(find.byTooltip('Disconnect'), findsOneWidget);
    expect(find.byTooltip('Connect'), findsNothing);
    expect(find.byTooltip('Remove this rotator'), findsNothing);
    expect(find.widgetWithText(TextButton, 'Connect…'), findsNothing);
    await tester.tap(find.byTooltip('Disconnect'));
    await tester.pumpAndSettle();
    expect(calls.log, ['disconnect']);
  });

  testWidgets('connecting: Cancel connecting, no Connect/Remove', (
    tester,
  ) async {
    await _pump(tester, _Status(EquipmentConnectionState.connecting));
    expect(find.byTooltip('Cancel connecting'), findsOneWidget);
    expect(find.byIcon(Icons.close), findsOneWidget);
    expect(find.byTooltip('Connect'), findsNothing);
    expect(find.byTooltip('Remove this rotator'), findsNothing);
  });

  testWidgets('disconnected (known): name, Connect + Remove, chooser kept', (
    tester,
  ) async {
    final calls = await _pump(
      tester,
      _Status(EquipmentConnectionState.disconnected),
    );
    expect(find.text('Nightcrawler'), findsOneWidget);
    expect(find.text('Disconnected'), findsOneWidget);
    expect(find.text('Not connected.'), findsOneWidget);
    expect(find.text('No rotator connected.'), findsNothing);
    expect(find.text('BODY'), findsNothing);
    expect(find.byTooltip('Disconnect'), findsNothing);
    expect(find.widgetWithText(TextButton, 'Reconnect'), findsNothing);
    expect(find.byTooltip('Connect'), findsOneWidget);
    expect(find.byTooltip('Remove this rotator'), findsOneWidget);
    expect(find.widgetWithText(TextButton, 'Connect…'), findsOneWidget);
    await tester.tap(find.byTooltip('Connect'));
    await tester.pumpAndSettle();
    expect(calls.log, ['reconnect']);
  });

  testWidgets('error (known): Connect + Remove, body still shown', (
    tester,
  ) async {
    await _pump(tester, _Status(EquipmentConnectionState.error));
    expect(find.text('BODY'), findsOneWidget);
    expect(find.byTooltip('Disconnect'), findsNothing);
    expect(find.byTooltip('Connect'), findsOneWidget);
    expect(find.byTooltip('Remove this rotator'), findsOneWidget);
    expect(find.widgetWithText(TextButton, 'Connect…'), findsOneWidget);
  });

  testWidgets('Remove asks first; Cancel forgets nothing', (tester) async {
    final calls = await _pump(
      tester,
      _Status(EquipmentConnectionState.disconnected),
    );
    await tester.tap(find.byTooltip('Remove this rotator'));
    await tester.pumpAndSettle();
    expect(find.text('Remove Nightcrawler?'), findsOneWidget);
    await tester.tap(find.widgetWithText(TextButton, 'Cancel'));
    await tester.pumpAndSettle();
    expect(calls.log, isEmpty);
  });

  testWidgets('Remove confirmed calls onForget', (tester) async {
    final calls = await _pump(
      tester,
      _Status(EquipmentConnectionState.disconnected),
    );
    await tester.tap(find.byTooltip('Remove this rotator'));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Remove'));
    await tester.pumpAndSettle();
    expect(calls.log, ['forget']);
  });

  testWidgets(
    'no onForget → no Remove icon; no onReconnect → no Connect icon',
    (tester) async {
      await _pump(
        tester,
        _Status(EquipmentConnectionState.disconnected),
        withForget: false,
        withReconnect: false,
      );
      expect(find.byTooltip('Remove this rotator'), findsNothing);
      expect(find.byTooltip('Connect'), findsNothing);
      expect(find.widgetWithText(TextButton, 'Connect…'), findsOneWidget);
    },
  );
}
