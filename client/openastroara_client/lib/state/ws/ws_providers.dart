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
  StreamSubscription<WsConnectionState>? sub;
  // Every callback guards on isClosed: on provider dispose we close the
  // controller and cancel the source, but the cancel is async, so a transition
  // the disposing WsEventStream emits (e.g. its final `disconnected`) could
  // otherwise hit a closed controller and throw StateError.
  controller.onListen = () {
    sub = stream.connectionStates.listen(
      (s) { if (!controller.isClosed) controller.add(s); },
      onError: (Object e, StackTrace st) { if (!controller.isClosed) controller.addError(e, st); },
      onDone: () { if (!controller.isClosed) controller.close(); },
    );
    if (!controller.isClosed) controller.add(stream.connectionState); // current value, after subscribing
    controller.onCancel = () => sub?.cancel();
  };
  ref.onDispose(() {
    sub?.cancel(); // cancel eagerly so no late event races the close()
    if (!controller.isClosed) controller.close();
  });
  return controller.stream;
});

/// Broadcast of every event from the active server's stream. Feature providers
/// filter by [WsEvent.type]. An empty stream when no server is saved.
final wsEventsProvider = StreamProvider.autoDispose<WsEvent>((ref) {
  final stream = ref.watch(wsEventStreamProvider);
  return stream?.events ?? const Stream<WsEvent>.empty();
});
