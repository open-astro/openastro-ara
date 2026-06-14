import 'package:dio/dio.dart';

import '../models/server.dart';
import '../models/stats/guiding_rms.dart';

/// The §50.7 Guiding RMS read the state layer depends on. An interface so tests
/// can supply a pure fake (no Dio); [GuidingRmsApi] is the Dio-backed
/// production implementation.
abstract interface class GuidingRmsClient {
  /// The active profile's guiding-RMS trend. Throws on a transport error or a
  /// non-object body (wire contract drift).
  Future<GuidingRmsSeries> fetch();

  void close();
}

/// Dio wrapper over `GET /api/v1/stats/guiding`.
class GuidingRmsApi implements GuidingRmsClient {
  final Dio _dio;

  /// [dio] is injectable so tests can supply a mock `HttpClientAdapter`;
  /// production passes nothing and a server-bound Dio is built.
  GuidingRmsApi(AraServer server, {Dio? dio})
      : _dio = dio ??
            Dio(BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 10),
            ));

  @override
  Future<GuidingRmsSeries> fetch() async {
    final res = await _dio.get<dynamic>('/api/v1/stats/guiding');
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw FormatException('stats/guiding returned a non-object body (${data.runtimeType})');
    }
    return GuidingRmsSeries.fromJson(data);
  }

  @override
  void close() => _dio.close(force: true);
}
