import 'dart:typed_data';

import 'package:dio/dio.dart';

import '../models/autofocus_run.dart';
import '../models/server.dart';

/// A fetched run frame: JPEG bytes + the server's `X-Frame-Seq`.
class AutofocusFrame {
  final Uint8List bytes;
  final int seq;
  const AutofocusFrame(this.bytes, this.seq);
}

/// One §65.5 background job's polled state, as served by `GET /api/v1/jobs/{id}`.
/// `state` is `queued`/`running`/`complete`/`failed`/`cancelled` on the wire.
class AutofocusJob {
  final String jobId;
  final String state;
  final String? errorMessage;
  final int done;
  final int total;

  const AutofocusJob({
    required this.jobId,
    required this.state,
    this.errorMessage,
    this.done = 0,
    this.total = 0,
  });

  bool get isTerminal => state == 'complete' || state == 'failed' || state == 'cancelled';

  static AutofocusJob fromJson(Map<String, dynamic> json) {
    final jobId = json['job_id'];
    if (jobId is! String) {
      throw FormatException(
          'autofocus job missing string "job_id" (${jobId.runtimeType})');
    }
    return AutofocusJob(
        jobId: jobId,
        state: json['state'] as String? ?? 'unknown',
        errorMessage: json['error_message'] as String?,
        done: (json['done'] as num?)?.toInt() ?? 0,
        total: (json['total'] as num?)?.toInt() ?? 0,
      );
  }
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

  /// The daemon's current / most recent run record (`GET /api/v1/autofocus/state`).
  Future<AutofocusRun> state();

  /// Cancel the run in progress, whoever started it. A 409 (nothing running) is
  /// swallowed — the record will say so on the next [state] read.
  Future<void> cancel();

  /// The run's rendered frame, or `null` (204) when none exists yet.
  Future<AutofocusFrame?> fetchFrame();

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
  Future<AutofocusRun> state() async {
    final res = await _dio.get<dynamic>('/api/v1/autofocus/state');
    final data = res.data;
    return data is Map<String, dynamic> ? AutofocusRun.fromJson(data) : AutofocusRun.idle;
  }

  @override
  Future<void> cancel() async {
    await _dio.post<void>(
      '/api/v1/autofocus/cancel',
      options: Options(validateStatus: (s) => s != null && (s < 400 || s == 409)),
    );
  }

  @override
  Future<AutofocusFrame?> fetchFrame() async {
    final res = await _dio.get<List<int>>(
      '/api/v1/autofocus/frame',
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
    return AutofocusFrame(u8, seq);
  }

  @override
  void close() => _dio.close(force: true);
}
