import 'dart:async';

import '../models/server.dart';
import 'server_api.dart';
import 'server_discovery_service.dart';

/// Finds a saved rig again after it stopped answering at its saved address
/// (#1129): DHCP gave it a new lease, it moved from Ethernet to Wi-Fi, or the
/// address was saved from an interface that has since gone away. The rig's
/// identity is its `server_uuid`, so the search is the whole local network
/// (mDNS plus the full subnet sweep) and the match is by uuid, never by name:
/// two rigs can share the default `openastro` name.
class ServerRelocator {
  ServerRelocator({
    required Stream<AraServer> Function() discoverEverything,
    Future<String?> Function(AraServer candidate)? uuidOf,
    this.timeout = const Duration(seconds: 25),
  })  : _discover = discoverEverything,
        _uuidOf = uuidOf ?? _askServer;

  /// Production wiring: every rig the discovery service can see.
  factory ServerRelocator.forService(ServerDiscoveryService discovery) =>
      ServerRelocator(
        discoverEverything: () => discovery.discover(sweepEverything: true),
      );

  final Stream<AraServer> Function() _discover;
  final Future<String?> Function(AraServer) _uuidOf;

  /// Upper bound on the whole search; a sweep of a /24 normally finishes well
  /// inside it.
  final Duration timeout;

  static Future<String?> _askServer(AraServer candidate) async {
    try {
      return (await ServerApi(candidate).getInfo()).serverUuid;
      // ignore: avoid_catches_without_on_clauses
    } catch (_) {
      return null; // not reachable or not an Ara daemon
    }
  }

  /// The rig [saved] now answers from, or null when it is nowhere on the
  /// network (powered off, or a different network). The result carries
  /// [saved]'s metadata where the found entry has none.
  ///
  /// With a recorded uuid the first rig reporting it wins. An entry saved
  /// before uuids were recorded can only be matched by name, so it is matched
  /// only when the search finishes with exactly one rig of that name.
  Future<AraServer?> relocate(AraServer saved) async {
    final wanted = saved.serverUuid;
    final byName = <String, AraServer>{}; // uuid → candidate, name matches only
    final done = Completer<AraServer?>();
    final pending = <Future<void>>[];
    late final StreamSubscription<AraServer> sub;

    void finish(AraServer? found) {
      if (!done.isCompleted) done.complete(found);
    }

    Future<void> check(AraServer candidate) async {
      final uuid = candidate.serverUuid ?? await _uuidOf(candidate);
      if (uuid == null || done.isCompleted) return;
      final found = AraServer(
        hostname: candidate.hostname,
        port: candidate.port,
        mdnsName: candidate.mdnsName ?? saved.mdnsName,
        serverVersion: candidate.serverVersion ?? saved.serverVersion,
        serverUuid: uuid,
      );
      if (wanted != null) {
        if (uuid == wanted) finish(found);
      } else if (saved.mdnsName != null && candidate.mdnsName == saved.mdnsName) {
        byName[uuid] = found;
      }
    }

    sub = _discover().listen(
      (candidate) {
        // The saved address is the one that just failed; skip it.
        if (candidate == saved) return;
        pending.add(check(candidate));
      },
      onError: (Object _) {},
      onDone: () async {
        await Future.wait(pending);
        finish(byName.length == 1 ? byName.values.single : null);
      },
    );
    final timer = Timer(timeout, () async {
      await Future.wait(pending).timeout(const Duration(seconds: 6), onTimeout: () => <void>[]);
      finish(byName.length == 1 ? byName.values.single : null);
    });
    try {
      return await done.future;
    } finally {
      timer.cancel();
      // Not awaited: the answer is already decided, and cancelling discovery
      // tears down the mDNS client and the subnet sweep, which can take a
      // while — the caller must not wait on that.
      unawaited(sub.cancel());
    }
  }
}
