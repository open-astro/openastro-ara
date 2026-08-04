import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/server.dart';
import '../state/saved_server_state.dart';

/// §29.1.1 — a block device on the server that could hold ARA data.
class StorageDevice {
  const StorageDevice({
    required this.path,
    required this.removable,
    required this.isSystemDisk,
    required this.isAraStore,
    this.uuid,
    this.label,
    this.model,
    this.fileSystem,
    this.sizeBytes,
    this.mountPoint,
    this.transport,
  });

  final String path;
  final String? uuid;
  final String? label;
  final String? model;
  final String? fileSystem;
  final int? sizeBytes;
  final String? mountPoint;
  final bool removable;
  final String? transport;
  final bool isSystemDisk;
  final bool isAraStore;

  /// Only ext4 mounts cleanly; anything else needs the §29.1.3 reformat path.
  bool get isExt4 => fileSystem == 'ext4';

  /// A drive with no filesystem at all can be formatted without a label echo.
  bool get isBlank => (fileSystem ?? '').isEmpty;

  bool get selectable => !isSystemDisk && (uuid != null || isBlank);

  String get displayName {
    final parts = <String>[
      if ((label ?? '').isNotEmpty) label!,
      if ((model ?? '').isNotEmpty) model!,
    ];
    return parts.isEmpty ? path : '${parts.join(' · ')}  ($path)';
  }

  String get sizeText {
    final bytes = sizeBytes;
    if (bytes == null) {
      return '';
    }
    final gb = bytes / (1000 * 1000 * 1000);
    return gb >= 1000
        ? '${(gb / 1000).toStringAsFixed(1)} TB'
        : '${gb.toStringAsFixed(gb >= 10 ? 0 : 1)} GB';
  }

  factory StorageDevice.fromJson(Map<String, dynamic> json) => StorageDevice(
        path: json['path'] as String? ?? '',
        uuid: json['uuid'] as String?,
        label: json['label'] as String?,
        model: json['model'] as String?,
        fileSystem: json['file_system'] as String?,
        sizeBytes: (json['size_bytes'] as num?)?.toInt(),
        mountPoint: json['mount_point'] as String?,
        removable: json['removable'] as bool? ?? false,
        transport: json['transport'] as String?,
        isSystemDisk: json['is_system_disk'] as bool? ?? false,
        isAraStore: json['is_ara_store'] as bool? ?? false,
      );
}

/// Outcome of a configure attempt. [code] is the helper's machine-readable
/// reason (`not_ext4`, `label_mismatch`, `device_busy`, `system_disk`, …) so
/// the UI can offer the right next step.
class StorageConfigureOutcome {
  const StorageConfigureOutcome({
    required this.success,
    required this.code,
    this.detail,
    this.saveDirectory,
  });

  final bool success;
  final String code;
  final String? detail;
  final String? saveDirectory;
}

class StorageDevicesApi {
  StorageDevicesApi(AraServer server, {Dio? dio})
      : _dio = dio ??
            Dio(BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              // mkfs on a large drive is not instant.
              receiveTimeout: const Duration(minutes: 5),
            ));

  final Dio _dio;

  Future<List<StorageDevice>> list() async {
    final res = await _dio.get<List<dynamic>>('/api/v1/storage/devices');
    return (res.data ?? const [])
        .whereType<Map<String, dynamic>>()
        .map(StorageDevice.fromJson)
        .where((d) => d.path.isNotEmpty)
        .toList();
  }

  /// Mount [uuid] as the ARA store. With [format] true the drive is
  /// **erased** and re-made as ext4; [confirmLabel] must equal its current
  /// label (the server refuses otherwise).
  Future<StorageConfigureOutcome> configure({
    required String uuid,
    bool format = false,
    String? confirmLabel,
  }) async {
    try {
      final res = await _dio.post<Map<String, dynamic>>(
        '/api/v1/storage/configure',
        data: {
          'uuid': uuid,
          'format': format,
          'confirm_label': ?confirmLabel,
        },
      );
      final data = res.data ?? const <String, dynamic>{};
      return StorageConfigureOutcome(
        success: data['success'] as bool? ?? true,
        code: data['code'] as String? ?? 'ok',
        detail: data['detail'] as String?,
        saveDirectory: data['save_directory'] as String?,
      );
    } on DioException catch (e) {
      // 422 carries the helper's code in `title`, its detail in `detail`.
      final body = e.response?.data;
      if (body is Map) {
        return StorageConfigureOutcome(
          success: false,
          code: body['title'] as String? ?? 'request_failed',
          detail: body['detail'] as String?,
        );
      }
      return StorageConfigureOutcome(
        success: false,
        code: 'request_failed',
        detail: e.message,
      );
    }
  }

  void close() => _dio.close(force: true);
}

final storageDevicesProvider =
    FutureProvider.autoDispose<List<StorageDevice>>((ref) async {
  final server = ref.watch(activeServerProvider);
  if (server == null) {
    return const [];
  }
  final api = StorageDevicesApi(server);
  ref.onDispose(api.close);
  return api.list();
});
