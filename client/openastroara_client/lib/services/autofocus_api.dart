import 'package:dio/dio.dart';

import '../models/server.dart';

/// One §65.5 background job's polled state, as served by `GET /api/v1/jobs/{id}`.
/// `state` is `queued`/`running`/`complete`/`failed`/`cancelled` on the wire.
class AutofocusJob {
  final String jobId;
  final String state;
  final String? errorMessage;

  const AutofocusJob({required this.jobId, required this.state, this.errorMessage});

  bool get isTerminal => state == 'complete' || state == 'failed' || state == 'cancelled';

  static AutofocusJob fromJson(Map<String, dynamic> json) => AutofocusJob(
        jobId: json['job_id'] as String,
        state: json['state'] as String? ?? 'unknown',
        errorMessage: json['error_message'] as String?,
      );
}

/// §59 — the manual autofocus trigger: `POST /api/v1/equipment/focuser/autofocus`
/// starts one V-curve sweep with the profile's autofocus settings as a background
/// job (202 + the job), and `GET /api/v1/jobs/{id}` polls it. A duplicate start
/// while a sweep runs JOINS the running job (the daemon's single-job-per-type
/// policy), so double-taps are harmless.
///
/// An interface so tests can supply a pure fake; [DioAutofocusApi] is the
/// Dio-backed production implementation.
abstract interface class AutofocusApi {
  /// Start (or join) a sweep; returns the job to poll.
  Future<AutofocusJob> start();

  /// The job's current state, or `null` when the daemon no longer knows the id
  /// (its in-memory job store never evicts, so a null means the daemon lost
  /// state — e.g. a restart mid-sweep — NOT that the job finished).
  Future<AutofocusJob?> job(String jobId);

  void close();
}

class DioAutofocusApi implements AutofocusApi {
  final Dio _dio;

  DioAutofocusApi(AraServer server)
      : _dio = Dio(BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          receiveTimeout: const Duration(seconds: 5),
          sendTimeout: const Duration(seconds: 5),
        ));

  @override
  Future<AutofocusJob> start() async {
    final res = await _dio.post<dynamic>('/api/v1/equipment/focuser/autofocus');
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw Exception('unexpected autofocus response — not a JSON object');
    }
    return AutofocusJob.fromJson(data);
  }

  // A 404 means the daemon no longer knows the job id. The job store is
  // in-memory and never evicts, so in practice this means the daemon RESTARTED
  // mid-sweep — the caller must treat it as "lost track", not as success.
  static final Options _jobOptions = Options(
    validateStatus: (status) => status != null && (status < 400 || status == 404),
  );

  @override
  Future<AutofocusJob?> job(String jobId) async {
    final res = await _dio.get<dynamic>('/api/v1/jobs/$jobId', options: _jobOptions);
    if (res.statusCode == 404) return null;
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw Exception('unexpected job response — not a JSON object');
    }
    return AutofocusJob.fromJson(data);
  }

  @override
  void close() => _dio.close(force: true);
}
