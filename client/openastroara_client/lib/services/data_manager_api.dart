import 'package:dio/dio.dart';

import '../models/catalog_object.dart';
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

  /// Delete an installed package's files. Returns true if files were freed, false
  /// if there was nothing to delete (the package wasn't present / already gone) —
  /// false is NOT a failure (a real error throws). Idempotent.
  Future<bool> delete(String packageId);

  /// Read an installed catalog package's objects (normalized {name, ra°, dec°,
  /// mag}). Returns null when the package isn't a known catalog, isn't
  /// installed, or has no catalog file (the daemon answers 404) — null is "no
  /// overlay", not an error. [maxMag] drops fainter objects; [limit] caps the
  /// count (the daemon also hard-caps at 50k).
  ///
  /// Currently unconsumed by design, NOT dead code: its Aladin-era consumer
  /// was pruned with AladinView, and this method + [CatalogObject] are the
  /// retained wire contract for the §36 Catalogs-overlay panel against the
  /// Stellarium planetarium (see design/PORT_TODO.md "OBSOLETE ... do NOT
  /// remove SkyCatalogService" entry before sweeping it).
  Future<List<CatalogObject>?> getCatalog(String packageId, {double? maxMag, int? limit});

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
      // A 2xx with a non-array body means the wire contract changed (or an error
      // envelope slipped through). Throw rather than return [] — the AsyncNotifier
      // surfaces this as an error state the UI can show, instead of a silently-empty
      // catalog. (Dio already throws on 4xx/5xx; this is the 200-wrong-shape case.)
      throw FormatException('data-manager/packages returned a non-array body (${data.runtimeType})');
    }
    return data
        .whereType<Map<String, dynamic>>()
        .map(DataPackage.fromJson)
        // Drop an id-less (malformed) entry — id is a URL path segment for delete
        // and the key the daemon downloads by, so an empty one is unusable.
        .where((p) => p.id.isNotEmpty)
        .toList(growable: false);
  }

  @override
  Future<String> download(String packageId, {bool forceReinstall = false}) async {
    assert(packageId.isNotEmpty, 'packageId must not be empty');
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
    assert(downloadId.isNotEmpty, 'downloadId must not be empty');
    try {
      await _dio.post<void>('/api/v1/data-manager/cancel/${Uri.encodeComponent(downloadId)}');
    } on DioException catch (e) {
      // 404 → the download already finished/was removed between the user tapping
      // Cancel and the request landing. Nothing to cancel — a no-op, not an error.
      if (e.response?.statusCode == 404) return;
      rethrow;
    }
  }

  @override
  Future<bool> delete(String packageId) async {
    assert(packageId.isNotEmpty, 'packageId must not be empty');
    try {
      // Route is `/api/v1/data-manager/{packageId}` — verified against the server:
      // `data.MapDelete("/{packageId}")` under `MapGroup("/api/v1/data-manager")`
      // (SystemEndpoints.cs). It is intentionally NOT `/packages/{id}`: `/packages`
      // is only the GET collection route; the delete item route sits at the group root.
      await _dio.delete<void>('/api/v1/data-manager/${Uri.encodeComponent(packageId)}');
      // Dio throws on any non-2xx, so reaching here means the delete succeeded (files freed).
      return true;
    } on DioException catch (e) {
      // 404 → already gone (another client removed it). Idempotent: report "nothing
      // freed" (false), not an error; anything else propagates.
      if (e.response?.statusCode == 404) return false;
      rethrow;
    }
  }

  @override
  Future<List<CatalogObject>?> getCatalog(String packageId, {double? maxMag, int? limit}) async {
    assert(packageId.isNotEmpty, 'packageId must not be empty');
    try {
      final res = await _dio.get<dynamic>(
        '/api/v1/data-manager/${Uri.encodeComponent(packageId)}/catalog',
        queryParameters: <String, dynamic>{
          'max_mag': ?maxMag,
          'limit': ?limit,
        },
      );
      final data = res.data;
      if (data is! List) {
        throw FormatException('catalog returned a non-array body (${data.runtimeType})');
      }
      return data
          .whereType<Map<String, dynamic>>()
          .map(CatalogObject.tryFromJson)
          .whereType<CatalogObject>() // drop positionless rows
          .toList(growable: false);
    } on DioException catch (e) {
      // 404 → not a known catalog / not installed / no catalog file. That's "no
      // overlay", not an error: return null so the caller just draws nothing.
      if (e.response?.statusCode == 404) return null;
      rethrow;
    }
  }

  @override
  void close() => _dio.close(force: true);
}
