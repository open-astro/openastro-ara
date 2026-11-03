import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/server.dart';
import '../services/last_rig_service.dart';
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
    NotifierProvider<SelectedServerNotifier, AraServer?>(
      SelectedServerNotifier.new,
    );

/// Result of the /api/v1/server/info handshake against [selectedServerProvider].
/// Null until a server is selected; AsyncValue otherwise (loading / data / error).
final serverHandshakeProvider = FutureProvider.autoDispose<ServerInfo?>((
  ref,
) async {
  final server = ref.watch(selectedServerProvider);
  if (server == null) return null;
  return ServerApi(server).getInfo();
});

/// ALP-4890 — where the last rig's name is kept; overridden in tests.
final lastRigServiceProvider = Provider<LastRigService>(
  (ref) => LastRigService(),
);

/// The rig this client last connected to, or null.
final lastRigProvider = FutureProvider<LastRig?>(
  (ref) => ref.read(lastRigServiceProvider).load(),
);

/// ALP-4890 — the rig to connect to without a tap: the first one found that
/// is the last rig. A rig that says its uuid is matched on it (#1485: two
/// rigs sharing a hostname, or one with no nickname); one that can't is
/// matched by name, which mDNS advertises with its dots as dashes and the
/// subnet sweep reports as is, so both spellings match.
// shortcut: the raw-multicast mDNS path (macOS, Android) reads no TXT record,
// so its rigs carry no uuid and fall back to the name; read TXT there if two
// same-named rigs ever share a site with those clients.
AraServer? autoPickRig(Iterable<AraServer> found, LastRig? last) {
  if (last == null) return null;
  String key(String n) => n.replaceAll('.', '-').toLowerCase();
  for (final s in found) {
    if (last.uuid != null && s.uuid != null) {
      if (s.uuid == last.uuid) return s;
    } else if (last.name.isNotEmpty &&
        s.mdnsName != null &&
        key(s.mdnsName!) == key(last.name)) {
      return s;
    }
  }
  return null;
}
