import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/server.dart';
import '../state/saved_server_state.dart';

/// #1121 — what the daemon's plate solver will find: the ASTAP star-database
/// directory (the profile's index path) and the solver binary. A fresh install
/// that skipped the star-database download fails every solve with ASTAP exit
/// 32; Settings -> Plate solving shows this so the app says why.
class PlateSolveDatabaseStatus {
  const PlateSolveDatabaseStatus({
    required this.configuredPath,
    required this.effectivePath,
    required this.fileCount,
    required this.databases,
    required this.solverPath,
    required this.solverFound,
  });

  /// The profile's index path, passed to ASTAP as `-d`.
  final String configuredPath;

  /// [configuredPath] when it holds files; null when missing or empty.
  final String? effectivePath;
  final int fileCount;

  /// Database abbreviations found there, e.g. `d80`.
  final List<String> databases;
  final String solverPath;
  final bool solverFound;

  bool get hasDatabase => effectivePath != null && fileCount > 0;

  factory PlateSolveDatabaseStatus.fromJson(Map<String, dynamic> json) =>
      PlateSolveDatabaseStatus(
        configuredPath: json['configured_path'] as String? ?? '',
        effectivePath: json['effective_path'] as String?,
        fileCount: (json['file_count'] as num?)?.toInt() ?? 0,
        databases: (json['databases'] as List? ?? const [])
            .whereType<String>()
            .toList(growable: false),
        solverPath: json['solver_path'] as String? ?? '',
        solverFound: json['solver_found'] as bool? ?? false,
      );
}

/// Dio wrapper over `GET /api/v1/platesolve/database`.
class PlateSolveDatabaseApi {
  PlateSolveDatabaseApi(AraServer server, {Dio? dio})
    : _dio =
          dio ??
          Dio(
            BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 10),
            ),
          );

  final Dio _dio;

  /// The status, or null when it cannot be known: no connection (offline at a
  /// dark site), a daemon older than the route (404), or an unexpected body.
  /// Never throws — "unknown" is a normal state, not an error.
  Future<PlateSolveDatabaseStatus?> fetch() async {
    try {
      final res = await _dio.get<dynamic>('/api/v1/platesolve/database');
      final data = res.data;
      return data is Map<String, dynamic>
          ? PlateSolveDatabaseStatus.fromJson(data)
          : null;
    } on DioException {
      return null;
    } on FormatException {
      return null;
    }
  }

  void close() => _dio.close(force: true);
}

/// Null = unknown (no active server, offline, or an older daemon). The plate
/// solve panel invalidates it after a Save, since the index path may change.
final plateSolveDatabaseStatusProvider =
    FutureProvider.autoDispose<PlateSolveDatabaseStatus?>((ref) async {
      final server = ref.watch(activeServerProvider);
      if (server == null) return null;
      final api = PlateSolveDatabaseApi(server);
      ref.onDispose(api.close);
      return api.fetch();
    });
