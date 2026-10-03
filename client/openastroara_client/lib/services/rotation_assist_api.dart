import 'package:dio/dio.dart';

import '../models/rotation_assist.dart';
import '../models/server.dart';

/// The by-hand rotation readout's client surface. An interface so the
/// notifier can be unit-tested with a fake.
abstract interface class RotationAssistClient {
  Future<void> start({required double positionAngleDeg});
  Future<void> stop();
  Future<RotationAssistStatus> status();
  void close();
}

/// Dio-backed client for `/api/v1/rotation-assist/*`.
class RotationAssistApi implements RotationAssistClient {
  final Dio _dio;

  RotationAssistApi(AraServer server)
    : _dio = Dio(
        BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          // Stop drains the in-flight capture + solve.
          receiveTimeout: const Duration(seconds: 95),
        ),
      );

  @override
  Future<void> start({required double positionAngleDeg}) async {
    await _dio.post<void>(
      '/api/v1/rotation-assist/start',
      data: <String, dynamic>{'position_angle_deg': positionAngleDeg},
    );
  }

  @override
  Future<void> stop() async {
    await _dio.post<void>('/api/v1/rotation-assist/stop');
  }

  @override
  Future<RotationAssistStatus> status() async {
    final res = await _dio.get<dynamic>('/api/v1/rotation-assist/state');
    final data = res.data;
    return data is Map<String, dynamic>
        ? RotationAssistStatus.fromJson(data)
        : RotationAssistStatus.idle;
  }

  @override
  void close() => _dio.close(force: true);
}
