import 'package:dio/dio.dart';

import '../models/server.dart';
import '../models/stats/stats_target.dart';

/// The §50.19 export reads the state/UI layers depend on. An interface so tests
/// can supply a pure fake (no Dio); [StatsExportApi] is the Dio-backed
/// production implementation.
abstract interface class StatsExportClient {
  /// Targets the catalog has frames for, busiest first. Throws on a transport
  /// error or a non-object body (wire contract drift).
  Future<List<StatsTarget>> fetchTargets();

  /// Absolute URL of the AstroBin acquisition CSV for [target], for opening in a
  /// browser / save dialog.
  String astrobinExportUrl(String target);

  void close();
}

/// Dio wrapper over `/api/v1/stats/*`.
class StatsExportApi implements StatsExportClient {
  final Dio _dio;
  final String _baseUrl;

  /// [dio] is injectable so tests can supply a mock `HttpClientAdapter`;
  /// production passes nothing and a server-bound Dio is built.
  StatsExportApi(AraServer server, {Dio? dio})
      : _baseUrl = server.baseUrl,
        _dio = dio ??
            Dio(BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 10),
            ));

  @override
  Future<List<StatsTarget>> fetchTargets() async {
    final res = await _dio.get<dynamic>('/api/v1/stats/targets');
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw FormatException('stats/targets returned a non-object body (${data.runtimeType})');
    }
    final list = data['targets'];
    if (list is! List) {
      throw const FormatException('stats/targets body has no targets array');
    }
    return list
        .whereType<Map<String, dynamic>>()
        .map(StatsTarget.fromJson)
        // A row with no name can't be exported (the endpoint keys on target), so drop it.
        .where((t) => t.targetName.isNotEmpty)
        .toList(growable: false);
  }

  @override
  String astrobinExportUrl(String target) =>
      // Resolve the export path against the base URL via Uri so an origin/port
      // (and any base path) is handled correctly rather than string-spliced; the
      // target is percent-encoded as a query value.
      Uri.parse(_baseUrl).replace(
        path: '/api/v1/stats/export/astrobin',
        queryParameters: <String, String>{'target': target},
      ).toString();

  @override
  void close() => _dio.close(force: true);
}
