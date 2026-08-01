import 'package:dio/dio.dart';

/// §63.20 — read the REAL device names from an Alpaca server's management API
/// (`/management/v1/configureddevices`). The guider daemon's choice strings
/// are generic ("Alpaca Camera [host:port/N]"); the Alpaca server knows what
/// N actually is ("ZWO ASI290MM Mini"). Returns a map keyed
/// `"<devicetype>/<devicenumber>"` (type lowercased, e.g. `"camera/1"`) →
/// device name. Best-effort: any failure returns an empty map — callers fall
/// back to the generic labels.
abstract interface class AlpacaDeviceNamesClient {
  Future<Map<String, String>> fetchNames(String host, int port);
  void close();
}

class AlpacaDeviceNamesApi implements AlpacaDeviceNamesClient {
  final Dio _dio;

  AlpacaDeviceNamesApi()
      : _dio = Dio(BaseOptions(
          connectTimeout: const Duration(seconds: 3),
          receiveTimeout: const Duration(seconds: 5),
        ));

  @override
  Future<Map<String, String>> fetchNames(String host, int port) async {
    try {
      final res = await _dio.get<dynamic>(
          'http://$host:$port/management/v1/configureddevices');
      final data = res.data;
      final value = data is Map<String, dynamic> ? data['Value'] : null;
      if (value is! List) return const {};
      final names = <String, String>{};
      for (final entry in value) {
        if (entry is! Map<String, dynamic>) continue;
        final type = entry['DeviceType'];
        final number = entry['DeviceNumber'];
        final name = entry['DeviceName'];
        if (type is String && number is num && name is String && name.isNotEmpty) {
          names['${type.toLowerCase()}/${number.toInt()}'] = name;
        }
      }
      return names;
    } catch (_) {
      return const {};
    }
  }

  @override
  void close() => _dio.close(force: true);
}
