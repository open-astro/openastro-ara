import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../services/ws_event_stream.dart';
import '../state/ws/ws_providers.dart';
import 'status_indicator.dart';

/// Status-bar indicator for the live §60.9 server link, driven by
/// [wsConnectionStateProvider]. Shows Connecting / Connected / Reconnecting /
/// Disconnected as the WebSocket stream connects, drops, and recovers.
class WsConnectionIndicator extends ConsumerWidget {
  const WsConnectionIndicator({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    // Before the stream emits (or with no server saved) the provider is still
    // loading — treat that as disconnected for the indicator.
    final state = ref.watch(wsConnectionStateProvider).asData?.value ?? WsConnectionState.disconnected;
    final (level, label) = describeWsConnection(state);
    return StatusIndicator(level: level, label: label);
  }
}

/// Pure mapping (so the label/colour choice is unit-testable without a widget):
/// WS link state → the status-bar [StatusLevel] + label.
(StatusLevel, String) describeWsConnection(WsConnectionState state) => switch (state) {
      WsConnectionState.connecting => (StatusLevel.info, 'Connecting…'),
      WsConnectionState.connected => (StatusLevel.connected, 'Connected'),
      WsConnectionState.reconnecting => (StatusLevel.busy, 'Reconnecting…'),
      WsConnectionState.disconnected => (StatusLevel.disconnected, 'Disconnected'),
    };
