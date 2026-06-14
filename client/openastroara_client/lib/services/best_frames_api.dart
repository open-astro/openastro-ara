import 'package:dio/dio.dart';

import '../models/server.dart';
import '../models/stats/best_frame.dart';

/// The §50 Best Frames read the state layer depends on. An interface so tests
/// can supply a pure fake (no Dio); [BestFramesApi] is the Dio-backed
/// production implementation.
abstract interface class BestFramesClient {
  /// The catalog's top-ranked frames (best composite-quality first). Throws on
  /// a transport error or a non-object body (wire contract drift).
  Future<List<BestFrame>> fetch();

  void close();
}

/// Dio wrapper over `GET /api/v1/stats/best-frames`.
class BestFramesApi implements BestFramesClient {
  final Dio _dio;

  /// [dio] is injectable so tests can supply a mock `HttpClientAdapter`;
  /// production passes nothing and a server-bound Dio is built.
  BestFramesApi(AraServer server, {Dio? dio})
      : _dio = dio ??
            Dio(BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 10),
            ));

  @override
  Future<List<BestFrame>> fetch() async {
    final res = await _dio.get<dynamic>('/api/v1/stats/best-frames');
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw FormatException('stats/best-frames returned a non-object body (${data.runtimeType})');
    }
    return BestFrame.listFromJson(data);
  }

  @override
  void close() => _dio.close(force: true);
}
