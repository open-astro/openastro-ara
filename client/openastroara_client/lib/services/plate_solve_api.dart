import 'package:dio/dio.dart';

import '../models/plate_solve_result.dart';
import '../models/server.dart';
import '../models/verify_position_result.dart';
import 'refusal.dart';

/// Client-side wrapper around §18.I `POST /api/v1/platesolve/frames/{id}/solve`.
/// Asks the daemon to plate-solve a catalogued frame with the configured solver
/// backend (ASTAP) and returns the astrometric solution.
class PlateSolveApi {
  final Dio _dio;

  /// [dio] is injectable so tests can supply a stub adapter (#1150); production
  /// passes nothing and a server-bound Dio is built.
  PlateSolveApi(AraServer server, {Dio? dio})
    : _dio =
          dio ??
          Dio(
            BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              // Solving shells out to ASTAP on a full frame — give it room.
              receiveTimeout: const Duration(seconds: 120),
            ),
          );

  /// Solve the frame [id]. Returns the solution (its [PlateSolveResult.success]
  /// reflects whether a solution was found). Throws a [PlateSolveException] on a
  /// frame-not-found (404), a [Refusal] when the rig declines (422: optics or
  /// solver not set), and rethrows other transport failures.
  Future<PlateSolveResult> solve(String id) async {
    try {
      final res = await _dio.post<Map<String, dynamic>>(
        '/api/v1/platesolve/frames/$id/solve',
      );
      return PlateSolveResult.fromJson(res.data ?? const <String, dynamic>{});
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) {
        throw const PlateSolveException(
          'Frame not found in the catalog. Capture a frame first.',
        );
      }
      final refusal = Refusal.fromDio(e);
      if (refusal != null) throw refusal;
      rethrow;
    }
  }

  /// §57.4 — Verify-Position after a Stop Mount: the daemon takes one quick
  /// snapshot ([exposureSeconds], default 2 s) and solves it. A 409 or 422
  /// (no camera, camera busy, optics or solver not set) throws a [Refusal].
  Future<VerifyPositionResult> verifyPosition({double? exposureSeconds}) async {
    try {
      final res = await _dio.post<Map<String, dynamic>>(
        '/api/v1/platesolve/verify-position',
        data: {'exposure_seconds': ?exposureSeconds},
      );
      return VerifyPositionResult.fromJson(
        res.data ?? const <String, dynamic>{},
      );
    } on DioException catch (e) {
      final refusal = Refusal.fromDio(e);
      if (refusal != null) throw refusal;
      rethrow;
    }
  }
}

/// A plate-solve failure with a user-readable message (404 frame-missing).
class PlateSolveException implements Exception {
  final String message;
  const PlateSolveException(this.message);

  @override
  String toString() => message;
}
