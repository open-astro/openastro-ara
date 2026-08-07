import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/server.dart';
import '../state/saved_server_state.dart';
import '../state/ws/ws_providers.dart';

/// §29 — free/total bytes of the volume behind the daemon's save directory.
/// Nulls when the volume is unreachable (e.g. an unmounted USB store), which
/// the panel renders as "unavailable" rather than inventing a number.
class StorageSpace {
  const StorageSpace({
    required this.saveDirectory,
    required this.isFallback,
    this.freeBytes,
    this.totalBytes,
  });

  final String saveDirectory;
  final bool isFallback;
  final int? freeBytes;
  final int? totalBytes;

  factory StorageSpace.fromJson(Map<String, dynamic> json) => StorageSpace(
        saveDirectory: json['save_directory'] as String? ?? '',
        isFallback: json['is_fallback'] as bool? ?? false,
        freeBytes: (json['free_bytes'] as num?)?.toInt(),
        totalBytes: (json['total_bytes'] as num?)?.toInt(),
      );
}

class StorageSpaceApi {
  StorageSpaceApi(AraServer server, {Dio? dio})
      : _dio = dio ??
            Dio(BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 10),
            ));

  final Dio _dio;

  Future<StorageSpace> fetch() async {
    final res = await _dio.get<Map<String, dynamic>>('/api/v1/storage/space');
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw const FormatException('storage/space returned a non-object body');
    }
    return StorageSpace.fromJson(data);
  }

  void close() => _dio.close(force: true);
}

/// Re-fetched whenever the panel asks (and after a save-directory change), so a
/// freshly mounted or freshly filled volume reports honestly.
final storageSpaceProvider = FutureProvider.autoDispose<StorageSpace?>((ref) async {
  final server = ref.watch(activeServerProvider);
  if (server == null) {
    return null;
  }
  // Same nudge as storageDevicesProvider: an unplugged/replugged store
  // changes what "free space" even means — re-read on the daemon's word.
  ref.listen(wsEventsProvider, (prev, next) {
    if (next.asData?.value.type == 'storage.devices_changed') {
      ref.invalidateSelf();
    }
  });
  final api = StorageSpaceApi(server);
  ref.onDispose(api.close);
  return api.fetch();
});
