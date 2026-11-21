import 'package:dio/dio.dart';

import '../models/server.dart';
import '../models/stats/server_internal_state.dart';

/// The §66.7 internal-state read the Stats "Pi performance" section depends on.
/// An interface so tests can supply a pure fake (no Dio).
abstract interface class ServerInternalStateClient {
  Future<ServerInternalState> fetch();
  void close();
}

/// Dio wrapper over `GET /api/v1/server/internal-state`.
class ServerInternalStateApi implements ServerInternalStateClient {
  final Dio _dio;

  ServerInternalStateApi(AraServer server, {Dio? dio})
      : _dio = dio ??
            Dio(BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 10),
            ));

  @override
  Future<ServerInternalState> fetch() async {
    final res = await _dio.get<dynamic>('/api/v1/server/internal-state');
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw FormatException('server/internal-state returned a non-object body (${data.runtimeType})');
    }
    return ServerInternalState.fromJson(data);
  }

  @override
  void close() => _dio.close(force: true);
}
