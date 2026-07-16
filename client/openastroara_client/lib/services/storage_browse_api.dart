import 'package:dio/dio.dart';

import '../models/server.dart';

/// One child directory of a server-side browse (§37.4/§29). `removable` badges
/// /media//mnt-style mounts so the picker can highlight USB drives.
class StorageBrowseEntry {
  final String name;
  final String path;
  final bool removable;

  const StorageBrowseEntry(
      {required this.name, required this.path, this.removable = false});

  static StorageBrowseEntry? fromJson(dynamic json) {
    if (json is! Map<String, dynamic>) return null;
    final name = json['name'];
    final path = json['path'];
    if (name is! String || path is! String) return null;
    return StorageBrowseEntry(
        name: name, path: path, removable: json['removable'] == true);
  }
}

/// One directory level of the SERVER's filesystem. `path` is empty for the
/// curated-roots listing (home, /media, /mnt, /).
class StorageBrowseLevel {
  final String path;
  final String? parent;
  final bool writable;
  final List<StorageBrowseEntry> dirs;

  const StorageBrowseLevel({
    required this.path,
    required this.parent,
    required this.writable,
    required this.dirs,
  });

  bool get isRoots => path.isEmpty;

  factory StorageBrowseLevel.fromJson(Map<String, dynamic> json) =>
      StorageBrowseLevel(
        path: json['path'] is String ? json['path'] as String : '',
        parent: json['parent'] is String ? json['parent'] as String : null,
        writable: json['writable'] == true,
        dirs: json['dirs'] is List
            ? (json['dirs'] as List)
                .map(StorageBrowseEntry.fromJson)
                .whereType<StorageBrowseEntry>()
                .toList()
            : const [],
      );
}

/// `GET /api/v1/storage/browse` — the save-directory picker's server walk.
class StorageBrowseApi {
  final Dio _dio;

  StorageBrowseApi(AraServer server, {Dio? dio})
      : _dio = dio ??
            Dio(BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 10),
            ));

  /// One level at [path]; null → the curated roots. Throws `DioException` on
  /// transport failure or a 403/404 Problem (caller surfaces the message).
  Future<StorageBrowseLevel> browse([String? path]) async {
    final res = await _dio.get<Map<String, dynamic>>(
      '/api/v1/storage/browse',
      queryParameters: path == null ? null : {'path': path},
    );
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw const FormatException('storage/browse returned a non-object body');
    }
    return StorageBrowseLevel.fromJson(data);
  }

  void close() => _dio.close(force: true);
}
