import 'dart:io';

import 'package:dio/dio.dart';

import '../models/bug_report_preparation.dart';
import '../models/server.dart';
import 'content_disposition.dart';

/// Abstraction over the §54 bug-report endpoints so the Support UI can be
/// driven by a fake in widget tests.
abstract interface class BugReportClient {
  /// `POST /api/v1/bugreport/prepare` — stage a diagnostic ZIP and describe it.
  Future<BugReportPreparation> prepare();

  /// `GET /api/v1/bugreport/download?preparationId=…&acknowledge=pii` — stream
  /// the ZIP straight to [savePath], chunk by chunk, never buffering it whole
  /// in memory. The `acknowledge=pii` parameter is required by the server (the
  /// bundle carries logs + the full profile + the filesystem path), so this is
  /// only called after the user confirms the PII disclosure. Returns the
  /// server's `Content-Disposition` file name (informational — the file is at
  /// [savePath]).
  Future<String> downloadTo(String savePath, String preparationId);

  void close();
}

/// Dio-backed [BugReportClient] against the active daemon.
class BugReportApi implements BugReportClient {
  final Dio _dio;

  BugReportApi(AraServer server)
      : _dio = Dio(BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          sendTimeout: const Duration(seconds: 10),
          // Staging the ZIP (logs + profile) and streaming it back can take a
          // little while on a busy daemon / slow LAN.
          receiveTimeout: const Duration(seconds: 60),
        ));

  @override
  Future<BugReportPreparation> prepare() async {
    final res = await _dio.post<Map<String, dynamic>>(
      '/api/v1/bugreport/prepare',
    );
    final data = res.data;
    if (data == null) {
      throw StateError('Bug-report prepare returned an empty response.');
    }
    return BugReportPreparation.fromJson(data);
  }

  @override
  Future<String> downloadTo(String savePath, String preparationId) async {
    // dio.download streams the body to the path — the multi-log ZIP never sits
    // whole in client memory.
    final res = await _dio.download(
      '/api/v1/bugreport/download',
      savePath,
      queryParameters: <String, dynamic>{
        'preparationId': preparationId,
        // Required server-side PII gate (403 without it).
        'acknowledge': 'pii',
      },
    );
    // The bundle always carries system-info.json server-side, so a 0-byte body
    // is a malformed response — remove the empty file rather than leaving it
    // under a "Saved" toast (mirrors prepare()'s null-data guard).
    final file = File(savePath);
    if (!await file.exists() || await file.length() == 0) {
      if (await file.exists()) {
        await file.delete();
      }
      throw StateError('Bug-report download returned an empty body.');
    }
    return fileNameFromContentDisposition(
            res.headers.value('content-disposition')) ??
        'openastroara-bug-report.zip';
  }

  @override
  void close() => _dio.close(force: true);
}
