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
/// no server is saved; otherwise the current state immediately, then every
/// transition (connecting → connected → reconnecting → …).
///
/// `connectionStates` is a non-replaying broadcast stream, so we must subscribe
/// to it BEFORE reading the current value — otherwise a transition firing in the
/// snapshot→subscribe gap (likely, since `connect()` runs synchronously in
/// [wsEventStreamProvider]) would be silently dropped and the consumer could be
/// stuck on a stale state. Subscribing first then emitting the current value
/// (no await between them) closes that gap atomically.
final wsConnectionStateProvider = StreamProvider.autoDispose<WsConnectionState>((ref) {
  final stream = ref.watch(wsEventStreamProvider);
  if (stream == null) return Stream.value(WsConnectionState.disconnected);
  final controller = StreamController<WsConnectionState>();
  controller.onListen = () {
    final sub = stream.connectionStates.listen(
      controller.add,
      onError: controller.addError,
      onDone: controller.close,
    );
    controller.add(stream.connectionState); // current value, captured after the subscription
    controller.onCancel = sub.cancel;
  };
  ref.onDispose(controller.close);
  return controller.stream;
});

/// Broadcast of every event from the active server's stream. Feature providers
/// filter by [WsEvent.type]. An empty stream when no server is saved.
final wsEventsProvider = StreamProvider.autoDispose<WsEvent>((ref) {
  final stream = ref.watch(wsEventStreamProvider);
  return stream?.events ?? const Stream<WsEvent>.empty();
});
