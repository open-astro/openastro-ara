import 'package:dio/dio.dart';

import '../models/data_package.dart';
import '../models/server.dart';

/// The §36 Data Manager operations the state layer depends on. An interface so
/// tests can supply a pure fake (no Dio); [DataManagerApi] is the Dio-backed
/// production implementation.
abstract interface class DataManagerClient {
  /// The curated package catalog with on-disk install state.
  Future<List<DataPackage>> listPackages();

  /// Start a download. Returns the download id (the accepted operation's id),
  /// used to [cancel] it. A non-force request for an already-installed package
  /// throws (the daemon answers 409).
  Future<String> download(String packageId, {bool forceReinstall});

  /// Cancel an in-flight download by its id.
  Future<void> cancel(String downloadId);

  /// Delete an installed package's files. Returns true if something was freed.
  Future<bool> delete(String packageId);

  void close();
}

/// Dio wrapper over `/api/v1/data-manager/*`. Download/cancel are 202-Accepted
/// (the daemon runs the fetch+extract in the background and reports progress
/// over the `data_manager.download.*` WS stream), so they return when the
/// request is accepted, not when the download completes.
class DataManagerApi implements DataManagerClient {
  final Dio _dio;

  DataManagerApi(AraServer server)
      : _dio = Dio(BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          sendTimeout: const Duration(seconds: 5),
          receiveTimeout: const Duration(seconds: 12),
        ));

  @override
  Future<List<DataPackage>> listPackages() async {
    final res = await _dio.get<dynamic>('/api/v1/data-manager/packages');
    final data = res.data;
    if (data is! List) {
      return const <DataPackage>[];
    }
    return data
        .whereType<Map<String, dynamic>>()
        .map(DataPackage.fromJson)
        .toList(growable: false);
  }

  @override
  Future<String> download(String packageId, {bool forceReinstall = false}) async {
    final res = await _dio.post<dynamic>(
      '/api/v1/data-manager/download',
      data: <String, dynamic>{
        'package_id': packageId,
        'force_reinstall': forceReinstall,
      },
    );
    final data = res.data;
    final id = data is Map<String, dynamic> ? data['operation_id'] : null;
    if (id is! String) {
      throw const FormatException('download accepted but no operation_id was returned');
    }
    return id;
  }

  @override
  Future<void> cancel(String downloadId) async {
    await _dio.post<void>('/api/v1/data-manager/cancel/${Uri.encodeComponent(downloadId)}');
  }

  @override
  Future<bool> delete(String packageId) async {
    try {
      final res = await _dio.delete<void>('/api/v1/data-manager/${Uri.encodeComponent(packageId)}');
      // 204 No Content → freed. Any 2xx is success.
      final code = res.statusCode ?? 0;
      return code >= 200 && code < 300;
    } on DioException catch (e) {
      // 404 → already gone (another client removed it). Delete is idempotent, so
      // a missing package is a no-op success, not an error; anything else propagates.
      if (e.response?.statusCode == 404) return false;
      rethrow;
    }
  }

  @override
  void close() => _dio.close(force: true);
}
