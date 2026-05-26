import 'package:dio/dio.dart';

import '../models/server.dart';

/// Thin wrapper around `dio` for the handshake portion of the first-run flow.
/// The full typed API client lands in a Phase 11 follow-up (openapi-generator
/// fed by `OpenAstroAra.Server/openapi.yaml`); for now we only need the
/// /healthz + /api/v1/server/info endpoints to confirm a server is reachable
/// and on a compatible API version (playbook §30.2 + §33).
class ServerApi {
  final Dio _dio;

  ServerApi(AraServer server)
      : _dio = Dio(BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          receiveTimeout: const Duration(seconds: 5),
        ));

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
