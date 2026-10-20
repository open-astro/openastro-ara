import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/server.dart';
import '../state/saved_server_state.dart';

/// The server's disk-to-disk copy: the store (or folders on it) onto a second
/// drive. Copy only — files already on the target are skipped, a different
/// file under the same name is a conflict and is left alone.
class StorageCopyStatus {
  const StorageCopyStatus({
    required this.state,
    this.targetUuid,
    this.folders = const [],
    this.filesToCopy = 0,
    this.filesCopied = 0,
    this.filesSkipped = 0,
    this.filesConflicting = 0,
    this.filesFailed = 0,
    this.bytesToCopy = 0,
    this.bytesCopied = 0,
    this.currentFile,
    this.errorCode,
    this.errorDetail,
    this.firstFailure,
    this.targetReleased = false,
  });

  static const idle = StorageCopyStatus(state: 'idle');

  /// idle · preparing · copying · done · cancelled · failed
  final String state;
  final String? targetUuid;
  final List<String> folders;
  final int filesToCopy;
  final int filesCopied;
  final int filesSkipped;
  final int filesConflicting;
  final int filesFailed;
  final int bytesToCopy;
  final int bytesCopied;
  final String? currentFile;
  final String? errorCode;
  final String? errorDetail;
  final String? firstFailure;

  /// The target was flushed and unmounted: safe to unplug.
  final bool targetReleased;

  bool get isRunning => state == 'preparing' || state == 'copying';
  bool get isFinished =>
      state == 'done' || state == 'cancelled' || state == 'failed';

  /// 0..1 by bytes; null while the file list is still being built.
  double? get fraction {
    if (state == 'preparing') return null;
    if (bytesToCopy <= 0) return isFinished ? 1 : null;
    return (bytesCopied / bytesToCopy).clamp(0.0, 1.0);
  }

  factory StorageCopyStatus.fromJson(Map<String, dynamic> json) {
    int n(String key) => (json[key] as num?)?.toInt() ?? 0;
    return StorageCopyStatus(
      state: json['state'] as String? ?? 'idle',
      targetUuid: json['target_uuid'] as String?,
      folders: (json['folders'] as List?)?.whereType<String>().toList() ??
          const [],
      filesToCopy: n('files_to_copy'),
      filesCopied: n('files_copied'),
      filesSkipped: n('files_skipped'),
      filesConflicting: n('files_conflicting'),
      filesFailed: n('files_failed'),
      bytesToCopy: n('bytes_to_copy'),
      bytesCopied: n('bytes_copied'),
      currentFile: json['current_file'] as String?,
      errorCode: json['error_code'] as String?,
      errorDetail: json['error_detail'] as String?,
      firstFailure: json['first_failure'] as String?,
      targetReleased: json['target_released'] == true,
    );
  }
}

/// Why the server would not start a copy (`title` of its Problem).
class StorageCopyRefused implements Exception {
  const StorageCopyRefused(this.code, this.detail);
  final String code;
  final String? detail;

  @override
  String toString() => detail ?? code;
}

class StorageCopyApi {
  StorageCopyApi(AraServer server, {Dio? dio})
      : _dio = dio ??
            Dio(BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 10),
            ));

  final Dio _dio;

  /// Start copying onto [targetUuid]; empty [folders] copies the whole store.
  /// Throws [StorageCopyRefused] when the server says no (busy, no store, …).
  Future<StorageCopyStatus> start({
    required String targetUuid,
    List<String> folders = const [],
  }) async {
    try {
      final res = await _dio.post<Map<String, dynamic>>(
        '/api/v1/storage/copy',
        data: {'target_uuid': targetUuid, 'folders': folders},
      );
      return StorageCopyStatus.fromJson(res.data ?? const {});
    } on DioException catch (e) {
      final body = e.response?.data;
      if (body is Map && body['title'] is String) {
        throw StorageCopyRefused(
            body['title'] as String, body['detail'] as String?);
      }
      rethrow;
    }
  }

  Future<StorageCopyStatus> status() async {
    final res = await _dio.get<Map<String, dynamic>>('/api/v1/storage/copy');
    return StorageCopyStatus.fromJson(res.data ?? const {});
  }

  /// Files already copied stay on the target.
  Future<void> cancel() async {
    try {
      await _dio.delete<void>('/api/v1/storage/copy');
    } on DioException catch (e) {
      // 404: it finished (or never ran) before the cancel arrived.
      if (e.response?.statusCode != 404) rethrow;
    }
  }

  void close() => _dio.close(force: true);
}

/// The server's copy status, polled every second while a copy runs and once
/// otherwise. Invalidate it after starting a copy to resume polling.
final storageCopyStatusProvider =
    StreamProvider.autoDispose<StorageCopyStatus>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) {
    return Stream.value(StorageCopyStatus.idle);
  }
  final api = StorageCopyApi(server);
  final controller = StreamController<StorageCopyStatus>();
  var disposed = false;
  ref.onDispose(() {
    disposed = true;
    api.close();
    controller.close();
  });
  Future<void> poll() async {
    // A Wi-Fi blip must not freeze the progress for good: a few failures in a
    // row are retried before the error is shown.
    var failures = 0;
    while (!disposed) {
      try {
        final status = await api.status();
        if (disposed) return;
        failures = 0;
        controller.add(status);
        if (!status.isRunning) return;
      } catch (e, st) {
        if (disposed) return;
        if (++failures >= 5) {
          controller.addError(e, st);
          return;
        }
      }
      await Future<void>.delayed(const Duration(seconds: 1));
    }
  }

  unawaited(poll());
  return controller.stream;
});
