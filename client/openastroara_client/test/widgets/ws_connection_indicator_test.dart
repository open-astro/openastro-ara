import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/ws_event_stream.dart';
import 'package:openastroara/widgets/status_indicator.dart';
import 'package:openastroara/widgets/ws_connection_indicator.dart';

void main() {
  group('describeWsConnection', () {
    test('maps each link state to a status level + label', () {
      expect(describeWsConnection(WsConnectionState.connecting), (StatusLevel.info, 'Server connecting…'));
      expect(describeWsConnection(WsConnectionState.connected), (StatusLevel.connected, 'Server connected'));
      expect(describeWsConnection(WsConnectionState.reconnecting), (StatusLevel.busy, 'Server reconnecting…'));
      expect(describeWsConnection(WsConnectionState.disconnected), (StatusLevel.disconnected, 'Server disconnected'));
    });
  });

  testWidgets('renders Server disconnected by default (no server / provider loading)', (tester) async {
    await tester.pumpWidget(const ProviderScope(
      child: MaterialApp(home: Scaffold(body: WsConnectionIndicator())),
    ));
    expect(find.text('Server disconnected'), findsOneWidget);
  });
}
