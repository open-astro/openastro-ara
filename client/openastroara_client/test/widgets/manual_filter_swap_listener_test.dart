import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/filter_wheel_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/services/equipment_device_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/equipment/filter_wheel_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/widgets/manual_filter_swap_listener.dart';

class _FakeSavedServerService implements SavedServerService {
  @override
  Future<List<AraServer>> loadAll() async => const [
    AraServer(hostname: 'h', port: 5555),
  ];
  @override
  Future<void> saveAll(List<AraServer> servers) async {}
  @override
  Future<void> add(AraServer server) async {}
}

class _FakeFwApi implements EquipmentDeviceClient<FilterWheelStatus> {
  FilterWheelStatus? status;
  final List<String> calls = [];
  @override
  Future<FilterWheelStatus?> getStatus() async => status;
  @override
  Future<void> connect(DiscoveredDevice device) async {}
  @override
  Future<void> disconnect() async {}
  @override
  Future<void> forget() async {}
  @override
  Future<void> command(String subpath, [Map<String, dynamic>? body]) async {
    calls.add('$subpath:${body?['position']}');
    // The daemon resolves the swap on a matching confirm or a cancel.
    if (subpath == 'swap/cancel' ||
        (subpath == 'installed' && body?['position'] == status?.pendingSlot)) {
      status = _manual(
        currentSlot: body?['position'] as int? ?? status?.currentSlot,
      );
    }
  }

  @override
  void close() {}
}

FilterWheelStatus _manual({int? currentSlot, int? pendingSlot}) =>
    FilterWheelStatus(
      deviceId: 'ara-manual-filter-wheel',
      name: 'Manual filter wheel',
      connectionState: EquipmentConnectionState.connected,
      runtimeState: pendingSlot == null ? 'idle' : 'awaiting_user',
      currentSlot: currentSlot,
      pendingSlot: pendingSlot,
      manual: true,
      slots: const [
        FilterSlot(position: 0, name: 'L', focusOffset: 0),
        FilterSlot(position: 1, name: 'Ha', focusOffset: 0),
      ],
    );

WsEvent _swapEvent() => WsEvent(
  type: 'equipment.filter_wheel.manual_swap',
  ts: DateTime.utc(2026, 10, 7),
  seq: 1,
  payload: const {'pending_slot': 1},
);

Future<(_FakeFwApi, StreamController<WsEvent>)> _pump(
  WidgetTester tester,
  FilterWheelStatus? initial,
) async {
  final api = _FakeFwApi()..status = initial;
  final ws = StreamController<WsEvent>.broadcast();
  addTearDown(ws.close);
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        serverLinkUpProvider.overrideWith((ref) => true),
        savedServerServiceProvider.overrideWithValue(_FakeSavedServerService()),
        filterWheelApiFactoryProvider.overrideWithValue((_) => api),
        wsEventsProvider.overrideWith((ref) => ws.stream),
      ],
      child: const MaterialApp(
        home: ManualFilterSwapListener(child: Scaffold(body: Text('shell'))),
      ),
    ),
  );
  await tester.pumpAndSettle();
  return (api, ws);
}

void main() {
  testWidgets('a swap pending at launch is shown, and confirming closes it', (
    tester,
  ) async {
    final (api, _) = await _pump(
      tester,
      _manual(currentSlot: 0, pendingSlot: 1),
    );
    expect(find.text('Install the Ha filter'), findsOneWidget);
    expect(find.textContaining('Take out L'), findsOneWidget);

    await tester.tap(find.widgetWithText(FilledButton, 'Ha is in'));
    await tester.pumpAndSettle();
    expect(api.calls, contains('installed:1'));
    expect(find.text('Install the Ha filter'), findsNothing);
  });

  testWidgets(
    'the swap event opens the prompt; resolving elsewhere closes it',
    (tester) async {
      final (api, ws) = await _pump(tester, _manual(currentSlot: 0));
      expect(find.byType(AlertDialog), findsNothing);

      api.status = _manual(currentSlot: 0, pendingSlot: 1);
      ws.add(_swapEvent());
      await tester.pumpAndSettle();
      expect(find.text('Install the Ha filter'), findsOneWidget);

      // Another client (or the Equipment card) confirmed it.
      api.status = _manual(currentSlot: 1);
      ws.add(_swapEvent());
      await tester.pumpAndSettle();
      expect(find.byType(AlertDialog), findsNothing);
    },
  );

  testWidgets(
    'Later closes without an answer and does not reopen for the same swap',
    (tester) async {
      final (api, ws) = await _pump(
        tester,
        _manual(currentSlot: 0, pendingSlot: 1),
      );
      await tester.tap(find.widgetWithText(TextButton, 'Later'));
      await tester.pumpAndSettle();
      expect(find.byType(AlertDialog), findsNothing);
      ws.add(_swapEvent()); // a re-read of the same standing swap
      await tester.pumpAndSettle();
      expect(find.byType(AlertDialog), findsNothing);
      expect(api.calls, isEmpty);
    },
  );

  testWidgets('Cancel swap sends the cancel', (tester) async {
    final (api, _) = await _pump(
      tester,
      _manual(currentSlot: 0, pendingSlot: 1),
    );
    await tester.tap(find.widgetWithText(TextButton, 'Cancel swap'));
    await tester.pumpAndSettle();
    expect(api.calls, contains('swap/cancel:null'));
    expect(find.byType(AlertDialog), findsNothing);
  });

  testWidgets('a motorised wheel never prompts', (tester) async {
    await _pump(
      tester,
      FilterWheelStatus(
        deviceId: 'efw',
        name: 'EFW',
        connectionState: EquipmentConnectionState.connected,
        runtimeState: 'moving',
        currentSlot: null,
        slots: const [],
      ),
    );
    expect(find.byType(AlertDialog), findsNothing);
  });
}
