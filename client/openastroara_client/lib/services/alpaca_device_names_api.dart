import 'package:dio/dio.dart';

import '../models/server.dart';

/// §63.20 / #1067 — the REAL device names behind an Alpaca server's generic
/// choice strings ("Alpaca Camera [host:port/N]" → "ZWO ASI290MM Mini"), read
/// off that server's management API BY THE DAEMON
/// (`GET /api/v1/equipment/guider/alpacadevicenames?host=&port=`). The client
/// used to call the Alpaca host itself — its one bypass of the daemon, and a
/// lookup that failed whenever this machine couldn't route to the rig's LAN.
/// Returns a map keyed `"<devicetype>/<devicenumber>"` (type lowercased, e.g.
/// `"camera/1"`) → device name. Best-effort: any failure returns an empty map —
/// callers fall back to the generic labels.
abstract interface class AlpacaDeviceNamesClient {
  Future<Map<String, String>> fetchNames(String host, int port);
  void close();
}

class AlpacaDeviceNamesApi implements AlpacaDeviceNamesClient {
  final Dio _dio;

  /// [dio] is the test seam (a stubbed transport); production builds one
  /// against the active daemon.
  AlpacaDeviceNamesApi(AraServer server, {Dio? dio})
      : _dio = dio ??
            Dio(BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              // The daemon's own lookup is capped at 3 s per host; leave
              // headroom.
              receiveTimeout: const Duration(seconds: 8),
            ));

  /// The daemon route this client dials — pinned by test so a typo can't
  /// degrade silently into "labels stay generic forever".
  static const String route = '/api/v1/equipment/guider/alpacadevicenames';

  @override
  Future<Map<String, String>> fetchNames(String host, int port) async {
    try {
      final res = await _dio.get<dynamic>(
        route,
        queryParameters: <String, dynamic>{'host': host, 'port': port},
      );
      return parseNames(res.data);
    } catch (_) {
      return const {};
    }
  }

  /// Parse the daemon's `{names: {"camera/1": "…"}}` envelope. Pure —
  /// unit-tested. Anything malformed yields an empty map.
  static Map<String, String> parseNames(Object? data) {
    if (data is! Map<String, dynamic>) return const {};
    final names = data['names'];
    if (names is! Map) return const {};
    return {
      for (final entry in names.entries)
        if (entry.key is String &&
            entry.value is String &&
            (entry.value as String).isNotEmpty)
          entry.key as String: entry.value as String,
    };
  }

  @override
  void close() => _dio.close(force: true);
}

/// The no-active-server stand-in: every lookup is empty, so the wizard keeps
/// the daemon's generic labels.
class NoServerAlpacaDeviceNames implements AlpacaDeviceNamesClient {
  const NoServerAlpacaDeviceNames();
  @override
  Future<Map<String, String>> fetchNames(String host, int port) async =>
      const {};
  @override
  void close() {}
}
