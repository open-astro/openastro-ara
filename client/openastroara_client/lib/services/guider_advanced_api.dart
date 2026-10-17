import 'package:dio/dio.dart';

import '../models/guider_advanced_settings.dart';
import '../models/server.dart';

/// #1364 item 3 — the guider-advanced profile block and the guider's current
/// values. An interface so the dialog can be tested with a fake.
abstract interface class GuiderAdvancedClient {
  Future<GuiderAdvancedSettings> getProfile();
  Future<GuiderAdvancedSettings> putProfile(GuiderAdvancedSettings settings);
  Future<GuiderAdvancedView> getGuider();

  /// The runtime-safe push (`?scope=tuning`): applies while guiding.
  Future<void> pushTuning();
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
  Future<GuiderAdvancedSettings> getProfile() async {
    final res = await _dio.get<dynamic>('/api/v1/profile/guider-advanced');
    final data = res.data;
    return data is Map<String, dynamic>
        ? GuiderAdvancedSettings.fromJson(data)
        : GuiderAdvancedSettings.empty;
  }

  @override
  Future<GuiderAdvancedSettings> putProfile(
    GuiderAdvancedSettings settings,
  ) async {
    final res = await _dio.put<dynamic>(
      '/api/v1/profile/guider-advanced',
      data: settings.toJson(),
    );
    final data = res.data;
    return data is Map<String, dynamic>
        ? GuiderAdvancedSettings.fromJson(data)
        : settings;
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
  void close() => _dio.close(force: true);
}
