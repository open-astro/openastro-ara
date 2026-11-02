import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/server.dart';
import '../state/saved_server_state.dart';
import 'autofocus_api.dart' show AutofocusJob;

/// `POST /api/v1/platesolve/center`: slew to J2000 coordinates and centre by
/// iterative plate solves, as a §65.5 background job (polled at
/// `/api/v1/jobs/{id}`, cancelled with `DELETE`). The job's shape is the
/// shared `BatchJobDto`, the one [AutofocusJob] already parses.
abstract interface class CenterClient {
  Future<AutofocusJob> start(double raHoursJ2000, double decDegJ2000);

  /// The job, or null when the daemon no longer knows it (restarted).
  Future<AutofocusJob?> job(String jobId);
  Future<void> cancel(String jobId);
  void close();
}

class CenterApi implements CenterClient {
  CenterApi(AraServer server)
    : _dio = Dio(
        BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          receiveTimeout: const Duration(seconds: 10),
        ),
      );

  final Dio _dio;

  @override
  Future<AutofocusJob> start(double raHoursJ2000, double decDegJ2000) async {
    final res = await _dio.post<dynamic>(
      '/api/v1/platesolve/center',
      data: {'ra_hours': raHoursJ2000, 'dec_degrees': decDegJ2000},
    );
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw const FormatException('center returned a non-object body');
    }
    return AutofocusJob.fromJson(data);
  }

  @override
  Future<AutofocusJob?> job(String jobId) async {
    final res = await _dio.get<dynamic>(
      '/api/v1/jobs/$jobId',
      options: Options(
        validateStatus: (s) => s != null && (s < 400 || s == 404),
      ),
    );
    if (res.statusCode == 404) return null;
    final data = res.data;
    return data is Map<String, dynamic> ? AutofocusJob.fromJson(data) : null;
  }

  @override
  Future<void> cancel(String jobId) async {
    await _dio.delete<void>(
      '/api/v1/jobs/$jobId',
      options: Options(validateStatus: (s) => s != null && s < 500),
    );
  }

  @override
  void close() => _dio.close(force: true);
}

/// Builds a [CenterClient] for a server. Overridable in tests.
final centerApiFactoryProvider = Provider<CenterClient Function(AraServer)>(
  (ref) => CenterApi.new,
);

final centerApiProvider = Provider<CenterClient?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(centerApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});
