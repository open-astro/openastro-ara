import 'package:dio/dio.dart';

import '../models/server.dart';
import '../models/stats/calendar_stats.dart';

/// The §50.6 Calendar read the state layer depends on. An interface so tests can
/// supply a pure fake (no Dio); [CalendarApi] is the Dio-backed production
/// implementation.
abstract interface class CalendarClient {
  /// The captured days in the most recent [days]-day window (inclusive of
  /// today). Throws on a transport error or a non-object body (wire contract
  /// drift).
  Future<CalendarStats> fetch({int days});

  void close();
}

/// Dio wrapper over `GET /api/v1/stats/calendar`.
class CalendarApi implements CalendarClient {
  final Dio _dio;

  /// [dio] is injectable so tests can supply a mock `HttpClientAdapter`;
  /// production passes nothing and a server-bound Dio is built.
  CalendarApi(AraServer server, {Dio? dio})
      : _dio = dio ??
            Dio(BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 10),
            ));

  @override
  Future<CalendarStats> fetch({int days = 49}) async {
    final now = DateTime.now();
    final today = DateTime(now.year, now.month, now.day);
    final from = today.subtract(Duration(days: days - 1));
    // Same `yyyy-MM-dd` formatter the model keys cells by, so query bounds and
    // cell keys never diverge.
    final res = await _dio.get<dynamic>(
      '/api/v1/stats/calendar',
      queryParameters: {
        'fromDate': CalendarStats.dayKey(from),
        'toDate': CalendarStats.dayKey(today),
      },
    );
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw FormatException('stats/calendar returned a non-object body (${data.runtimeType})');
    }
    return CalendarStats.fromJson(data);
  }

  @override
  void close() => _dio.close(force: true);
}
