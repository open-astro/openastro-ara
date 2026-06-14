import 'package:dio/dio.dart';

import '../models/server.dart';
import '../models/stats/focus_temp.dart';

/// The §50.4 Focus & Temperature read the state layer depends on. An interface
/// so tests can supply a pure fake (no Dio); [FocusTempApi] is the Dio-backed
/// production implementation.
abstract interface class FocusTempClient {
  /// The active profile's focus-vs-temperature scatter. Throws on a transport
  /// error or a non-object body (wire contract drift).
  Future<FocusTempSeries> fetch();

  void close();
}

/// Dio wrapper over `GET /api/v1/stats/focus-temp`.
class FocusTempApi implements FocusTempClient {
  final Dio _dio;

  /// [dio] is injectable so tests can supply a mock `HttpClientAdapter`;
  /// production passes nothing and a server-bound Dio is built.
  FocusTempApi(AraServer server, {Dio? dio})
      : _dio = dio ??
            Dio(BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 10),
            ));

  @override
  Future<FocusTempSeries> fetch() async {
    final res = await _dio.get<dynamic>('/api/v1/stats/focus-temp');
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw FormatException('stats/focus-temp returned a non-object body (${data.runtimeType})');
    }
    return FocusTempSeries.fromJson(data);
  }

  @override
  void close() => _dio.close(force: true);
}
