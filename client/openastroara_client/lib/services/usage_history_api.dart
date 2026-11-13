import 'package:dio/dio.dart';

import '../models/server.dart';
import '../models/stats/usage_history.dart';

/// The #1582 usage-history read. An interface so tests can supply a pure fake.
abstract interface class UsageHistoryClient {
  /// The rig's samples (only those after [since] when given). Null when the
  /// daemon is older and has no such route (404): the panel keeps today's figures.
  Future<UsageHistory?> fetch({DateTime? since});
  void close();
}

/// Dio wrapper over `GET /api/v1/server/usage-history`.
class UsageHistoryApi implements UsageHistoryClient {
  final Dio _dio;

  UsageHistoryApi(AraServer server, {Dio? dio})
    : _dio =
          dio ??
          Dio(
            BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 10),
            ),
          );

  @override
  Future<UsageHistory?> fetch({DateTime? since}) async {
    final Response<dynamic> res;
    try {
      res = await _dio.get<dynamic>(
        '/api/v1/server/usage-history',
        queryParameters: since == null
            ? null
            : {'since': since.toUtc().toIso8601String()},
      );
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) return null;
      rethrow;
    }
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw FormatException(
        'server/usage-history returned a non-object body (${data.runtimeType})',
      );
    }
    return UsageHistory.fromJson(data);
  }

  @override
  void close() => _dio.close(force: true);
}
