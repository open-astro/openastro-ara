import 'package:dio/dio.dart';

import '../models/polar_align.dart';
import '../models/server.dart';

/// The §45 polar-align operations the state layer depends on. An interface so
/// tests supply a pure fake; [PolarAlignApi] is the Dio-backed implementation.
abstract interface class PolarAlignClient {
  Future<PolarAlignStatus?> getStatus();
  Future<void> start();
  Future<void> stop();
  Future<void> complete();
  Future<PolarAlignSettings> getSettings();
  Future<PolarAlignSettings> putSettings(PolarAlignSettings settings);
  void close();
}

/// Client wrapper around the §45 surface: the routine
/// (`/api/v1/equipment/polaralign/{status,start,stop,complete}` — the POSTs are
/// 202-Accepted; live progress arrives on the `polar_align.*` WS stream) and the
/// §45.12 profile section (`/api/v1/profile/polar-align`).
class PolarAlignApi implements PolarAlignClient {
  final Dio _dio;

  PolarAlignApi(AraServer server)
      : _dio = Dio(BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          receiveTimeout: const Duration(seconds: 5),
        ));

  /// Current routine snapshot, or `null` on a `404` (a server predating §45).
  /// Other HTTP failures throw `DioException` for the caller to surface.
  @override
  Future<PolarAlignStatus?> getStatus() async {
    try {
      final res = await _dio.get<dynamic>('/api/v1/equipment/polaralign/status');
      final data = res.data;
      if (data is! Map<String, dynamic>) return null;
      return PolarAlignStatus.fromJson(data);
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) return null;
      rethrow;
    }
  }

  /// Begin the routine (preflight: connected guider + mount, configured site).
  /// 202-Accepted; a failed preflight surfaces as a 409/422 `DioException`.
  @override
  Future<void> start() async {
    await _dio.post<void>('/api/v1/equipment/polaralign/start');
  }

  /// Abort — the mount stays where it is; logged as `aborted`. 202-Accepted.
  @override
  Future<void> stop() async {
    await _dio.post<void>('/api/v1/equipment/polaralign/stop');
  }

  /// The user is satisfied: same unwind as [stop] but the §45.13 session row
  /// records `complete` with the achieved error. 202-Accepted.
  @override
  Future<void> complete() async {
    await _dio.post<void>('/api/v1/equipment/polaralign/complete');
  }

  @override
  Future<PolarAlignSettings> getSettings() async {
    final res = await _dio.get<dynamic>('/api/v1/profile/polar-align');
    final data = res.data;
    return data is Map<String, dynamic>
        ? PolarAlignSettings.fromJson(data)
        : const PolarAlignSettings();
  }

  @override
  Future<PolarAlignSettings> putSettings(PolarAlignSettings settings) async {
    final res = await _dio.put<dynamic>(
      '/api/v1/profile/polar-align',
      data: settings.toJson(),
    );
    final data = res.data;
    return data is Map<String, dynamic>
        ? PolarAlignSettings.fromJson(data)
        : settings;
  }

  /// Releases the underlying Dio's connection pool. Call when the API is
  /// replaced (e.g. the active server changed) so sockets don't leak.
  @override
  void close() => _dio.close(force: true);
}
