import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../models/ws_event.dart';
import '../../services/ws_event_stream.dart';
import '../saved_server_state.dart';

/// Builds the §60.9 event-stream client for a server. Overridable in tests so a
/// fake socket connector can be injected (the default hits a real WebSocket).
final wsEventStreamFactoryProvider = Provider<WsEventStream Function(AraServer)>(
  (ref) => (server) => WsEventStream(server),
);

/// The §60.9 WS event-stream for the **active** server (`savedServers.last`).
/// Lives as long as it's watched; `ref.onDispose` tears the socket down. Returns
/// null when no server is saved yet. Auto-disposed when no consumer watches it.
final wsEventStreamProvider = Provider.autoDispose<WsEventStream?>((ref) {
  final servers = ref.watch(savedServersProvider).maybeWhen(
        data: (list) => list,
        orElse: () => const <AraServer>[],
      );
  if (servers.isEmpty) return null;
  final stream = ref.watch(wsEventStreamFactoryProvider)(servers.last);
  stream.connect();
  // dispose() is async; onDispose takes a void callback, so the teardown
  // (final `disconnected`, controller closes) runs fire-and-forget — explicit
  // via unawaited. On a server-list change the old stream is torn down here.
  ref.onDispose(() => unawaited(stream.dispose()));
  return stream;
});

/// Live link state of the active server's WS stream. Emits `disconnected` when
/// no server is saved; otherwise seeds with the current state then follows
/// transitions (connecting → connected → reconnecting → …).
final wsConnectionStateProvider = StreamProvider.autoDispose<WsConnectionState>((ref) async* {
  final stream = ref.watch(wsEventStreamProvider);
  if (stream == null) {
    yield WsConnectionState.disconnected;
    return;
  }
  yield stream.connectionState;
  yield* stream.connectionStates;
});

/// Broadcast of every event from the active server's stream. Feature providers
/// filter by [WsEvent.type]. An empty stream when no server is saved.
final wsEventsProvider = StreamProvider.autoDispose<WsEvent>((ref) {
  final stream = ref.watch(wsEventStreamProvider);
  return stream?.events ?? const Stream<WsEvent>.empty();
});
