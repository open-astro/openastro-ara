import 'package:dio/dio.dart';

import '../models/cursor_page.dart';
import '../models/fault_row.dart';
import '../models/server.dart';

/// §42.5 fault-history client (`/api/v1/faults`). Interface first so tests and
/// future transports can fake it, mirroring `CalibrationClient`.
abstract interface class FaultsClient {
  /// One newest-first page; pass [cursor] from the previous page's
  /// [CursorPage.nextCursor] to continue. Filters are optional and
  /// AND-combined; [equipmentType]/[faultType] take the lowercase wire tokens.
  Future<CursorPage<FaultRow>> list({
    int limit = 200,
    String? cursor,
    String? equipmentType,
    String? faultType,
    String? sessionId,
    bool? unresolvedOnly,
  });

  /// One fault by id, or null when the server doesn't know it (404).
  Future<FaultRow?> getById(String id);

  void close();
}

/// Dio wrapper over `/api/v1/faults`.
class FaultsApi implements FaultsClient {
  final Dio _dio;

  FaultsApi(AraServer server, {Dio? dio})
      : _dio = dio ??
            Dio(BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              sendTimeout: const Duration(seconds: 5),
              receiveTimeout: const Duration(seconds: 12),
            ));

  @override
  Future<CursorPage<FaultRow>> list({
    int limit = 200,
    String? cursor,
    String? equipmentType,
    String? faultType,
    String? sessionId,
    bool? unresolvedOnly,
  }) async {
    final res = await _dio.get<dynamic>(
      '/api/v1/faults',
      // Query keys are the endpoint's parameter names (camelCase), matching
      // the equipment discovery `forceRefresh` precedent.
      queryParameters: <String, dynamic>{
        'limit': limit,
        'cursor': ?cursor,
        'equipmentType': ?equipmentType,
        'faultType': ?faultType,
        'sessionId': ?sessionId,
        'unresolvedOnly': ?unresolvedOnly,
      },
    );
    final data = res.data;
    // CursorPage envelope { items, next_cursor, has_more }; a 2xx with another
    // shape means the wire contract changed — throw so the notifier surfaces it.
    if (data is! Map<String, dynamic> || data['items'] is! List) {
      throw FormatException(
          'faults list returned an unexpected body (${data.runtimeType})');
    }
    final items = (data['items'] as List)
        .whereType<Map<String, dynamic>>()
        .map(FaultRow.fromJson)
        .where((f) => f.id.isNotEmpty)
        .toList(growable: false);
    final next = data['next_cursor'];
    return CursorPage(
      items: items,
      nextCursor: next is String && next.isNotEmpty ? next : null,
      hasMore: data['has_more'] == true,
    );
  }

  @override
  Future<FaultRow?> getById(String id) async {
    try {
      final res = await _dio.get<dynamic>('/api/v1/faults/$id');
      final data = res.data;
      if (data is! Map<String, dynamic>) {
        throw FormatException(
            'fault $id returned an unexpected body (${data.runtimeType})');
      }
      return FaultRow.fromJson(data);
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) return null;
      rethrow;
    }
  }

  @override
  void close() => _dio.close();
}
