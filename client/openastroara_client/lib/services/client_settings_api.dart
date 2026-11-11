import 'package:dio/dio.dart';

import '../models/client_settings.dart';
import '../models/server.dart';

/// The §55.1 settings-sync operations the state layer depends on. An interface
/// so tests can supply a pure fake (no Dio); [ClientSettingsApi] is the
/// Dio-backed production implementation.
abstract interface class ClientSettingsClient {
  /// The active profile's stored UI-preferences blob (empty when none saved).
  /// Throws on a transport error or a non-object body (wire contract drift).
  Future<ClientSettings> fetch();

  /// Replace the stored preferences wholesale (last-write-wins). Returns the
  /// server's stored view (with its new timestamp). Throws on a 4xx (e.g. a
  /// non-object / oversized body) or a transport error.
  Future<ClientSettings> replace(Map<String, dynamic> settings);

  /// #1644 — replace only the top-level keys of [patch] on the rig (`PATCH`);
  /// a `null` value removes that key. Returns the stored view (the whole
  /// object, with its new timestamp). Throws on a 4xx — a daemon that
  /// predates the merge answers 404/405 ([isMergeUnsupported]).
  Future<ClientSettings> merge(Map<String, dynamic> patch);

  void close();
}

/// Dio wrapper over `/api/v1/client-settings`.
class ClientSettingsApi implements ClientSettingsClient {
  final Dio _dio;

  /// [dio] is injectable so tests can supply a mock `HttpClientAdapter`;
  /// production passes nothing and a server-bound Dio is built.
  ClientSettingsApi(AraServer server, {Dio? dio})
      : _dio = dio ??
            Dio(BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 10),
            ));

  @override
  Future<ClientSettings> fetch() async {
    final res = await _dio.get<dynamic>('/api/v1/client-settings');
    return _parse(res.data);
  }

  @override
  Future<ClientSettings> replace(Map<String, dynamic> settings) async {
    final res = await _dio.put<dynamic>(
      '/api/v1/client-settings',
      data: <String, dynamic>{'settings': settings},
    );
    return _parse(res.data);
  }

  @override
  Future<ClientSettings> merge(Map<String, dynamic> patch) async {
    final res = await _dio.patch<dynamic>(
      '/api/v1/client-settings',
      data: <String, dynamic>{'settings': patch},
    );
    return _parse(res.data);
  }

  static ClientSettings _parse(dynamic data) {
    if (data is! Map<String, dynamic>) {
      throw FormatException('client-settings returned a non-object body (${data.runtimeType})');
    }
    return ClientSettings.fromJson(data);
  }

  @override
  void close() => _dio.close(force: true);
}

/// True for the 404/405 an older daemon answers to the merge: preferences then
/// stay on this device, with nothing to show.
bool isMergeUnsupported(Object error) =>
    error is DioException &&
    (error.response?.statusCode == 404 || error.response?.statusCode == 405);
