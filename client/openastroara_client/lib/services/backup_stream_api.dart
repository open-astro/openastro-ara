import 'dart:typed_data';

import 'package:dio/dio.dart';

import '../models/backup_stream.dart';
import '../models/server.dart';

/// The §44 backup-stream operations the puller depends on. An interface so
/// tests can supply a pure fake; [BackupStreamApi] is the Dio-backed
/// production implementation.
abstract interface class BackupStreamClient {
  /// Daemon-side rollup (enabled / active target / pending / synced / bytes).
  Future<BackupStreamStatus> status();

  /// Claim the single stream slot as [hostname]. True on success; false when
  /// another desktop holds it (409 — its hostname rides the problem detail,
  /// surfaced via the thrown-through message on other errors).
  Future<bool> claim(String hostname);

  /// Voluntarily release the slot. A non-holder release is a no-op.
  Future<void> release(String hostname);

  /// The pending queue, oldest first. Throws on 409 (slot lost — re-claim).
  Future<List<BackupStreamQueueEntry>> queue(String hostname, {int limit = 50});

  /// Ack a stored + sha-verified frame. Throws on 409 (slot lost).
  Future<void> ack(String hostname, String frameId);

  /// The frame's FITS bytes via the existing download endpoint.
  Future<Uint8List> downloadFrame(String frameId);

  void close();
}

/// Signals the §44.5 slot was lost to another desktop (409) — the puller
/// re-claims (its own hostname re-claims idempotently) or stops.
class BackupStreamSlotLostException implements Exception {
  final String? holder;
  const BackupStreamSlotLostException(this.holder);
  @override
  String toString() => 'backup-stream slot held by ${holder ?? 'another desktop'}';
}

/// Dio wrapper over `/api/v1/server/backup-stream/*` + the frame download.
class BackupStreamApi implements BackupStreamClient {
  final Dio _dio;

  /// [dio] is injectable for tests; production builds a server-bound Dio. The
  /// download timeout is generous — a full-frame FITS over Wi-Fi takes a while.
  BackupStreamApi(AraServer server, {Dio? dio})
      : _dio = dio ??
            Dio(BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              sendTimeout: const Duration(seconds: 10),
              receiveTimeout: const Duration(minutes: 5),
            ));

  @override
  Future<BackupStreamStatus> status() async {
    final res = await _dio.get<Map<String, dynamic>>('/api/v1/server/backup-stream/status');
    final data = res.data;
    if (data == null) {
      throw const FormatException('backup-stream/status returned a non-object body');
    }
    return BackupStreamStatus.fromJson(data);
  }

  @override
  Future<bool> claim(String hostname) async {
    try {
      await _dio.post<dynamic>('/api/v1/server/backup-stream/claim',
          data: {'hostname': hostname});
      return true;
    } on DioException catch (e) {
      if (e.response?.statusCode == 409) return false;
      rethrow;
    }
  }

  @override
  Future<void> release(String hostname) async {
    try {
      await _dio.post<dynamic>('/api/v1/server/backup-stream/release',
          data: {'hostname': hostname});
    } on DioException catch (e) {
      // 404 = we weren't the holder — already the desired end state.
      if (e.response?.statusCode != 404) rethrow;
    }
  }

  @override
  Future<List<BackupStreamQueueEntry>> queue(String hostname, {int limit = 50}) async {
    try {
      final res = await _dio.get<dynamic>('/api/v1/server/backup-stream/queue',
          queryParameters: {'hostname': hostname, 'limit': limit});
      final data = res.data;
      if (data is! List) {
        throw FormatException('backup-stream/queue returned a non-array body (${data.runtimeType})');
      }
      return data
          .whereType<Map<String, dynamic>>()
          .map(BackupStreamQueueEntry.fromJson)
          .where((e) => e.id.isNotEmpty)
          .toList(growable: false);
    } on DioException catch (e) {
      if (e.response?.statusCode == 409) {
        throw BackupStreamSlotLostException(_holderFrom(e));
      }
      rethrow;
    }
  }

  @override
  Future<void> ack(String hostname, String frameId) async {
    try {
      await _dio.post<dynamic>('/api/v1/server/backup-stream/ack',
          queryParameters: {'hostname': hostname},
          data: {'frame_id': frameId, 'sha256_verified': true});
    } on DioException catch (e) {
      if (e.response?.statusCode == 409) {
        throw BackupStreamSlotLostException(_holderFrom(e));
      }
      // 404 = the frame vanished from the catalog between queue and ack — the
      // next queue poll simply won't list it; nothing to surface.
      if (e.response?.statusCode != 404) rethrow;
    }
  }

  @override
  Future<Uint8List> downloadFrame(String frameId) async {
    final res = await _dio.get<List<int>>('/api/v1/frames/$frameId/download',
        options: Options(responseType: ResponseType.bytes));
    final data = res.data;
    if (data == null) {
      throw const FormatException('frame download returned an empty body');
    }
    return Uint8List.fromList(data);
  }

  static String? _holderFrom(DioException e) {
    final data = e.response?.data;
    return data is Map<String, dynamic> ? data['detail'] as String? : null;
  }

  @override
  void close() => _dio.close();
}
