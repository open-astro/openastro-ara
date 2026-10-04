import 'dart:typed_data';

import 'package:dio/dio.dart';

import '../models/rotation_assist.dart';
import '../models/server.dart';

/// The by-hand rotation readout's client surface. An interface so the
/// notifier can be unit-tested with a fake.
/// A fetched frame: JPEG bytes + the server's `X-Frame-Seq`.
class RotationAssistFrame {
  final Uint8List bytes;
  final int seq;
  const RotationAssistFrame(this.bytes, this.seq);
}

abstract interface class RotationAssistClient {
  Future<void> start({required double positionAngleDeg});
  Future<void> stop();
  Future<RotationAssistStatus> status();
  Future<RotationAssistFrame?> fetchFrame();
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
  Future<RotationAssistFrame?> fetchFrame() async {
    final res = await _dio.get<List<int>>(
      '/api/v1/rotation-assist/frame',
      options: Options(
        responseType: ResponseType.bytes,
        validateStatus: (s) => s == 200 || s == 204,
      ),
    );
    if (res.statusCode == 204) return null;
    final bytes = res.data;
    if (bytes == null || bytes.isEmpty) return null;
    final u8 = bytes is Uint8List ? bytes : Uint8List.fromList(bytes);
    final seq = int.tryParse(res.headers.value('x-frame-seq') ?? '') ?? 0;
    return RotationAssistFrame(u8, seq);
  }

  @override
  void close() => _dio.close(force: true);
}
