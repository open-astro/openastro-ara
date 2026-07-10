import 'dart:convert';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import '../models/server.dart';

/// Persists the user's confirmed servers to flutter_secure_storage so the
/// app can skip the first-run flow on subsequent launches (playbook §30).
/// "Confirmed" = handshake against /api/v1/server/info returned 200 at least
/// once. Multiple servers are supported per §30.6 (e.g. observatory + travel
/// rig); the last one used is the default on next launch.
class SavedServerService {
  static const _storageKey = 'ara.saved_servers.v1';
  final FlutterSecureStorage _storage;

  SavedServerService([FlutterSecureStorage? storage])
      : _storage = storage ?? const FlutterSecureStorage();

  Future<List<AraServer>> loadAll() async {
    // The outer try/catch covers both classes of failure:
    //   1. The underlying keychain/keyring is unavailable (Linux without
    //      libsecret, macOS keychain locked, etc) — flutter_secure_storage
    //      throws from `read`.
    //   2. The stored JSON is corrupted (mismatched schema, partial write).
    // Both degrade gracefully to "no saved servers" so the app lands in
    // FirstRunScreen instead of stranding the user on the error route.
    try {
      final raw = await _storage.read(key: _storageKey);
      if (raw == null || raw.isEmpty) return const <AraServer>[];
      final List<dynamic> parsed = jsonDecode(raw) as List<dynamic>;
      return parsed
          .whereType<Map<String, dynamic>>()
          .map(_serverFromJson)
          .whereType<AraServer>()
          .toList(growable: false);
    } catch (_) {
      return const <AraServer>[];
    }
  }

  Future<void> saveAll(List<AraServer> servers) async {
    final encoded = jsonEncode(servers.map(_serverToJson).toList());
    await _storage.write(key: _storageKey, value: encoded);
  }

  Future<void> add(AraServer server) async {
    final existing = await loadAll();
    // Re-confirming a known server (equality is host:port) moves it to the
    // END — "the last one used is the default on next launch" was a lie while
    // this early-returned: with two saved rigs, reconnecting to the older one
    // left the other as `activeServerProvider`'s pick. The re-add refreshes
    // stored metadata (serverVersion/mdnsName can change between
    // confirmations) but merges per-field: a bare manual re-entry (host:port
    // only) must not blank metadata a richer earlier confirmation recorded.
    await saveAll(
        [...existing.where((s) => s != server), merge(server, existing)]);
  }

  /// The entry to store for a (re-)confirmed [server]: its own metadata,
  /// falling back per-field to what a prior confirmation of the same
  /// host:port recorded.
  static AraServer merge(AraServer server, List<AraServer> existing) {
    AraServer? prior;
    for (final s in existing) {
      if (s == server) prior = s;
    }
    if (prior == null) return server;
    return AraServer(
      hostname: server.hostname,
      port: server.port,
      mdnsName: server.mdnsName ?? prior.mdnsName,
      serverVersion: server.serverVersion ?? prior.serverVersion,
    );
  }

  Map<String, dynamic> _serverToJson(AraServer s) => <String, dynamic>{
        'hostname': s.hostname,
        'port': s.port,
        if (s.mdnsName != null) 'mdnsName': s.mdnsName,
        if (s.serverVersion != null) 'serverVersion': s.serverVersion,
      };

  AraServer? _serverFromJson(Map<String, dynamic> j) {
    final host = j['hostname'] as String?;
    final port = j['port'] as int?;
    if (host == null || port == null) return null;
    return AraServer(
      hostname: host,
      port: port,
      mdnsName: j['mdnsName'] as String?,
      serverVersion: j['serverVersion'] as String?,
    );
  }
}
