import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';

import '../models/library/live_library.dart';
import '../models/server.dart';

/// §40 image-library client (`/api/v1/sessions` + `/api/v1/frames`).
/// Interface first so tests can fake it, mirroring `SequenceClient`.
abstract interface class LibraryClient {
  /// First page at the server's cap — cursor paging for larger catalogs is
  /// tracked in PORT_TODO alongside the calibration screen's.
  Future<List<LibrarySession>> listSessions({int limit = 200});

  Future<List<LibraryFrameItem>> sessionFrames(String sessionId,
      {int limit = 200});

  /// GET url serving the frame's capture-time thumbnail JPEG (§40.4).
  String thumbnailUrl(String frameId);

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
  Future<List<LibrarySession>> listSessions({int limit = 200}) async {
    final res = await _dio.get<dynamic>(
      '/api/v1/sessions',
      queryParameters: <String, dynamic>{'limit': limit},
    );
    return _parsePage(res.data, 'sessions', LibrarySession.fromJson,
        (s) => s.id.isNotEmpty, limit);
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

  /// CursorPage envelope { items, next_cursor, has_more }; a 2xx with another
  /// shape means the wire contract changed — throw so the notifier surfaces it.
  static List<T> _parsePage<T>(
    dynamic data,
    String what,
    T Function(Map<String, dynamic>) fromJson,
    bool Function(T) keep,
    int limit,
  ) {
    if (data is! Map<String, dynamic> || data['items'] is! List) {
      throw FormatException(
          '$what returned an unexpected body (${data.runtimeType})');
    }
    if (data['has_more'] == true) {
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
