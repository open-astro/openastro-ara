import 'dart:typed_data';

import 'package:dio/dio.dart';

import '../models/polar_align.dart';
import '../models/server.dart';
import 'refusal.dart';

/// The §45 polar-align operations the state layer depends on. An interface so
/// tests supply a pure fake; [PolarAlignApi] is the Dio-backed implementation.
abstract interface class PolarAlignClient {
  Future<PolarAlignStatus?> getStatus();

  /// Begin a run from the mount's Home position: the three-point measurement,
  /// then the live adjust loop.
  Future<void> start();

  /// Stop the run where it is, at any time (the mount going Home included).
  Future<void> stop();

  /// Done: the rig stops the adjust loop, sends the mount Home, verifies the
  /// alignment and sends the mount Home again, ending in `checked`.
  Future<void> complete();
  Future<void> requestCapture();
  Future<Uint8List?> getLiveFrame();
  void close();
}

/// Client wrapper around the §45 surface: the routine
/// (`/api/v1/equipment/polaralign/{status,start,stop,complete}` — the POSTs are
/// 202-Accepted; live progress arrives on the `polar_align.*` WS stream). The
/// §45.12 profile section (`/api/v1/profile/polar-align`) is a profile section
/// like the others: `ProfileApi` and `polarAlignSettingsProvider` (ADR 0015).
class PolarAlignApi implements PolarAlignClient {
  final Dio _dio;

  PolarAlignApi(AraServer server, {Dio? dio})
    : _dio =
          dio ??
          Dio(
            BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 5),
            ),
          );

  /// Current routine snapshot, or `null` on a `404` (a server predating §45).
  /// Other HTTP failures throw `DioException` for the caller to surface.
  @override
  Future<PolarAlignStatus?> getStatus() async {
    try {
      final res = await _dio.get<dynamic>('/api/v1/equipment/polaralign/status');
      final data = res.data;
      if (data is! Map<String, dynamic>) return null;
      return PolarAlignStatus.fromJson(data);
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) return null;
      rethrow;
    }
  }

  /// Begin a run. No body. 202-Accepted; a start the rig refuses surfaces as
  /// a 409/422, and one with a declared cause (a parked mount, a mount not at
  /// Home, an unverified mount clock or position) is a [Refusal].
  @override
  Future<void> start() async {
    try {
      await _dio.post<void>('/api/v1/equipment/polaralign/start');
    } on DioException catch (e) {
      final refusal = Refusal.fromDio(e);
      if (refusal != null) throw refusal;
      rethrow;
    }
  }

  /// Stop: the mount stops where it is, also while it goes Home (#2048: the
  /// daemon confirms the stop before it answers). 202-Accepted.
  @override
  Future<void> stop() async {
    await _dio.post<void>(
      '/api/v1/equipment/polaralign/stop',
      options: Options(receiveTimeout: endTimeout),
    );
  }

  /// Done. 202-Accepted at once; the rest of the run (Home, verify, Home)
  /// arrives on the `polar_align.*` WS stream and needs no further call.
  @override
  Future<void> complete() async {
    await _dio.post<void>('/api/v1/equipment/polaralign/complete');
  }

  /// #2048 — Stop answers once the daemon has ended the run and
  /// confirmed the mount stopped. Its slowest end (`EndWorstCase` in
  /// PolarAlignService.MountStop.cs) is a stuck run: one round of stop
  /// commands (3 × 3 s), the 15 s unwind grace, then the stop it confirms
  /// itself (8 s window, plus the poll, two 3 s reads and one more 9 s round):
  /// 47.25 s. The margin covers tracking off, the session row and the camera
  /// release, so the app never reports a failed Stop while the daemon is still
  /// stopping the mount. PolarAlignServiceTest reads this value and checks it
  /// against the daemon's bound.
  static const endTimeout = Duration(seconds: 60);

  /// Single-frame mode: take the next adjust frame. 202-Accepted; a 409
  /// outside the adjust phase.
  @override
  Future<void> requestCapture() async {
    await _dio.post<void>('/api/v1/equipment/polaralign/capture');
  }

  /// The guide camera's latest frame as JPEG bytes (the live view), or
  /// `null` when the server has none to serve (`404`).
  @override
  Future<Uint8List?> getLiveFrame() async {
    try {
      final res = await _dio.get<List<int>>(
        '/api/v1/equipment/polaralign/frame.jpg',
        options: Options(responseType: ResponseType.bytes),
      );
      final data = res.data;
      return data == null ? null : Uint8List.fromList(data);
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) return null;
      rethrow;
    }
  }

  /// Releases the underlying Dio's connection pool. Call when the API is
  /// replaced (e.g. the active server changed) so sockets don't leak.
  @override
  void close() => _dio.close(force: true);
}

/// #1971 the rig's Polar Aligner: the device (`/api/v1/equipment/polaraligner`,
/// its stop and home) and its motors in a running routine
/// (`/api/v1/equipment/polaralign/correction/{stop,resume}`). The commands are
/// 202-Accepted; a refusal is a 409 whose detail is a sentence for people.
abstract interface class PolarAlignerClient {
  /// Null on a 404: a daemon that predates the Polar Aligner.
  Future<PolarAligner?> get();
  Future<void> stop();
  Future<void> home();

  /// Stop the motors; the routine carries on by hand.
  Future<void> stopInRoutine();

  /// After a stop: correct with the motors again, from a new test move.
  Future<void> resumeInRoutine();
  void close();
}

class PolarAlignerApi implements PolarAlignerClient {
  final Dio _dio;

  PolarAlignerApi(AraServer server, {Dio? dio})
    : _dio =
          dio ??
          Dio(
            BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 5),
            ),
          );

  @override
  Future<PolarAligner?> get() async {
    try {
      final res = await _dio.get<dynamic>('/api/v1/equipment/polaraligner');
      final data = res.data;
      return data is Map<String, dynamic> ? PolarAligner.fromJson(data) : null;
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) return null;
      rethrow;
    }
  }

  @override
  Future<void> stop() => _dio.post<void>('/api/v1/equipment/polaraligner/stop');

  @override
  Future<void> home() => _dio.post<void>('/api/v1/equipment/polaraligner/home');

  @override
  Future<void> stopInRoutine() => _dio.post<void>('/api/v1/equipment/polaralign/correction/stop');

  @override
  Future<void> resumeInRoutine() => _dio.post<void>('/api/v1/equipment/polaralign/correction/resume');

  @override
  void close() => _dio.close(force: true);
}
