import 'package:dio/dio.dart';

import '../models/guiding_autotune.dart';
import '../models/server.dart';

abstract interface class GuidingAutoTuneClient {
  Future<GuidingAutoTuneCapabilities> getCapabilities();
  Future<GuidingAutoTuneStatus> getLatest();
  Future<GuidingAutoTuneStatus> getSession(String sessionId);
  Future<GuidingAutoTuneReport> getReport();
  Future<GuidingAutoTuneReport> getSessionReport(String sessionId);
  Future<GuidingAutoTuneStatus> start({
    String depth,
    bool dryRun,
    bool useMainCameraValidation,
  });
  Future<GuidingAutoTuneStatus> cancel();
  Future<GuidingAutoTuneStatus> cancelSession(String sessionId);
  Future<GuidingAutoTuneStatus> apply();
  Future<GuidingAutoTuneStatus> applySession(String sessionId);
  Future<GuidingAutoTuneStatus> rollback();
  Future<GuidingAutoTuneStatus> rollbackSession(String sessionId);
  void close();
}

class GuidingAutoTuneApi implements GuidingAutoTuneClient {
  final Dio _dio;

  GuidingAutoTuneApi(AraServer server, {Dio? dio})
      : _dio = dio ?? Dio(BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          receiveTimeout: const Duration(seconds: 10),
        ));

  @override
  Future<GuidingAutoTuneCapabilities> getCapabilities() async {
    final response = await _dio.get<dynamic>('/api/v1/guiding/autotune/capabilities');
    return GuidingAutoTuneCapabilities.fromJson(
        (response.data as Map).cast<String, dynamic>());
  }

  @override
  Future<GuidingAutoTuneStatus> getLatest() async {
    final response = await _dio.get<dynamic>('/api/v1/guiding/autotune/sessions/latest');
    return GuidingAutoTuneStatus.fromJson(
        (response.data as Map).cast<String, dynamic>());
  }

  @override
  Future<GuidingAutoTuneStatus> getSession(String sessionId) async {
    final response = await _dio.get<dynamic>(
        '/api/v1/guiding/autotune/sessions/$sessionId');
    return GuidingAutoTuneStatus.fromJson(
        (response.data as Map).cast<String, dynamic>());
  }

  @override
  Future<GuidingAutoTuneReport> getReport() async {
    final response = await _dio.get<dynamic>('/api/v1/guiding/autotune/sessions/latest/report');
    return GuidingAutoTuneReport.fromJson(
        (response.data as Map).cast<String, dynamic>());
  }

  @override
  Future<GuidingAutoTuneReport> getSessionReport(String sessionId) async {
    final response = await _dio.get<dynamic>(
        '/api/v1/guiding/autotune/sessions/$sessionId/report');
    return GuidingAutoTuneReport.fromJson(
        (response.data as Map).cast<String, dynamic>());
  }

  @override
  Future<GuidingAutoTuneStatus> start({
    String depth = 'standard',
    bool dryRun = true,
    bool useMainCameraValidation = false,
  }) async {
    final response = await _dio.post<dynamic>(
      '/api/v1/guiding/autotune/sessions',
      data: <String, dynamic>{
        'depth': depth,
        'dry_run': dryRun,
        'use_main_camera_validation': useMainCameraValidation,
      },
    );
    return GuidingAutoTuneStatus.fromJson(
        (response.data as Map).cast<String, dynamic>());
  }

  Future<GuidingAutoTuneStatus> _post(String path) async {
    final response = await _dio.post<dynamic>(path);
    return GuidingAutoTuneStatus.fromJson(
        (response.data as Map).cast<String, dynamic>());
  }

  @override
  Future<GuidingAutoTuneStatus> cancel() =>
      _post('/api/v1/guiding/autotune/sessions/latest/cancel');

  @override
  Future<GuidingAutoTuneStatus> cancelSession(String sessionId) =>
      _post('/api/v1/guiding/autotune/sessions/$sessionId/cancel');

  @override
  Future<GuidingAutoTuneStatus> apply() =>
      _post('/api/v1/guiding/autotune/sessions/latest/apply');

  @override
  Future<GuidingAutoTuneStatus> applySession(String sessionId) =>
      _post('/api/v1/guiding/autotune/sessions/$sessionId/apply');

  @override
  Future<GuidingAutoTuneStatus> rollback() =>
      _post('/api/v1/guiding/autotune/sessions/latest/rollback');

  @override
  Future<GuidingAutoTuneStatus> rollbackSession(String sessionId) =>
      _post('/api/v1/guiding/autotune/sessions/$sessionId/rollback');

  @override
  void close() => _dio.close(force: true);
}
