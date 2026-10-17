import 'dart:typed_data';

import 'package:dio/dio.dart';

import '../models/guider_calibration_view.dart';
import '../models/guider_live.dart';
import '../models/server.dart';

/// #1364 — the live guide-camera view's client surface. An interface so the
/// notifier can be unit-tested with a fake.
abstract interface class GuiderLiveClient {
  Future<GuiderLive> getLive();

  /// The guider's latest frame as JPEG, or null while it has none (204).
  Future<Uint8List?> getFrame();
  Future<void> loop();
  Future<void> stop();

  /// Lock on the star nearest (x, y) guide pixels.
  Future<void> selectStar(double x, double y);
  Future<void> deselectStar();

  /// #1364 item 5 — the guider's recent guide steps, oldest first: each in
  /// the `guider.step` payload shape plus `age_sec` (seconds before now,
  /// measured on the guider's clock). Empty when it has none.
  Future<List<Map<String, dynamic>>> getHistory();

  /// This session's calibration run, the stored result and Ara's verdict.
  Future<GuiderCalibrationView> getCalibration();

  /// Drop the stored calibration; the next guide start calibrates again.
  Future<void> clearCalibration();
  void close();
}

/// Dio-backed client for `/api/v1/equipment/guider/live*`.
class GuiderLiveApi implements GuiderLiveClient {
  final Dio _dio;

  GuiderLiveApi(AraServer server)
    : _dio = Dio(
        BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          receiveTimeout: const Duration(seconds: 10),
        ),
      );

  @override
  Future<GuiderLive> getLive() async {
    final res = await _dio.get<dynamic>('/api/v1/equipment/guider/live');
    final data = res.data;
    return data is Map<String, dynamic>
        ? GuiderLive.fromJson(data)
        : GuiderLive.disconnected;
  }

  @override
  Future<Uint8List?> getFrame() async {
    final res = await _dio.get<List<int>>(
      '/api/v1/equipment/guider/live/frame',
      options: Options(
        responseType: ResponseType.bytes,
        validateStatus: (s) => s == 200 || s == 204,
      ),
    );
    if (res.statusCode == 204) return null;
    final bytes = res.data;
    if (bytes == null || bytes.isEmpty) return null;
    return bytes is Uint8List ? bytes : Uint8List.fromList(bytes);
  }

  @override
  Future<void> loop() => _dio.post<void>('/api/v1/equipment/guider/live/loop');

  @override
  Future<void> stop() => _dio.post<void>('/api/v1/equipment/guider/live/stop');

  @override
  Future<void> selectStar(double x, double y) => _dio.post<void>(
    '/api/v1/equipment/guider/live/select',
    data: <String, dynamic>{'x': x, 'y': y},
  );

  @override
  Future<void> deselectStar() =>
      _dio.post<void>('/api/v1/equipment/guider/live/deselect');

  @override
  Future<List<Map<String, dynamic>>> getHistory() async {
    final res = await _dio.get<dynamic>('/api/v1/equipment/guider/history');
    final data = res.data;
    final steps = data is Map<String, dynamic> ? data['steps'] : null;
    return [
      if (steps is List)
        for (final s in steps)
          if (s is Map<String, dynamic>) s,
    ];
  }

  @override
  Future<GuiderCalibrationView> getCalibration() async {
    final res = await _dio.get<dynamic>(
      '/api/v1/equipment/guider/live/calibration',
    );
    final data = res.data;
    return data is Map<String, dynamic>
        ? GuiderCalibrationView.fromJson(data)
        : GuiderCalibrationView.disconnected;
  }

  @override
  Future<void> clearCalibration() =>
      _dio.post<void>('/api/v1/equipment/guider/live/calibration/clear');

  @override
  void close() => _dio.close(force: true);
}
