import 'dart:typed_data';

import 'package:dio/dio.dart';

import '../models/bahtinov_focus.dart';
import '../models/server.dart';
import 'guide_focus_api.dart' show GuideFocusFrame;

/// The Bahtinov readout's client surface (#1299). An interface so the notifier
/// can be unit-tested with a fake.
abstract interface class BahtinovFocusClient {
  Future<void> start({required double exposureSec});
  Future<void> stop();
  Future<BahtinovFocusStatus> status();
  /// The latest star crop (or whole frame); [GuideFocusFrame] carries the
  /// JPEG and its `X-Frame-Seq`.
  Future<GuideFocusFrame?> fetchFrame();
  void close();
}

/// Dio-backed client for `/api/v1/bahtinov-focus/*`.
class BahtinovFocusApi implements BahtinovFocusClient {
  final Dio _dio;

  BahtinovFocusApi(AraServer server)
      : _dio = Dio(BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          // Stop drains the in-flight frame (≤ exposure + 30 s on the daemon).
          receiveTimeout: const Duration(seconds: 65),
        ));

  @override
  Future<void> start({required double exposureSec}) async {
    await _dio.post<void>(
      '/api/v1/bahtinov-focus/start',
      data: <String, dynamic>{'exposure_sec': exposureSec},
    );
  }

  @override
  Future<void> stop() async {
    await _dio.post<void>('/api/v1/bahtinov-focus/stop');
  }

  @override
  Future<BahtinovFocusStatus> status() async {
    final res = await _dio.get<dynamic>('/api/v1/bahtinov-focus/state');
    final data = res.data;
    return data is Map<String, dynamic>
        ? BahtinovFocusStatus.fromJson(data)
        : BahtinovFocusStatus.idle;
  }

  @override
  Future<GuideFocusFrame?> fetchFrame() async {
    final res = await _dio.get<List<int>>(
      '/api/v1/bahtinov-focus/frame',
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
    return GuideFocusFrame(u8, seq);
  }

  @override
  void close() => _dio.close(force: true);
}
