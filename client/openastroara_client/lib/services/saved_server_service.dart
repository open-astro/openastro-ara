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
    final raw = await _storage.read(key: _storageKey);
    if (raw == null || raw.isEmpty) return const <AraServer>[];
    try {
      final List<dynamic> parsed = jsonDecode(raw) as List<dynamic>;
      return parsed
          .whereType<Map<String, dynamic>>()
          .map(_serverFromJson)
          .whereType<AraServer>()
          .toList(growable: false);
    } catch (_) {
      // Corrupted store — start fresh rather than crash the app on launch.
      // The user re-adds the server via the first-run flow.
      return const <AraServer>[];
    }
  }

  Future<void> saveAll(List<AraServer> servers) async {
    final encoded = jsonEncode(servers.map(_serverToJson).toList());
    await _storage.write(key: _storageKey, value: encoded);
  }

  Future<void> add(AraServer server) async {
    final existing = await loadAll();
    if (existing.contains(server)) return;
    await saveAll([...existing, server]);
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
