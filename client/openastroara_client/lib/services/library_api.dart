import 'dart:io';

import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';

import '../models/cursor_page.dart';
import '../models/library/frame_viewer.dart';
import '../models/library/live_library.dart';
import '../models/server.dart';
import 'content_disposition.dart';

/// §40 image-library client (`/api/v1/sessions` + `/api/v1/frames`).
/// Interface first so tests can fake it, mirroring `SequenceClient`.
abstract interface class LibraryClient {
  /// One page at the server's cap; pass [cursor] from the previous page's
  /// [CursorPage.nextCursor] to continue.
  Future<CursorPage<LibrarySession>> listSessions({
    int limit = 200,
    String? cursor,
  });

  Future<List<LibraryFrameItem>> sessionFrames(
    String sessionId, {
    int limit = 200,
  });

  /// GET url serving the frame's capture-time thumbnail JPEG (§40.4).
  String thumbnailUrl(String frameId);

  /// §40.8 bulk operations — the server answers 202 and applies them in the
  /// background; callers refresh after.
  Future<void> bulkRate(List<String> frameIds, int rating);

  Future<void> bulkTag(
    List<String> frameIds, {
    List<String> addTags = const [],
    List<String> removeTags = const [],
  });

  Future<void> bulkDelete(List<String> frameIds, {bool deleteFromDisk = false});

  /// §40.8 move: reassign frames to another session (422 if it doesn't exist).
  Future<void> bulkMove(List<String> frameIds, String targetSessionId);

  /// §39.10 export: tar bytes of the selected frames' FITS files (server skips
  /// files missing on disk; 404 when nothing was exportable). [exportedCount]
  /// is how many frames actually made it into the tar — export is
  /// partial-success by design.
  Future<(List<int> bytes, String fileName, int exportedCount)> exportFrames(
    List<String> frameIds,
  );

  /// §40.6 resume-target: the server persists (or echoes) a runnable §38
  /// sequence seeded from the session and returns its id.
  Future<String> resumeTarget(String sessionId);

  /// Full frame detail for the viewer (tags + capture settings the list
  /// endpoint doesn't carry).
  Future<LibraryFrameDetail> frameDetail(String frameId);

  /// Durable source, analysis, preview, and quarantine state.
  Future<FrameMetadata> frameMetadata(String frameId);

  /// §65 preview bytes plus the exact server-applied render parameters.
  Future<FramePreviewImage> fetchPreview(
    String frameId,
    FramePreviewOptions options, {
    CancelToken? cancelToken,
  });

  /// Stream the original source file directly to [savePath].
  Future<String> downloadFrameTo(
    String frameId,
    String savePath, {
    CancelToken? cancelToken,
  });

  Future<FrameOperationAccepted> rebuildPreview(
    String frameId,
    FramePreviewOptions options,
  );

  Future<FrameOperationAccepted> reanalyze(
    String frameId, {
    double? starSensitivity,
    int? starNoiseReduction,
  });

  Future<FrameOperationAccepted> bulkQuarantine(
    List<String> frameIds, {
    required bool quarantined,
    String? reason,
  });

  Future<FrameJobStatus> jobStatus(String jobId);

  Future<void> cancelJob(String jobId);

  void close();
}

/// Dio wrapper over the §40 endpoints.
class LibraryApi implements LibraryClient {
  final Dio _dio;
  final String _baseUrl;
  int _uniqueSequence = 0;

  LibraryApi(AraServer server, {Dio? dio})
    : _baseUrl = server.baseUrl,
      _dio =
          dio ??
          Dio(
            BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              sendTimeout: const Duration(seconds: 5),
              receiveTimeout: const Duration(seconds: 12),
            ),
          );

  @override
  Future<CursorPage<LibrarySession>> listSessions({
    int limit = 200,
    String? cursor,
  }) async {
    final res = await _dio.get<dynamic>(
      '/api/v1/sessions',
      queryParameters: <String, dynamic>{'limit': limit, 'cursor': ?cursor},
    );
    // logTruncation false: a full page with has_more is the NORMAL paged case
    // here — the Load-more affordance handles it, no warning warranted (r4).
    final items = _parsePage(
      res.data,
      'sessions',
      LibrarySession.fromJson,
      (s) => s.id.isNotEmpty,
      limit,
      logTruncation: false,
    );
    final data = res.data as Map<String, dynamic>;
    final next = data['next_cursor'];
    return CursorPage(
      items: items,
      nextCursor: next is String && next.isNotEmpty ? next : null,
      hasMore: data['has_more'] == true,
    );
  }

  @override
  Future<List<LibraryFrameItem>> sessionFrames(
    String sessionId, {
    int limit = 200,
  }) async {
    final res = await _dio.get<dynamic>(
      '/api/v1/sessions/$sessionId/frames',
      queryParameters: <String, dynamic>{'limit': limit},
    );
    return _parsePage(
      res.data,
      'session frames',
      LibraryFrameItem.fromJson,
      (f) => f.id.isNotEmpty,
      limit,
    );
  }

  @override
  String thumbnailUrl(String frameId) =>
      '$_baseUrl/api/v1/frames/$frameId/thumbnail';

  @override
  Future<void> bulkRate(List<String> frameIds, int rating) async {
    await _postIdempotent<dynamic>(
      '/api/v1/frames/bulk/rate',
      <String, dynamic>{'frame_ids': frameIds, 'rating': rating},
      'rate',
    );
  }

  @override
  Future<void> bulkTag(
    List<String> frameIds, {
    List<String> addTags = const [],
    List<String> removeTags = const [],
  }) async {
    await _postIdempotent<dynamic>('/api/v1/frames/bulk/tag', <String, dynamic>{
      'frame_ids': frameIds,
      'add_tags': addTags,
      'remove_tags': removeTags,
    }, 'tag');
  }

  @override
  Future<void> bulkDelete(
    List<String> frameIds, {
    bool deleteFromDisk = false,
  }) async {
    await _postIdempotent<dynamic>(
      '/api/v1/frames/bulk/delete',
      <String, dynamic>{
        'frame_ids': frameIds,
        'delete_from_disk': deleteFromDisk,
      },
      'delete',
    );
  }

  @override
  Future<LibraryFrameDetail> frameDetail(String frameId) async {
    final res = await _dio.get<dynamic>('/api/v1/frames/$frameId');
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw FormatException(
        'frame detail returned an unexpected body (${data.runtimeType})',
      );
    }
    return LibraryFrameDetail.fromJson(data);
  }

  @override
  Future<FrameMetadata> frameMetadata(String frameId) async {
    final res = await _dio.get<dynamic>('/api/v1/frames/$frameId/metadata');
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw FormatException(
        'frame metadata returned an unexpected body (${data.runtimeType})',
      );
    }
    return FrameMetadata.fromJson(data);
  }

  @override
  Future<FramePreviewImage> fetchPreview(
    String frameId,
    FramePreviewOptions options, {
    CancelToken? cancelToken,
  }) async {
    final res = await _dio.post<List<int>>(
      '/api/v1/frames/$frameId/preview',
      data: options.toJson(),
      cancelToken: cancelToken,
      options: Options(responseType: ResponseType.bytes),
    );
    final data = res.data;
    if (data == null || data.isEmpty) {
      throw const FormatException('frame preview returned an empty body');
    }
    final headerValues = <String, String?>{
      for (final entry in res.headers.map.entries)
        entry.key.toLowerCase(): entry.value.isEmpty
            ? null
            : entry.value.join(','),
    };
    return FramePreviewImage(
      bytes: data is Uint8List ? data : Uint8List.fromList(data),
      applied: FramePreviewApplied.fromHeaders(headerValues),
    );
  }

  @override
  Future<String> downloadFrameTo(
    String frameId,
    String savePath, {
    CancelToken? cancelToken,
  }) async {
    final destination = File(savePath);
    if (await FileSystemEntity.type(savePath, followLinks: false) !=
        FileSystemEntityType.notFound) {
      throw FileSystemException(
        'Refusing to overwrite an existing frame download.',
        savePath,
      );
    }
    final partial = File(
      '${destination.parent.path}${Platform.pathSeparator}'
      '.openastroara-${_uniqueToken('download')}.part',
    );
    try {
      final res = await _dio.download(
        '/api/v1/frames/$frameId/download',
        partial.path,
        cancelToken: cancelToken,
        options: Options(receiveTimeout: const Duration(minutes: 5)),
      );
      if (!await partial.exists() || await partial.length() == 0) {
        throw const FormatException('frame download returned an empty body');
      }
      // Normal picker paths are unique. Re-check before the atomic same-folder
      // rename so an unexpected concurrent creator is never overwritten.
      if (await FileSystemEntity.type(savePath, followLinks: false) !=
          FileSystemEntityType.notFound) {
        throw FileSystemException(
          'The selected frame download path became occupied.',
          savePath,
        );
      }
      await partial.rename(destination.path);
      return fileNameFromContentDisposition(
            res.headers.value('content-disposition'),
          ) ??
          'openastroara-$frameId.fits';
    } catch (_) {
      if (await partial.exists()) {
        try {
          await partial.delete();
        } on FileSystemException {
          // Preserve the original transport error; cleanup is best-effort.
        }
      }
      rethrow;
    }
  }

  @override
  Future<FrameOperationAccepted> rebuildPreview(
    String frameId,
    FramePreviewOptions options,
  ) async {
    final res = await _postIdempotent<dynamic>(
      '/api/v1/frames/$frameId/rebuild-preview',
      options.toJson(),
      'rebuild',
    );
    return _operation(res.data, 'rebuild-preview');
  }

  @override
  Future<FrameOperationAccepted> reanalyze(
    String frameId, {
    double? starSensitivity,
    int? starNoiseReduction,
  }) async {
    final res = await _postIdempotent<dynamic>(
      '/api/v1/frames/$frameId/reanalyze',
      <String, dynamic>{
        'star_sensitivity': starSensitivity,
        'star_noise_reduction': starNoiseReduction,
      },
      'reanalyze',
    );
    return _operation(res.data, 'reanalyze');
  }

  @override
  Future<FrameOperationAccepted> bulkQuarantine(
    List<String> frameIds, {
    required bool quarantined,
    String? reason,
  }) async {
    final res = await _postIdempotent<dynamic>(
      '/api/v1/frames/bulk/quarantine',
      <String, dynamic>{
        'frame_ids': frameIds,
        'quarantined': quarantined,
        'reason': reason,
      },
      quarantined ? 'quarantine' : 'restore',
    );
    return _operation(res.data, 'bulk-quarantine');
  }

  @override
  Future<FrameJobStatus> jobStatus(String jobId) async {
    final res = await _dio.get<dynamic>('/api/v1/jobs/$jobId');
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw FormatException(
        'job status returned an unexpected body (${data.runtimeType})',
      );
    }
    return FrameJobStatus.fromJson(data);
  }

  @override
  Future<void> cancelJob(String jobId) async {
    await _dio.delete<dynamic>('/api/v1/jobs/$jobId');
  }

  @override
  Future<void> bulkMove(List<String> frameIds, String targetSessionId) async {
    await _postIdempotent<dynamic>(
      '/api/v1/frames/bulk/move',
      <String, dynamic>{
        'frame_ids': frameIds,
        'target_session_id': targetSessionId,
      },
      'move',
    );
  }

  @override
  Future<(List<int>, String, int)> exportFrames(List<String> frameIds) async {
    final res = await _dio.post<List<int>>(
      '/api/v1/frames/bulk/export',
      data: <String, dynamic>{'frame_ids': frameIds},
      options: Options(responseType: ResponseType.bytes),
    );
    final data = res.data;
    if (data == null || data.isEmpty) {
      throw const FormatException('frame export returned an empty body');
    }
    // The server names the tar via Content-Disposition; fall back sanely.
    final disposition = res.headers.value('content-disposition') ?? '';
    final match = RegExp('filename="?([^";]+)"?').firstMatch(disposition);
    final count =
        int.tryParse(res.headers.value('x-ara-exported-count') ?? '') ??
        frameIds.length;
    return (data, match?.group(1) ?? 'openastroara-frames.tar', count);
  }

  @override
  Future<String> resumeTarget(String sessionId) async {
    final res = await _postIdempotent<dynamic>(
      '/api/v1/sessions/$sessionId/resume-target',
      <String, dynamic>{
        'recreate_sequence': false,
        'override_sequence_id': null,
      },
      'resume-target',
    );
    final data = res.data;
    final id = data is Map<String, dynamic> ? data['sequence_id'] : null;
    if (id is! String || id.isEmpty) {
      throw FormatException(
        'resume-target returned an unexpected body (${data.runtimeType})',
      );
    }
    return id;
  }

  /// CursorPage envelope { items, next_cursor, has_more }; a 2xx with another
  /// shape means the wire contract changed — throw so the notifier surfaces it.
  static List<T> _parsePage<T>(
    dynamic data,
    String what,
    T Function(Map<String, dynamic>) fromJson,
    bool Function(T) keep,
    int limit, {
    bool logTruncation = true,
  }) {
    if (data is! Map<String, dynamic> || data['items'] is! List) {
      throw FormatException(
        '$what returned an unexpected body (${data.runtimeType})',
      );
    }
    if (logTruncation && data['has_more'] == true) {
      // Frame strips stay first-page-only by design (a 200-frame strip is
      // already beyond useful scroll) — surface the truncation in logs.
      debugPrint('$what truncated to first $limit — more exist');
    }
    return (data['items'] as List)
        .whereType<Map<String, dynamic>>()
        .map(fromJson)
        .where(keep)
        .toList(growable: false);
  }

  Future<Response<T>> _postIdempotent<T>(
    String path,
    Object? data,
    String operation,
  ) async {
    final key = _uniqueToken('wilma-$operation');
    final options = Options(headers: <String, String>{'Idempotency-Key': key});
    try {
      return await _dio.post<T>(path, data: data, options: options);
    } on DioException catch (error) {
      // The daemon may have accepted the mutation before the connection died.
      // Retry once with the same key; a response-bearing 4xx/5xx is definitive.
      if (error.response != null || error.type == DioExceptionType.cancel) {
        rethrow;
      }
      return _dio.post<T>(path, data: data, options: options);
    }
  }

  static FrameOperationAccepted _operation(dynamic data, String operation) {
    if (data is! Map<String, dynamic>) {
      throw FormatException(
        '$operation returned an unexpected body (${data.runtimeType})',
      );
    }
    return FrameOperationAccepted.fromJson(data);
  }

  String _uniqueToken(String prefix) =>
      '$prefix-${DateTime.now().microsecondsSinceEpoch}-'
      '${_uniqueSequence++}-${identityHashCode(this).toRadixString(16)}';

  @override
  void close() => _dio.close(force: true);
}
