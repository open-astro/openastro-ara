import 'package:dio/dio.dart';

import '../models/guider_equipment_choices.dart';
import '../models/server.dart';

/// The §63.17 guider equipment operations the state layer depends on. An
/// interface so tests can supply a pure fake (no Dio); [GuiderEquipmentApi] is
/// the Dio-backed production implementation.
abstract interface class GuiderEquipmentClient {
  Future<GuiderEquipmentChoicesResponse> getChoices();
  Future<List<String>> discoverAlpaca({int? numQueries, int? timeoutSeconds});
  Future<void> pushProfile();

  /// §63.20 — the camera's sensor pixel size (µm) read from its Alpaca driver
  /// via the daemon, or null when it couldn't be read (disconnected guider,
  /// unreachable camera, driver reports no size). Best-effort — callers fall
  /// back to manual entry.
  Future<double?> getAlpacaCameraPixelSize({
    String? host,
    int? port,
    int? device,
  });
  void close();
}

/// Dio wrapper over the §63.17 guider equipment surface
/// (`/api/v1/equipment/guider/{choices,discover,profile/push}`). Discovery is
/// deliberately synchronous on the daemon (a picker-button action, not a §60.5
/// background job) and can block for the whole sweep — the server caps the
/// combined sweep at 60 s, so [discoverAlpaca] carries a generous read budget.
/// The profile push is 202-Accepted (results arrive on the
/// `guider.profile_pushed` WS event), so it returns when accepted.
class GuiderEquipmentApi implements GuiderEquipmentClient {
  final Dio _dio;

  GuiderEquipmentApi(AraServer server)
      : _dio = Dio(BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          sendTimeout: const Duration(seconds: 5),
          // Base budget for choices + the quick 202 push; discoverAlpaca()
          // overrides with a sweep-sized read budget below.
          receiveTimeout: const Duration(seconds: 10),
        ));

  @override
  Future<GuiderEquipmentChoicesResponse> getChoices() async {
    final res = await _dio.get<dynamic>('/api/v1/equipment/guider/choices');
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      return const GuiderEquipmentChoicesResponse(connected: false);
    }
    return GuiderEquipmentChoicesResponse.fromJson(data);
  }

  @override
  Future<List<String>> discoverAlpaca({
    int? numQueries,
    int? timeoutSeconds,
  }) async {
    final res = await _dio.post<dynamic>(
      '/api/v1/equipment/guider/discover',
      data: <String, dynamic>{
        'num_queries': ?numQueries,
        'timeout_seconds': ?timeoutSeconds,
      },
      // The daemon blocks for the whole sweep (server-capped at 60 s combined);
      // 90 s leaves headroom for RPC dial + serialization on a busy daemon.
      options: Options(receiveTimeout: const Duration(seconds: 90)),
    );
    final data = res.data;
    if (data is! Map<String, dynamic>) return const [];
    final servers = data['servers'];
    return servers is List
        ? List.unmodifiable(servers.whereType<String>())
        : const [];
  }

  @override
  Future<void> pushProfile() async {
    await _dio.post<void>('/api/v1/equipment/guider/profile/push');
  }

  @override
  Future<double?> getAlpacaCameraPixelSize({
    String? host,
    int? port,
    int? device,
  }) async {
    final res = await _dio.get<dynamic>(
      '/api/v1/equipment/guider/camerapixelsize',
      queryParameters: <String, dynamic>{
        'host': ?host,
        'port': ?port,
        'device': ?device,
      },
    );
    final data = res.data;
    if (data is! Map<String, dynamic>) return null;
    return (data['pixel_size'] as num?)?.toDouble();
  }

  @override
  void close() => _dio.close(force: true);
}
