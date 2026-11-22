import 'package:dio/dio.dart';

import '../models/pose_solve_check.dart';
import '../models/server.dart';
import 'refusal.dart';

/// #2173 — the plate-solve check of an unverified mount position. A 409 or 422
/// (no camera, camera busy, solver or optics not set, nothing to check, no Sync
/// on offer) throws a [Refusal].
abstract class PoseSolveCheckClient {
  Future<PoseSolveAvailability> availability();
  Future<PoseSolveCheckResult> check();
  Future<PoseSolveCheckResult> sync(String checkId);
  void close();
}

class PoseSolveCheckApi implements PoseSolveCheckClient {
  static const _path = '/api/v1/equipment/telescope/verify-position-by-solve';

  final Dio _dio;

  PoseSolveCheckApi(AraServer server, {Dio? dio})
    : _dio =
          dio ??
          Dio(
            BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              // An exposure and a solve (blind if the near one misses), twice
              // after a Sync.
              receiveTimeout: const Duration(minutes: 5),
            ),
          );

  @override
  Future<PoseSolveAvailability> availability() async {
    final res = await _dio.get<Map<String, dynamic>>(_path);
    final body = res.data;
    if (body == null) throw const FormatException('empty answer');
    return PoseSolveAvailability.fromJson(body);
  }

  @override
  Future<PoseSolveCheckResult> check() => _post(_path, null);

  @override
  Future<PoseSolveCheckResult> sync(String checkId) =>
      _post('$_path/sync', {'check_id': checkId});

  Future<PoseSolveCheckResult> _post(
    String path,
    Map<String, dynamic>? body,
  ) async {
    try {
      final res = await _dio.post<Map<String, dynamic>>(path, data: body);
      final data = res.data;
      if (data == null) throw const FormatException('empty answer');
      return PoseSolveCheckResult.fromJson(data);
    } on DioException catch (e) {
      final refusal = Refusal.fromDio(e);
      if (refusal != null) throw refusal;
      rethrow;
    }
  }

  @override
  void close() => _dio.close();
}
