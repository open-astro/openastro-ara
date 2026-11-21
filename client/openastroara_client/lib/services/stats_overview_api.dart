import 'package:dio/dio.dart';

import '../models/server.dart';
import '../models/stats/stats_overview.dart';

/// The §50 Stats Overview read the state layer depends on. An interface so tests
/// can supply a pure fake (no Dio); [StatsOverviewApi] is the Dio-backed
/// production implementation.
abstract interface class StatsOverviewClient {
  /// The active profile's catalog totals. Throws on a transport error or a
  /// non-object body (wire contract drift).
  Future<StatsOverview> fetch();

  void close();
}

/// Dio wrapper over `GET /api/v1/stats/overview`.
class StatsOverviewApi implements StatsOverviewClient {
  final Dio _dio;

  /// [dio] is injectable so tests can supply a mock `HttpClientAdapter`;
  /// production passes nothing and a server-bound Dio is built.
  StatsOverviewApi(AraServer server, {Dio? dio})
      : _dio = dio ??
            Dio(BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 10),
            ));

  @override
  Future<StatsOverview> fetch() async {
    final res = await _dio.get<dynamic>('/api/v1/stats/overview');
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw FormatException('stats/overview returned a non-object body (${data.runtimeType})');
    }
    return StatsOverview.fromJson(data);
  }

  @override
  void close() => _dio.close(force: true);
}
