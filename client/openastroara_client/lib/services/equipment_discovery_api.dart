import 'package:dio/dio.dart';

import '../models/discovered_device.dart';
import '../models/server.dart';
import '../state/settings/equipment_connection_state.dart';

/// Client-side wrapper around §6.2 `GET /api/v1/equipment/discover/{type}`.
/// The Phase 6 daemon endpoint runs real Alpaca UDP discovery on port 32227
/// via `ASCOM.Alpaca.Discovery.GetAscomDevicesAsync` and returns the list of
/// matching devices.
class EquipmentDiscoveryApi {
  final Dio _dio;

  EquipmentDiscoveryApi(AraServer server)
      : _dio = Dio(BaseOptions(
          baseUrl: server.baseUrl,
          // Discovery can take 3-5s on a local subnet (Alpaca UDP timeout +
          // ID exchange per device). Generous read budget.
          connectTimeout: const Duration(seconds: 3),
          receiveTimeout: const Duration(seconds: 10),
        ));

  /// Returns the discovered devices of the given type. Throws `DioException`
  /// on connect/HTTP failure — caller surfaces the error UI.
  Future<List<DiscoveredDevice>> discover(
    EquipmentDeviceType type, {
    bool forceRefresh = false,
  }) async {
    final segment = DiscoveredDevice.pathSegmentFor(type);
    final res = await _dio.get<List<dynamic>>(
      '/api/v1/equipment/discover/$segment',
      queryParameters: forceRefresh ? {'forceRefresh': 'true'} : null,
    );
    final raw = res.data ?? const <dynamic>[];
    return raw
        .whereType<Map<String, dynamic>>()
        .map(DiscoveredDevice.fromJson)
        .toList();
  }

  /// Release the underlying connection pool. Callers that construct a one-shot
  /// instance (the wizard probe, the discovery sheet) must close it — each
  /// instance owns its own Dio, and an unclosed one leaks its pool.
  void close() => _dio.close(force: true);
}
