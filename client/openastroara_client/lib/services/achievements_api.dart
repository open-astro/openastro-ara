import 'package:dio/dio.dart';

import '../models/server.dart';
import '../models/stats/achievements.dart';

/// The §50.19 achievements read the state layer depends on. An interface so
/// tests can supply a pure fake (no Dio); [AchievementsApi] is the Dio-backed
/// production implementation.
abstract interface class AchievementsClient {
  /// The active profile's cumulative records + milestone badges. Throws on a
  /// transport error or a non-object body (wire contract drift).
  Future<StatsAchievements> fetch();

  void close();
}

/// Dio wrapper over `GET /api/v1/stats/achievements`.
class AchievementsApi implements AchievementsClient {
  final Dio _dio;

  /// [dio] is injectable so tests can supply a mock `HttpClientAdapter`;
  /// production passes nothing and a server-bound Dio is built.
  AchievementsApi(AraServer server, {Dio? dio})
      : _dio = dio ??
            Dio(BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 10),
            ));

  @override
  Future<StatsAchievements> fetch() async {
    final res = await _dio.get<dynamic>('/api/v1/stats/achievements');
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      // A 2xx with a non-object body means the wire contract changed (or an error
      // envelope slipped through). Throw rather than return an empty record so the
      // AsyncNotifier surfaces an error state instead of a silent wall of zeros.
      throw FormatException('stats/achievements returned a non-object body (${data.runtimeType})');
    }
    return StatsAchievements.fromJson(data);
  }

  @override
  void close() => _dio.close(force: true);
}
