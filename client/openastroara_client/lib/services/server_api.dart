import 'package:dio/dio.dart';

import '../models/server.dart';

/// Thin wrapper around `dio` for the handshake portion of the first-run flow.
/// The full typed API client lands in a Phase 11 follow-up (openapi-generator
/// fed by `OpenAstroAra.Server/openapi.yaml`); for now we only need the
/// /healthz + /api/v1/server/info endpoints to confirm a server is reachable
/// and on a compatible API version (playbook §30.2 + §33).
class ServerApi {
  final Dio _dio;

  ServerApi(AraServer server, {Dio? dio})
    : _dio =
          dio ??
          Dio(
            BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 5),
            ),
          );

  /// Returns the server's reported version + name from /api/v1/server/info.
  /// Throws DioException on connect/HTTP failure — caller handles UI surfacing.
  Future<ServerInfo> getInfo() async {
    final res = await _dio.get<Map<String, dynamic>>('/api/v1/server/info');
    final data = res.data ?? <String, dynamic>{};
    return ServerInfo(
      name: data['name'] as String? ?? 'OpenAstro Ara',
      version: data['version'] as String? ?? 'unknown',
      apiVersion: data['api_version'] as String? ?? 'unknown',
    );
  }

  /// §27 — claim the single-client control slot (`POST /api/v1/server/connect`).
  /// [sessionId] is this client's previous session id, if any: a matching id is
  /// an idempotent re-claim after a WS blip (same session back, no takeover
  /// modal at the holder). A 409 (slot held / holder unresponsive / dance in
  /// progress) maps to a denied [SessionClaim] with the server's human-readable
  /// detail; anything else (older daemon without §27 → 404, network fault)
  /// throws for the caller to degrade on.
  ///
  /// The receive timeout is raised well above this API's default: on a live
  /// holder the server legitimately blocks up to ~30 s waiting for the holder's
  /// modal answer.
  Future<SessionClaim> connectClient({
    required String hostname,
    String? sessionId,
  }) async {
    try {
      final res = await _dio.post<Map<String, dynamic>>(
        '/api/v1/server/connect',
        data: {'hostname': hostname, 'session_id': ?sessionId},
        options: Options(receiveTimeout: const Duration(seconds: 40)),
      );
      final data = res.data ?? <String, dynamic>{};
      final grantedId = data['session_id'] as String?;
      if (grantedId == null || grantedId.trim().isEmpty) {
        // A 200 without a session id is server misbehavior; treating it as
        // granted would bind the WS with an empty X-Ara-Session and later
        // "release" an empty id. Degrade to denied instead.
        return const SessionClaim.denied(
          'The server accepted the connection but returned no session id.',
        );
      }
      return SessionClaim.granted(grantedId);
    } on DioException catch (e) {
      final res = e.response;
      if (res?.statusCode == 409) {
        final body = res?.data;
        final detail = body is Map<String, dynamic>
            ? body['detail'] as String?
            : null;
        return SessionClaim.denied(
          detail ?? 'The server is in use by another client.',
        );
      }
      rethrow;
    }
  }

  /// §27 — gracefully release the control slot. Best-effort: true when the
  /// server accepted the release, false when the session was already gone
  /// (taken over / released) — both mean "this client no longer holds it".
  Future<bool> disconnectClient(String sessionId) async {
    try {
      await _dio.post<void>(
        '/api/v1/server/disconnect',
        data: {'session_id': sessionId},
      );
      return true;
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) return false;
      rethrow;
    }
  }
}

/// Outcome of a §27 [ServerApi.connectClient] claim.
class SessionClaim {
  /// Session id when granted; null when denied.
  final String? sessionId;

  /// Server's human-readable denial reason; null when granted.
  final String? deniedReason;

  const SessionClaim.granted(this.sessionId) : deniedReason = null;
  const SessionClaim.denied(this.deniedReason) : sessionId = null;

  bool get granted => sessionId != null;
}

class ServerInfo {
  final String name;
  final String version;
  final String apiVersion;

  const ServerInfo({
    required this.name,
    required this.version,
    required this.apiVersion,
  });
}
