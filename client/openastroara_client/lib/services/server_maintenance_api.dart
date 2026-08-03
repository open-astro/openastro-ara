import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/server.dart';
import '../state/saved_server_state.dart';

/// Daemon build + platform identity from `GET /api/v1/server/versions` — what
/// the operator needs when reporting a problem or checking an update landed.
class DaemonVersions {
  const DaemonVersions({
    required this.daemonVersion,
    required this.daemonGitSha,
    required this.dotnetVersion,
    required this.osRelease,
    required this.osArch,
  });

  final String daemonVersion;
  final String daemonGitSha;
  final String dotnetVersion;
  final String osRelease;
  final String osArch;

  factory DaemonVersions.fromJson(Map<String, dynamic> json) => DaemonVersions(
        daemonVersion: json['daemon_version'] as String? ?? 'unknown',
        daemonGitSha: json['daemon_git_sha'] as String? ?? '',
        dotnetVersion: json['dotnet_version'] as String? ?? '',
        osRelease: json['os_release'] as String? ?? '',
        osArch: json['os_arch'] as String? ?? '',
      );
}

/// §33/§34 — daemon identity and the two restart verbs. Restarting is the
/// in-app answer to "the daemon is wedged" that otherwise costs an SSH session.
class ServerMaintenanceApi {
  ServerMaintenanceApi(AraServer server, {Dio? dio})
      : _dio = dio ??
            Dio(BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 10),
            ));

  final Dio _dio;

  Future<DaemonVersions> versions() async {
    final res = await _dio.get<Map<String, dynamic>>('/api/v1/server/versions');
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw const FormatException('server/versions returned a non-object body');
    }
    return DaemonVersions.fromJson(data);
  }

  /// Restart now — in-flight work is lost; the caller confirms first.
  Future<void> restart({String reason = 'operator_requested'}) =>
      _dio.post<Map<String, dynamic>>('/api/v1/server/restart',
          queryParameters: {'reason': reason});

  /// Restart when nothing is running — safe to fire mid-evening.
  Future<void> restartOnIdle({String reason = 'operator_requested'}) =>
      _dio.post<Map<String, dynamic>>('/api/v1/server/restart-on-idle',
          queryParameters: {'reason': reason});

  void close() => _dio.close(force: true);
}

final daemonVersionsProvider =
    FutureProvider.autoDispose<DaemonVersions?>((ref) async {
  final server = ref.watch(activeServerProvider);
  if (server == null) {
    return null;
  }
  final api = ServerMaintenanceApi(server);
  ref.onDispose(api.close);
  return api.versions();
});
