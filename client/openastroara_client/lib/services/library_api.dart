import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';

import '../models/cursor_page.dart';
import '../models/library/live_library.dart';
import '../models/server.dart';

/// §40 image-library client (`/api/v1/sessions` + `/api/v1/frames`).
/// Interface first so tests can fake it, mirroring `SequenceClient`.
abstract interface class LibraryClient {
  /// One page at the server's cap; pass [cursor] from the previous page's
  /// [CursorPage.nextCursor] to continue.
  Future<CursorPage<LibrarySession>> listSessions(
      {int limit = 200, String? cursor});

  Future<List<LibraryFrameItem>> sessionFrames(String sessionId,
      {int limit = 200});

  /// GET url serving the frame's capture-time thumbnail JPEG (§40.4).
  String thumbnailUrl(String frameId);

  /// §40.8 bulk operations — the server answers 202 and applies them in the
  /// background; callers refresh after.
  Future<void> bulkRate(List<String> frameIds, int rating);

  Future<void> bulkTag(List<String> frameIds,
      {List<String> addTags = const [], List<String> removeTags = const []});

  Future<void> bulkDelete(List<String> frameIds,
      {bool deleteFromDisk = false});

  /// §40.8 move: reassign frames to another session (422 if it doesn't exist).
  Future<void> bulkMove(List<String> frameIds, String targetSessionId);

  /// §39.10 export: tar bytes of the selected frames' FITS files (server skips
  /// files missing on disk; 404 when nothing was exportable).
  Future<(List<int> bytes, String fileName)> exportFrames(List<String> frameIds);

  /// §40.6 resume-target: the server persists (or echoes) a runnable §38
  /// sequence seeded from the session and returns its id.
  Future<String> resumeTarget(String sessionId);

  /// Full frame detail for the viewer (tags + capture settings the list
  /// endpoint doesn't carry).
  Future<LibraryFrameDetail> frameDetail(String frameId);

  /// §65 stretched preview JPEG bytes for the frame viewer. [stretch] is one
  /// of the §65 palette ids (auto_stf, linear, log, asinh, sqrt, equalized,
  /// manual). The 0–1 normalized [blackPoint]/[midtonePoint]/[whitePoint]
  /// apply to the manual palette (§65.9); null falls back to the profile's
  /// manual-stretch seeds server-side.
  Future<List<int>> fetchPreview(String frameId,
      {required String stretch,
      int maxDimensionPx = 2048,
      double? blackPoint,
      double? midtonePoint,
      double? whitePoint});

  void close();
}

/// Dio wrapper over the §40 endpoints.
class LibraryApi implements LibraryClient {
  final Dio _dio;
  final String _baseUrl;

  LibraryApi(AraServer server, {Dio? dio})
      : _baseUrl = server.baseUrl,
        _dio = dio ??
            Dio(BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              sendTimeout: const Duration(seconds: 5),
              receiveTimeout: const Duration(seconds: 12),
            ));

  @override
  Future<CursorPage<LibrarySession>> listSessions(
      {int limit = 200, String? cursor}) async {
    final res = await _dio.get<dynamic>(
      '/api/v1/sessions',
      queryParameters: <String, dynamic>{
        'limit': limit,
        'cursor': ?cursor,
      },
    );
    // logTruncation false: a full page with has_more is the NORMAL paged case
    // here — the Load-more affordance handles it, no warning warranted (r4).
    final items = _parsePage(res.data, 'sessions', LibrarySession.fromJson,
        (s) => s.id.isNotEmpty, limit, logTruncation: false);
    final data = res.data as Map<String, dynamic>;
    final next = data['next_cursor'];
    return CursorPage(
      items: items,
      nextCursor: next is String && next.isNotEmpty ? next : null,
      hasMore: data['has_more'] == true,
    );
  }

  @override
  Future<List<LibraryFrameItem>> sessionFrames(String sessionId,
      {int limit = 200}) async {
    final res = await _dio.get<dynamic>(
      '/api/v1/sessions/$sessionId/frames',
      queryParameters: <String, dynamic>{'limit': limit},
    );
    return _parsePage(res.data, 'session frames', LibraryFrameItem.fromJson,
        (f) => f.id.isNotEmpty, limit);
  }

  @override
  String thumbnailUrl(String frameId) =>
      '$_baseUrl/api/v1/frames/$frameId/thumbnail';

  @override
  Future<void> bulkRate(List<String> frameIds, int rating) async {
    await _dio.post<dynamic>('/api/v1/frames/bulk/rate', data: <String, dynamic>{
      'frame_ids': frameIds,
      'rating': rating,
    });
  }

  @override
  Future<void> bulkTag(List<String> frameIds,
      {List<String> addTags = const [],
      List<String> removeTags = const []}) async {
    await _dio.post<dynamic>('/api/v1/frames/bulk/tag', data: <String, dynamic>{
      'frame_ids': frameIds,
      'add_tags': addTags,
      'remove_tags': removeTags,
    });
  }

  @override
  Future<void> bulkDelete(List<String> frameIds,
      {bool deleteFromDisk = false}) async {
    await _dio.post<dynamic>('/api/v1/frames/bulk/delete', data: <String, dynamic>{
      'frame_ids': frameIds,
      'delete_from_disk': deleteFromDisk,
    });
  }

  @override
  Future<LibraryFrameDetail> frameDetail(String frameId) async {
    final res = await _dio.get<dynamic>('/api/v1/frames/$frameId');
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw FormatException(
          'frame detail returned an unexpected body (${data.runtimeType})');
    }
    return LibraryFrameDetail.fromJson(data);
  }

  @override
  Future<List<int>> fetchPreview(String frameId,
      {required String stretch,
      int maxDimensionPx = 2048,
      double? blackPoint,
      double? midtonePoint,
      double? whitePoint}) async {
    final res = await _dio.post<List<int>>(
      '/api/v1/frames/$frameId/preview',
      data: <String, dynamic>{
        'stretch_palette': stretch,
        'black_point': blackPoint,
        'midtone_point': midtonePoint,
        'white_point': whitePoint,
        'max_dimension_px': maxDimensionPx,
        'apply_debayer': false,
      },
      options: Options(responseType: ResponseType.bytes),
    );
    final data = res.data;
    if (data == null || data.isEmpty) {
      throw const FormatException('frame preview returned an empty body');
    }
    return data;
  }

  @override
  Future<void> bulkMove(List<String> frameIds, String targetSessionId) async {
    await _dio.post<dynamic>('/api/v1/frames/bulk/move', data: <String, dynamic>{
      'frame_ids': frameIds,
      'target_session_id': targetSessionId,
    });
  }

  @override
  Future<(List<int>, String)> exportFrames(List<String> frameIds) async {
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
    return (data, match?.group(1) ?? 'openastroara-frames.tar');
  }

  @override
  Future<String> resumeTarget(String sessionId) async {
    final res = await _dio.post<dynamic>(
      '/api/v1/sessions/$sessionId/resume-target',
      data: <String, dynamic>{
        'recreate_sequence': false,
        'override_sequence_id': null,
      },
    );
    final data = res.data;
    final id = data is Map<String, dynamic> ? data['sequence_id'] : null;
    if (id is! String || id.isEmpty) {
      throw FormatException(
          'resume-target returned an unexpected body (${data.runtimeType})');
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
          '$what returned an unexpected body (${data.runtimeType})');
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

  @override
  void close() => _dio.close();
}
