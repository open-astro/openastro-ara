import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../services/ws_event_stream.dart';
import '../state/ws/ws_providers.dart';
import 'status_indicator.dart';

/// Status-bar indicator for the **client ↔ server link**, driven by
/// [wsConnectionStateProvider]. Labelled explicitly as the *server* connection
/// (not a device/camera) so the user always knows what it refers to.
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
      WsConnectionState.connecting => (StatusLevel.info, 'Server connecting…'),
      WsConnectionState.connected => (StatusLevel.connected, 'Server connected'),
      WsConnectionState.reconnecting => (StatusLevel.busy, 'Server reconnecting…'),
      WsConnectionState.disconnected => (StatusLevel.disconnected, 'Server disconnected'),
    };
