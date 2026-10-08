import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/filter_wheel_status.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/services/equipment_device_api.dart';
import 'package:openastroara/state/equipment/filter_wheel_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/widgets/manual_filter_swap_listener.dart';

/// The wheel as the listener sees it: one read at launch, the two actions,
/// and refresh calls counted (no live poll is involved).
class _FakeWheel extends FilterWheelNotifier {
  _FakeWheel(this.initial, this.calls, {this.performs = true});
  final FilterWheelStatus? initial;
  final List<String> calls;
  final bool performs;
  @override
  Future<FilterWheelStatus?> build() async => initial;
  @override
  Future<void> refresh([
    EquipmentDeviceClient<FilterWheelStatus>? client,
  ]) async => calls.add('refresh');
  @override
  Future<bool> reportInstalled(int position) async {
    calls.add('installed:$position');
    return performs;
  }

  @override
  Future<bool> cancelManualSwap() async {
    calls.add('cancel');
    return performs;
  }
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

WsEvent _swap({int? pending, String? pendingName, String? current}) => WsEvent(
  type: 'equipment.filter_wheel.manual_swap',
  ts: DateTime.utc(2026, 10, 7),
  seq: 1,
  payload: {
    'device_type': 'filterwheel',
    'pending_slot': pending,
    'pending_name': pendingName,
    'current_slot': 0,
    'current_name': current,
  },
);

Future<(List<String>, StreamController<WsEvent>)> _pump(
  WidgetTester tester,
  FilterWheelStatus? initial, {
  bool performs = true,
}) async {
  final calls = <String>[];
  final ws = StreamController<WsEvent>.broadcast();
  addTearDown(ws.close);
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        filterWheelProvider.overrideWith(
          () => _FakeWheel(initial, calls, performs: performs),
        ),
        wsEventsProvider.overrideWith((ref) => ws.stream),
      ],
      child: const MaterialApp(
        home: ManualFilterSwapListener(child: Scaffold(body: Text('shell'))),
      ),
    ),
  );
  await tester.pumpAndSettle();
  return (calls, ws);
}

void main() {
  testWidgets('a swap pending at launch is shown, and confirming closes it', (
    tester,
  ) async {
    final (calls, _) = await _pump(
      tester,
      _manual(currentSlot: 0, pendingSlot: 1),
    );
    expect(find.text('Install the Ha filter'), findsOneWidget);
    expect(find.textContaining('Take out L'), findsOneWidget);

    await tester.tap(find.widgetWithText(FilledButton, 'Ha is in'));
    await tester.pumpAndSettle();
    expect(calls, contains('installed:1'));
    expect(find.byType(AlertDialog), findsNothing);
  });

  testWidgets(
    'the swap event opens the prompt from its payload; a resolve event closes it',
    (tester) async {
      final (calls, ws) = await _pump(tester, _manual(currentSlot: 0));
      expect(find.byType(AlertDialog), findsNothing);

      ws.add(_swap(pending: 1, pendingName: 'Ha', current: 'L'));
      await tester.pumpAndSettle();
      expect(find.text('Install the Ha filter'), findsOneWidget);
      expect(
        calls,
        contains('refresh'),
        reason: 'open panels re-read on the event',
      );

      // Confirmed on another client or the Equipment card.
      ws.add(_swap(current: 'Ha'));
      await tester.pumpAndSettle();
      expect(find.byType(AlertDialog), findsNothing);
    },
  );

  testWidgets(
    'Later closes without an answer and a repeat event does not reopen it',
    (tester) async {
      final (calls, ws) = await _pump(
        tester,
        _manual(currentSlot: 0, pendingSlot: 1),
      );
      await tester.tap(find.widgetWithText(TextButton, 'Later'));
      await tester.pumpAndSettle();
      expect(find.byType(AlertDialog), findsNothing);
      ws.add(_swap(pending: 1, pendingName: 'Ha', current: 'L'));
      await tester.pumpAndSettle();
      expect(find.byType(AlertDialog), findsNothing);
      expect(calls.where((c) => c != 'refresh'), isEmpty);
    },
  );

  testWidgets('Cancel swap sends the cancel', (tester) async {
    final (calls, _) = await _pump(
      tester,
      _manual(currentSlot: 0, pendingSlot: 1),
    );
    await tester.tap(find.widgetWithText(TextButton, 'Cancel swap'));
    await tester.pumpAndSettle();
    expect(calls, contains('cancel'));
    expect(find.byType(AlertDialog), findsNothing);
  });

  testWidgets(
    'a confirm the notifier dropped reopens the prompt and says why',
    (tester) async {
      final (calls, _) = await _pump(
        tester,
        _manual(currentSlot: 0, pendingSlot: 1),
        performs: false,
      );
      await tester.tap(find.widgetWithText(FilledButton, 'Ha is in'));
      await tester.pumpAndSettle();
      expect(calls, contains('installed:1'));
      expect(
        find.textContaining('another filter wheel action is still running'),
        findsOneWidget,
      );
      expect(
        find.text('Install the Ha filter'),
        findsOneWidget,
        reason: 'nothing reached the daemon, so the swap still stands',
      );
    },
  );

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
