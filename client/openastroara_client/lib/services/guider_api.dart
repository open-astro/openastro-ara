import 'package:dio/dio.dart';

import '../models/guider_status.dart';
import '../models/server.dart';

/// Default guider host/port (match the daemon's `GuiderConnectRequestDto`).
/// Declared once so the [GuiderApi] impl and the state-layer action don't drift.
const String kDefaultGuiderHost = 'localhost';
const int kDefaultGuiderPort = 4400;

/// The guider equipment operations the state layer depends on. An interface so
/// tests can supply a pure fake (no Dio / sockets); [GuiderApi] is the
/// Dio-backed production implementation.
abstract interface class GuiderClient {
  Future<GuiderStatus?> getStatus();
  Future<void> connect({String host, int port});
  Future<void> disconnect();
  void close();
}

/// Client wrapper around the §63 guider equipment surface
/// (`/api/v1/equipment/guider`). Drives the daemon's PHD2 client: read status,
/// connect/disconnect, and (start/stop/dither for a later slice). The daemon's
/// connect/disconnect are 202-Accepted (the work runs in the background and the
/// state transition surfaces via `GET` / WS), so these return when the request
/// is accepted, not when the guider is fully connected.
class GuiderApi implements GuiderClient {
  final Dio _dio;

  GuiderApi(AraServer server)
      : _dio = Dio(BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          receiveTimeout: const Duration(seconds: 5),
        ));

  /// Current guider descriptor, or `null` when the daemon has no guider
  /// configured (`404`). Other HTTP failures throw `DioException` for the
  /// caller to surface.
  @override
  Future<GuiderStatus?> getStatus() async {
    try {
      final res = await _dio.get<Map<String, dynamic>>('/api/v1/equipment/guider');
      final data = res.data;
      if (data == null) return null;
      return GuiderStatus.fromJson(data);
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) return null;
      rethrow;
    }
  }

  /// Ask the daemon to connect its PHD2 client to the guider at [host]:[port]
  /// (defaults match the daemon's `GuiderConnectRequestDto`). 202-Accepted;
  /// poll [getStatus] for the resulting link state.
  @override
  Future<void> connect({String host = kDefaultGuiderHost, int port = kDefaultGuiderPort}) async {
    // 202-Accepted with no body we read — poll getStatus() for the result.
    await _dio.post<void>(
      '/api/v1/equipment/guider/connect',
      data: <String, dynamic>{'host': host, 'port': port},
    );
  }

  /// Ask the daemon to disconnect the guider. 202-Accepted.
  @override
  Future<void> disconnect() async {
    await _dio.post<void>('/api/v1/equipment/guider/disconnect');
  }

  /// Releases the underlying Dio's connection pool. Call when the API is
  /// replaced (e.g. the active server changed) so sockets don't leak.
  @override
  void close() => _dio.close(force: true);
}
