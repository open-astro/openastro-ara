import 'package:dio/dio.dart';

import '../models/discovered_device.dart';
import '../models/server.dart';

/// Generic single-instance equipment-device REST client: live status +
/// connect/disconnect over `/api/v1/equipment/{path}`. Every non-Switch Alpaca
/// device shares this envelope (Switch is the multi-instance outlier with its own
/// list client). The status GET 404s when no device of this type is connected —
/// surfaced here as `null`, not a thrown error.
///
/// An interface so tests can supply a pure fake (no Dio / sockets);
/// [EquipmentDeviceApi] is the Dio-backed production implementation.
abstract interface class EquipmentDeviceClient<T> {
  /// The connected device's live status, or `null` when none is connected
  /// (`GET /api/v1/equipment/{path}` → 200 status / 404 when absent).
  Future<T?> getStatus();

  /// Connect the chosen device. 202-Accepted + background — poll [getStatus] for
  /// the resulting state (it first reads back `connecting`).
  Future<void> connect(DiscoveredDevice device);

  /// Disconnect the device (idempotent). 202-Accepted.
  Future<void> disconnect();

  void close();
}

/// Dio-backed [EquipmentDeviceClient] for one device type. [path] is the
/// equipment segment (e.g. `safetymonitor`, `focuser`); [fromJson] decodes the
/// device's status DTO.
class EquipmentDeviceApi<T> implements EquipmentDeviceClient<T> {
  final Dio _dio;
  final String _base;
  final T Function(Map<String, dynamic>) fromJson;

  EquipmentDeviceApi(
    AraServer server, {
    required String path,
    required this.fromJson,
  })  : _base = '/api/v1/equipment/$path',
        _dio = Dio(BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          receiveTimeout: const Duration(seconds: 5),
          // Bound the body write too — connect/disconnect are POSTs, so a server
          // that accepts the socket but stalls reading the body must not hang.
          sendTimeout: const Duration(seconds: 5),
        ));

  // A "not connected" device is a legitimate 404 on the status GET (only) — scope
  // the exemption to that request so a 404 on the connect/disconnect POSTs (a
  // routing/version bug) still throws instead of looking like a silent success.
  static final Options _getStatusOptions = Options(
    validateStatus: (status) => status != null && (status < 400 || status == 404),
  );

  @override
  Future<T?> getStatus() async {
    final res = await _dio.get<dynamic>(_base, options: _getStatusOptions);
    if (res.statusCode == 404) return null; // no device of this type connected
    final data = res.data;
    // Tolerate a malformed/proxy-rewritten non-object body without a TypeError
    // escaping — reads as "not connected" rather than crashing the panel.
    if (data is! Map<String, dynamic>) return null;
    return fromJson(data);
  }

  @override
  Future<void> connect(DiscoveredDevice device) async {
    await _dio.post<void>(
      '$_base/connect',
      data: <String, dynamic>{'device': device.toConnectRequestJson()},
    );
  }

  @override
  Future<void> disconnect() async {
    await _dio.post<void>('$_base/disconnect');
  }

  /// Releases the underlying Dio's connection pool. Call when the API is replaced
  /// (e.g. the active server changed) so sockets don't leak.
  @override
  void close() => _dio.close(force: true);
}
