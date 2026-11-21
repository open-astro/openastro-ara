import 'package:dio/dio.dart';

import '../models/guider_advanced_settings.dart';
import '../models/server.dart';

/// #1364 item 3 — the guider-advanced profile block and the guider's current
/// values. An interface so the dialog can be tested with a fake.
///
/// ADR 0015 — the block is read with the rig's `ETag` and written back with
/// the loaded one as `If-Match`, so a copy another device saved since is
/// refused (412) instead of written over.
abstract interface class GuiderAdvancedClient {
  Future<({GuiderAdvancedSettings value, String? etag})> getProfile();
  Future<({GuiderAdvancedSettings value, String? etag})> putProfile(
    GuiderAdvancedSettings settings, {
    String? ifMatch,
  });
  Future<GuiderAdvancedView> getGuider();

  /// The runtime-safe push (`?scope=tuning`): applies while guiding.
  Future<void> pushTuning();

  /// #1592 — write the profile's multi-star setting to the connected guider.
  /// The daemon answers 409 "stop guiding first" while a guide session runs.
  Future<void> applyMultiStar();
  void close();
}

class GuiderAdvancedApi implements GuiderAdvancedClient {
  final Dio _dio;

  GuiderAdvancedApi(AraServer server)
    : _dio = Dio(
        BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          receiveTimeout: const Duration(seconds: 20),
        ),
      );

  @override
  Future<({GuiderAdvancedSettings value, String? etag})> getProfile() async {
    final res = await _dio.get<dynamic>('/api/v1/profile/guider-advanced');
    final data = res.data;
    return (
      value: data is Map<String, dynamic>
          ? GuiderAdvancedSettings.fromJson(data)
          : GuiderAdvancedSettings.empty,
      etag: res.headers.value('etag'),
    );
  }

  @override
  Future<({GuiderAdvancedSettings value, String? etag})> putProfile(
    GuiderAdvancedSettings settings, {
    String? ifMatch,
  }) async {
    final res = await _dio.put<dynamic>(
      '/api/v1/profile/guider-advanced',
      data: settings.toJson(),
      options: Options(headers: {'If-Match': ?ifMatch}),
    );
    final data = res.data;
    return (
      value: data is Map<String, dynamic>
          ? GuiderAdvancedSettings.fromJson(data)
          : settings,
      etag: res.headers.value('etag'),
    );
  }

  @override
  Future<GuiderAdvancedView> getGuider() async {
    final res = await _dio.get<dynamic>('/api/v1/equipment/guider/advanced');
    final data = res.data;
    return data is Map<String, dynamic>
        ? GuiderAdvancedView.fromJson(data)
        : GuiderAdvancedView.disconnected;
  }

  @override
  Future<void> pushTuning() => _dio.post<void>(
    '/api/v1/equipment/guider/profile/push',
    queryParameters: const {'scope': 'tuning'},
  );

  @override
  Future<void> applyMultiStar() => _dio.post<void>(
    '/api/v1/equipment/guider/multi-star/apply',
    // The guider's equipment is released and reconnected inside this request.
    options: Options(receiveTimeout: const Duration(seconds: 60)),
  );

  @override
  void close() => _dio.close(force: true);
}
