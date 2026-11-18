import 'package:dio/dio.dart';

import '../models/alpacabridge_setup.dart';
import '../models/server.dart';

/// #1580 — the daemon's AlpacaBridge setup routes (`/api/v1/equipment/alpacabridge`). The daemon
/// dials the bridge at [BridgeAddress]; the client never does.
abstract class AlpacaBridgeSetupClient {
  Future<List<BridgeDriver>> catalog();
  Future<List<AttachedCamera>> cameras(String vendor);

  /// Never throws for an answer or a transport failure: both decode to an [AddOutcome].
  Future<AddOutcome> addDevice(Map<String, Object?> body);
  void close();
}

class AlpacaBridgeSetupApi implements AlpacaBridgeSetupClient {
  AlpacaBridgeSetupApi(
    AraServer server, {
    required String host,
    required int port,
    Dio? dio,
  }) : _query = {'host': host, 'port': port},
       _dio =
           dio ??
           Dio(
             BaseOptions(
               baseUrl: server.baseUrl,
               connectTimeout: const Duration(seconds: 3),
               receiveTimeout: const Duration(seconds: 15),
             ),
           );

  static const route = '/api/v1/equipment/alpacabridge';

  /// The bridge waits up to 30 s for an add; wait longer so a device that lands late is not read as lost.
  static const addTimeout = Duration(seconds: 40);

  final Dio _dio;
  final Map<String, Object?> _query;

  @override
  Future<List<BridgeDriver>> catalog() async {
    final res = await _dio.get<Map<String, dynamic>>(
      '$route/catalog',
      queryParameters: _query,
    );
    return BridgeDriver.listFromCatalogJson(res.data ?? const {});
  }

  @override
  Future<List<AttachedCamera>> cameras(String vendor) async {
    final res = await _dio.get<Map<String, dynamic>>(
      '$route/cameras',
      queryParameters: {..._query, 'vendor': vendor},
    );
    return [
      for (final c
          in (res.data?['cameras'] as List? ?? const [])
              .whereType<Map<String, dynamic>>())
        AttachedCamera(
          index: (c['index'] as num?)?.toInt() ?? 0,
          name: c['name'] as String? ?? '',
          serial: c['serial'] as String? ?? '',
        ),
    ];
  }

  @override
  Future<AddOutcome> addDevice(Map<String, Object?> body) async {
    try {
      final res = await _dio.post<dynamic>(
        '$route/devices',
        queryParameters: _query,
        data: body,
        options: Options(receiveTimeout: addTimeout),
      );
      return _decode(res.statusCode, res.data);
    } on DioException catch (e) {
      final r = e.response;
      // No answer (timeout, dropped connection): the device may have landed.
      return r == null ? const AddUnconfirmed() : _decode(r.statusCode, r.data);
    }
  }

  // ADR 0022 rule 5: status and transport decode here, so no widget reads a code.
  static AddOutcome _decode(int? status, Object? data) {
    final map = data is Map ? data : const {};
    final detail = map['detail'];
    if (status == 200) {
      return AddAdded(
        map['message'] as String? ?? '',
        map['warning'] as String?,
      );
    }
    // 4xx: nothing reached the bridge (or it said no); 5xx and anything else: unknown.
    // No detail: an empty one, which the panel words in the app's language.
    if (status != null && status >= 400 && status < 500) {
      return AddRefused(detail is String ? detail : '');
    }
    return const AddUnconfirmed();
  }

  @override
  void close() => _dio.close(force: true);
}
