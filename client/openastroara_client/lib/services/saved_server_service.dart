import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import '../models/server.dart';

/// The servers confirmed in THIS session. Nothing is written to the device
/// (#1129): a rig's address and even its name can change between nights —
/// backyard, remote site, a new DHCP lease — so every launch starts from a
/// fresh network scan rather than a remembered address that strands the app
/// on "your rig didn't answer". The list lives as long as the app does.
///
/// Older versions persisted the list (with addresses) under [legacyStorageKey];
/// the first [loadAll] of a session deletes that entry, so no address stays on
/// the device after an update.
class SavedServerService {
  static const legacyStorageKey = 'ara.saved_servers.v1';
  final FlutterSecureStorage _storage;
  final List<AraServer> _session = [];
  bool _legacyCleared = false;

  SavedServerService([FlutterSecureStorage? storage])
      : _storage = storage ?? const FlutterSecureStorage();

  Future<List<AraServer>> loadAll() async {
    if (!_legacyCleared) {
      _legacyCleared = true;
      try {
        await _storage.delete(key: legacyStorageKey);
        // ignore: avoid_catches_without_on_clauses
      } catch (_) {
        // Keychain/keyring unavailable: nothing can have been stored there
        // either. Never block launch on the cleanup.
      }
    }
    return List.unmodifiable(_session);
  }

  Future<void> saveAll(List<AraServer> servers) async {
    _session
      ..clear()
      ..addAll(servers);
  }

  Future<void> add(AraServer server) async {
    final existing = await loadAll();
    // Re-confirming a known server (equality is host:port) moves it to the
    // END — the last one confirmed is the active one. Metadata merges
    // per-field: a bare manual re-entry (host:port only) must not blank what
    // a richer earlier confirmation recorded.
    await saveAll(
        [...existing.where((s) => s != server), merge(server, existing)]);
  }

  /// The entry to keep for a (re-)confirmed [server]: its own metadata,
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
}
