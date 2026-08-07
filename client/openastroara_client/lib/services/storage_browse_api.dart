import 'package:dio/dio.dart';

import '../models/server.dart';

/// One child directory of a server-side browse (§37.4/§29). `removable` badges
/// /media//mnt-style mounts so the picker can highlight USB drives.
class StorageBrowseEntry {
  final String name;
  final String path;
  final bool removable;

  const StorageBrowseEntry({
    required this.name,
    required this.path,
    this.removable = false,
  });

  static StorageBrowseEntry? fromJson(dynamic json) {
    if (json is! Map<String, dynamic>) return null;
    final name = json['name'];
    final path = json['path'];
    if (name is! String || path is! String) return null;
    return StorageBrowseEntry(
      name: name,
      path: path,
      removable: json['removable'] == true,
    );
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

/// One real mounted filesystem on the server (§29.1). `isRootDevice` means it
/// shares a physical disk with the OS root — the Pi's SD card.
class StorageDrive {
  final String device;
  final String mountPoint;
  final String filesystem;
  final int totalBytes;
  final int freeBytes;
  final bool isRootDevice;
  final bool isSaveTarget;

  const StorageDrive({
    required this.device,
    required this.mountPoint,
    required this.filesystem,
    required this.totalBytes,
    required this.freeBytes,
    required this.isRootDevice,
    required this.isSaveTarget,
  });

  static StorageDrive? fromJson(dynamic json) {
    if (json is! Map<String, dynamic>) return null;
    final device = json['device'];
    final mount = json['mount_point'];
    if (device is! String || mount is! String) return null;
    return StorageDrive(
      device: device,
      mountPoint: mount,
      filesystem: json['filesystem'] is String
          ? json['filesystem'] as String
          : '',
      totalBytes: json['total_bytes'] is int ? json['total_bytes'] as int : 0,
      freeBytes: json['free_bytes'] is int ? json['free_bytes'] as int : 0,
      isRootDevice: json['is_root_device'] == true,
      isSaveTarget: json['is_save_target'] == true,
    );
  }
}

/// §29.1 `GET /api/v1/storage/status` — where the save directory actually
/// lives (device/filesystem/real free space) + every candidate drive.
class StorageStatus {
  final String saveDirectory;
  final bool saveDirectoryExists;
  final String? mountPoint;
  final String? device;
  final String? filesystem;
  final int totalBytes;
  final int freeBytes;
  final bool onRootDevice;
  final List<StorageDrive> drives;

  const StorageStatus({
    required this.saveDirectory,
    required this.saveDirectoryExists,
    required this.mountPoint,
    required this.device,
    required this.filesystem,
    required this.totalBytes,
    required this.freeBytes,
    required this.onRootDevice,
    required this.drives,
  });

  factory StorageStatus.fromJson(Map<String, dynamic> json) => StorageStatus(
    saveDirectory: json['save_directory'] is String
        ? json['save_directory'] as String
        : '',
    saveDirectoryExists: json['save_directory_exists'] == true,
    mountPoint: json['mount_point'] is String
        ? json['mount_point'] as String
        : null,
    device: json['device'] is String ? json['device'] as String : null,
    filesystem: json['filesystem'] is String
        ? json['filesystem'] as String
        : null,
    totalBytes: json['total_bytes'] is int ? json['total_bytes'] as int : 0,
    freeBytes: json['free_bytes'] is int ? json['free_bytes'] as int : 0,
    onRootDevice: json['on_root_device'] == true,
    drives: json['drives'] is List
        ? (json['drives'] as List)
              .map(StorageDrive.fromJson)
              .whereType<StorageDrive>()
              .toList()
        : const [],
  );
}

/// `GET /api/v1/storage/browse` — the save-directory picker's server walk.
class StorageBrowseApi {
  final Dio _dio;

  StorageBrowseApi(AraServer server, {Dio? dio})
    : _dio =
          dio ??
          Dio(
            BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 10),
            ),
          );

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

  /// Create [name] as a child folder of [parentPath]; returns the parent's
  /// refreshed listing (the new folder included). Throws `DioException` on
  /// transport failure or a 400/403/404/409 Problem.
  Future<StorageBrowseLevel> createFolder(
    String parentPath,
    String name,
  ) async {
    final res = await _dio.post<Map<String, dynamic>>(
      '/api/v1/storage/mkdir',
      data: {'path': parentPath, 'name': name},
    );
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw const FormatException('storage/mkdir returned a non-object body');
    }
    return StorageBrowseLevel.fromJson(data);
  }

  /// §29.1 real storage probe for the Storage panel.
  Future<StorageStatus> fetchStatus() async {
    final res = await _dio.get<Map<String, dynamic>>('/api/v1/storage/status');
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw const FormatException('storage/status returned a non-object body');
    }
    return StorageStatus.fromJson(data);
  }

  void close() => _dio.close(force: true);
}
