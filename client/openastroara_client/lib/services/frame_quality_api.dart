import 'package:dio/dio.dart';

import '../models/server.dart';
import '../models/stats/frame_quality.dart';

/// The §50.10 Frame Quality read the state layer depends on. An interface so
/// tests can supply a pure fake (no Dio); [FrameQualityApi] is the Dio-backed
/// production implementation.
abstract interface class FrameQualityClient {
  /// The composite frame-quality histogram for the active profile, optionally
  /// restricted to one [filter]. Throws on a transport error or a non-object
  /// body (wire contract drift).
  Future<FrameQualityDistribution> fetch({String? filter});

  void close();
}

/// Dio wrapper over `GET /api/v1/stats/frame-quality`.
class FrameQualityApi implements FrameQualityClient {
  final Dio _dio;

  /// [dio] is injectable so tests can supply a mock `HttpClientAdapter`;
  /// production passes nothing and a server-bound Dio is built.
  FrameQualityApi(AraServer server, {Dio? dio})
      : _dio = dio ??
            Dio(BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 10),
            ));

  @override
  Future<FrameQualityDistribution> fetch({String? filter}) async {
    final res = await _dio.get<dynamic>(
      '/api/v1/stats/frame-quality',
      queryParameters: filter == null ? null : {'filter': filter},
    );
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw FormatException('stats/frame-quality returned a non-object body (${data.runtimeType})');
    }
    return FrameQualityDistribution.fromJson(data);
  }

  @override
  void close() => _dio.close(force: true);
}
