import 'dart:typed_data';

import 'package:dio/dio.dart';

import '../models/server.dart';

/// One live frame: the JPEG bytes plus the server's sequence + session ids
/// (from the `X-Frame-Seq` / `X-Live-Session` headers). A changed [session]
/// means a new Live View session (seq restarts at 1), so a poller treats it as
/// new rather than a backward seq.
class LiveFrame {
  final Uint8List bytes;
  final int seq;
  final int session;
  const LiveFrame(this.bytes, this.seq, this.session);
}

/// The §64 Live View client surface (start/stop/poll). An interface so the
/// frame notifier can be unit-tested with a fake instead of real HTTP.
abstract interface class LiveViewClient {
  Future<void> start({required double exposureSec, int? gain, int binX, int binY});
  Future<void> stop();
  Future<LiveFrame?> fetchFrame();
  void close();
}

/// Client wrapper for the §64 Live View daemon endpoints under
/// `/api/v1/equipment/camera/liveview`. Drives start/stop and polls the latest
/// rendered frame (ephemeral JPEG — never cataloged).
class LiveViewApi implements LiveViewClient {
  final Dio _dio;

  LiveViewApi(AraServer server)
      : _dio = Dio(BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          // Stop awaits the server loop draining (the daemon caps this at the
          // 15 s exposure + readout for a non-cancellable ImageArray download),
          // so allow a generous receive window.
          receiveTimeout: const Duration(seconds: 30),
        ));

  /// Start the loop. ExposureSec is clamped to the daemon's (0, 15] cap.
  @override
  Future<void> start({
    required double exposureSec,
    int? gain,
    int binX = 2,
    int binY = 2,
  }) async {
    await _dio.post<void>(
      '/api/v1/equipment/camera/liveview/start',
      data: <String, dynamic>{
        'exposure_sec': exposureSec.clamp(0.001, 15.0),
        'gain': gain,
        'bin_x': binX,
        'bin_y': binY,
      },
    );
  }

  /// Stop the loop. The daemon returns 204 only after the loop fully drains.
  @override
  Future<void> stop() async {
    await _dio.post<void>('/api/v1/equipment/camera/liveview/stop');
  }

  /// Fetch the latest frame, or `null` when none is available yet (204).
  @override
  Future<LiveFrame?> fetchFrame() async {
    final res = await _dio.get<List<int>>(
      '/api/v1/equipment/camera/liveview/frame',
      options: Options(
        responseType: ResponseType.bytes,
        validateStatus: (s) => s == 200 || s == 204,
      ),
    );
    if (res.statusCode == 204) return null;
    final bytes = res.data;
    if (bytes == null || bytes.isEmpty) return null;
    // ResponseType.bytes already materialises a Uint8List — avoid re-copying the
    // multi-KB JPEG on every poll tick (constant GC pressure at ~4 Hz).
    final u8 = bytes is Uint8List ? bytes : Uint8List.fromList(bytes);
    final seq = int.tryParse(res.headers.value('x-frame-seq') ?? '') ?? 0;
    final session = int.tryParse(res.headers.value('x-live-session') ?? '') ?? 0;
    return LiveFrame(u8, seq, session);
  }

  @override
  void close() => _dio.close(force: true);
}
