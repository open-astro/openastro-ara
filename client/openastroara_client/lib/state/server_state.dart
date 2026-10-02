import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/server.dart';
import '../services/server_api.dart';
import '../services/server_discovery_service.dart';

/// Single-source Riverpod providers for the first-run flow.
/// State persistence to flutter_secure_storage lands in the Phase 11 follow-up
/// once the storage schema is locked down per playbook §30.

final discoveryServiceProvider = Provider<ServerDiscoveryService>(
  (ref) => ServerDiscoveryService(),
);

/// Live stream of servers discovered by mDNS. The UI listens to this and
/// updates the list as each `_openastroara._tcp.local` SRV record arrives.
final discoveredServersProvider = StreamProvider.autoDispose<AraServer>(
  (ref) => ref.watch(discoveryServiceProvider).discover(),
);

/// The server the user has selected (either from the mDNS list or manual entry).
/// Once a handshake against this server succeeds, `serverHandshakeProvider`
/// fires with version metadata. Riverpod 3.x removed StateProvider; we use a
/// thin Notifier instead so the screen can call `.select(server)` to update.
class SelectedServerNotifier extends Notifier<AraServer?> {
  @override
  AraServer? build() => null;

  void select(AraServer? server) => state = server;
}

final selectedServerProvider =
    NotifierProvider<SelectedServerNotifier, AraServer?>(SelectedServerNotifier.new);

/// Result of the /api/v1/server/info handshake against [selectedServerProvider].
/// Null until a server is selected; AsyncValue otherwise (loading / data / error).
final serverHandshakeProvider = FutureProvider.autoDispose<ServerInfo?>(
  (ref) async {
    final server = ref.watch(selectedServerProvider);
    if (server == null) return null;
    return ServerApi(server).getInfo();
  },
);
