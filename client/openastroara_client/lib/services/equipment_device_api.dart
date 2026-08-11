import 'package:dio/dio.dart';

import '../models/discovered_device.dart';
import '../models/server.dart';

/// A short, user-facing message for an equipment API error. A [DioException]'s
/// `toString()` dumps the request URL + headers + body (a multi-line paragraph in
/// a SnackBar), so extract just the gist. Shared by every equipment panel.
String describeEquipmentError(Object? e) {
  if (e == null) return 'unknown error';
  if (e is DioException) {
    final code = e.response?.statusCode;
    if (code != null) return 'server returned $code';
    return e.message ?? 'network error';
  }
  return e.toString().replaceFirst('Exception: ', '');
}

/// True when [e] is a 404 from the equipment API — e.g. a reconnect with no
/// remembered device for that type, distinct from a real failure.
bool isNotFoundEquipmentError(Object? e) =>
    e is DioException && e.response?.statusCode == 404;

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

  /// POST a device-specific control command to `{base}/{subpath}` (e.g. a focuser
  /// `move`, a filter-wheel `position`, a flat-device `apply`). 202-Accepted +
  /// background — poll [getStatus] for the result. [body] is the JSON request, or
  /// omitted for a bodyless command.
  Future<void> command(String subpath, [Map<String, dynamic>? body]);

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
    // A 200 whose body isn't a JSON object (a proxy HTML error page, a rewritten
    // response) is an ERROR, not "not connected" — throw so it surfaces as the
    // error state (Retry) rather than silently masquerading as no-device. Caught
    // by the caller's AsyncValue.guard; no TypeError escapes.
    if (data is! Map<String, dynamic>) {
      throw Exception(
          'unexpected ${res.statusCode} body from $_base — not a JSON object');
    }
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

  @override
  Future<void> command(String subpath, [Map<String, dynamic>? body]) async {
    await _dio.post<void>('$_base/$subpath', data: body);
  }

  /// Releases the underlying Dio's connection pool. Call when the API is replaced
  /// (e.g. the active server changed) so sockets don't leak.
  @override
  void close() => _dio.close(force: true);
}
