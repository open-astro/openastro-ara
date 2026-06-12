import 'package:dio/dio.dart';

import '../models/plate_solve_result.dart';
import '../models/server.dart';

/// Client-side wrapper around §18.I `POST /api/v1/platesolve/frames/{id}/solve`.
/// Asks the daemon to plate-solve a catalogued frame with the configured solver
/// backend (ASTAP) and returns the astrometric solution.
class PlateSolveApi {
  final Dio _dio;

  PlateSolveApi(AraServer server)
      : _dio = Dio(BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          // Solving shells out to ASTAP on a full frame — give it room.
          receiveTimeout: const Duration(seconds: 120),
        ));

  /// Solve the frame [id]. Returns the solution (its [PlateSolveResult.success]
  /// reflects whether a solution was found). Throws a [PlateSolveException] with
  /// a readable message on a frame-not-found (404) or a solver-misconfiguration
  /// (422), and rethrows other transport failures.
  Future<PlateSolveResult> solve(String id) async {
    try {
      final res = await _dio.post<Map<String, dynamic>>(
        '/api/v1/platesolve/frames/$id/solve',
      );
      return PlateSolveResult.fromJson(res.data ?? const <String, dynamic>{});
    } on DioException catch (e) {
      final code = e.response?.statusCode;
      if (code == 404) {
        throw const PlateSolveException(
            'Frame not found in the catalog. Capture a frame first.');
      }
      if (code == 422) {
        // The daemon returns an RFC 7807 Problem whose detail explains the
        // misconfiguration (e.g. focal length / pixel size not set, or ASTAP
        // not installed). Surface that message rather than a bare 422.
        final detail = _problemDetail(e.response?.data);
        throw PlateSolveException(detail ??
            'The plate solver is not configured (check focal length, pixel '
                'size, and the ASTAP path in settings).');
      }
      rethrow;
    }
  }

  static String? _problemDetail(Object? body) {
    if (body is Map<String, dynamic>) {
      final detail = body['detail'] ?? body['title'];
      if (detail is String && detail.isNotEmpty) return detail;
    }
    return null;
  }
}

/// A plate-solve failure with a user-readable message (404 frame-missing or
/// 422 solver-misconfiguration).
class PlateSolveException implements Exception {
  final String message;
  const PlateSolveException(this.message);

  @override
  String toString() => message;
}
