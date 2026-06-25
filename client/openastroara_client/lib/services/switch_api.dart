import 'package:dio/dio.dart';

import '../models/discovered_device.dart';
import '../models/server.dart';
import '../models/switch_device.dart';

/// The multi-instance Switch operations the state layer depends on. An interface
/// so tests can supply a pure fake (no Dio / sockets); [SwitchApi] is the
/// Dio-backed production implementation.
abstract interface class SwitchClient {
  /// Every connected/known switch (`GET /api/v1/equipment/switch`).
  Future<List<SwitchDevice>> getAll();

  /// Connect an additional switch — does not evict the others. 202-Accepted; poll
  /// [getAll] for the resulting state. The device's `alpaca_device_number` becomes
  /// its address.
  Future<void> connect(DiscoveredDevice device);

  /// Disconnect the switch at [deviceNumber] (idempotent). 202-Accepted.
  Future<void> disconnect(int deviceNumber);

  /// Reconnect every switch the daemon remembers (no re-discovery), e.g. after a
  /// gear power-cycle (`POST /api/v1/equipment/switch/reconnect`). 202-Accepted;
  /// throws a 404 when no switch has ever been connected.
  Future<void> reconnect();

  /// Write [value] to [portId] of the switch at [deviceNumber]. 202-Accepted.
  Future<void> setValue({
    required int deviceNumber,
    required int portId,
    required double value,
  });

  void close();
}

/// Client wrapper around the §6 multi-instance Switch surface
/// (`/api/v1/equipment/switch[/{n}]`). Discovery of switches goes through the
/// shared `EquipmentDiscoveryApi` (`EquipmentDeviceType.switchDevice`); this API
/// covers list + connect + per-device disconnect/value. Connect/disconnect/value
/// are 202-Accepted (work runs in the background, state surfaces via `getAll`).
class SwitchApi implements SwitchClient {
  final Dio _dio;

  SwitchApi(AraServer server)
      : _dio = Dio(BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          receiveTimeout: const Duration(seconds: 5),
          // Bound the request-body write too: connect/disconnect/setValue are
          // POSTs on the control path, so a server that accepts the socket but
          // stalls reading the body must not hang the call indefinitely.
          sendTimeout: const Duration(seconds: 5),
        ));

  static const String _base = '/api/v1/equipment/switch';

  @override
  Future<List<SwitchDevice>> getAll() async {
    final res = await _dio.get<dynamic>(_base);
    final data = res.data;
    // The endpoint returns a JSON array; tolerate a non-list 200 body without a
    // TypeError escaping (a malformed/proxy-rewritten body reads as "no switches").
    if (data is! List) return const <SwitchDevice>[];
    return data
        .whereType<Map<String, dynamic>>()
        .map(SwitchDevice.fromJson)
        .toList(growable: false);
  }

  @override
  Future<void> connect(DiscoveredDevice device) async {
    await _dio.post<void>(
      '$_base/connect',
      data: <String, dynamic>{'device': device.toConnectRequestJson()},
    );
  }

  @override
  Future<void> disconnect(int deviceNumber) async {
    await _dio.post<void>('$_base/$deviceNumber/disconnect');
  }

  @override
  Future<void> reconnect() async {
    await _dio.post<void>('$_base/reconnect');
  }

  @override
  Future<void> setValue({
    required int deviceNumber,
    required int portId,
    required double value,
  }) async {
    await _dio.post<void>(
      '$_base/$deviceNumber/value',
      data: <String, dynamic>{'port_id': portId, 'value': value},
    );
  }

  /// Releases the underlying Dio's connection pool. Call when the API is replaced
  /// (e.g. the active server changed) so sockets don't leak.
  @override
  void close() => _dio.close(force: true);
}
