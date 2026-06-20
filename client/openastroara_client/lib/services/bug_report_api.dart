import 'dart:typed_data';

import 'package:dio/dio.dart';

import '../models/bug_report_preparation.dart';
import '../models/server.dart';

/// Result of a bug-report download: the ZIP bytes + the server-supplied file
/// name (from `Content-Disposition`), used as the default Save-As name.
typedef BugReportDownload = ({Uint8List bytes, String fileName});

/// Abstraction over the §54 bug-report endpoints so the Support UI can be
/// driven by a fake in widget tests.
abstract interface class BugReportClient {
  /// `POST /api/v1/bugreport/prepare` — stage a diagnostic ZIP and describe it.
  Future<BugReportPreparation> prepare();

  /// `GET /api/v1/bugreport/download?preparationId=…&acknowledge=pii` — the ZIP.
  /// The `acknowledge=pii` parameter is required by the server (the bundle
  /// carries logs + the full profile + the filesystem path), so this is only
  /// called after the user confirms the PII disclosure.
  Future<BugReportDownload> download(String preparationId);

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
  Future<BugReportDownload> download(String preparationId) async {
    final res = await _dio.get<List<int>>(
      '/api/v1/bugreport/download',
      queryParameters: <String, dynamic>{
        'preparationId': preparationId,
        // Required server-side PII gate (403 without it).
        'acknowledge': 'pii',
      },
      options: Options(responseType: ResponseType.bytes),
    );
    final bytes = Uint8List.fromList(res.data ?? const <int>[]);
    final name = _fileNameFromContentDisposition(
            res.headers.value('content-disposition')) ??
        'openastroara-bug-report.zip';
    return (bytes: bytes, fileName: name);
  }

  @override
  void close() => _dio.close(force: true);

  // Pull the file name out of a Content-Disposition header, if present —
  // preferring the RFC 5987 filename*=UTF-8''<pct-encoded> form — and strip any
  // path component (defence-in-depth before it reaches the Save dialog).
  static String? _fileNameFromContentDisposition(String? header) {
    if (header == null) return null;
    final extended =
        RegExp("filename\\*=[^']*'[^']*'([^;]+)", caseSensitive: false)
            .firstMatch(header);
    if (extended != null) {
      final raw = extended.group(1)!.trim();
      String decoded;
      try {
        decoded = Uri.decodeComponent(raw);
      } catch (_) {
        decoded = raw;
      }
      return _basename(decoded);
    }
    final plain = RegExp('filename="?([^";]+)"?', caseSensitive: false)
        .firstMatch(header);
    final name = plain?.group(1)?.trim();
    return name == null ? null : _basename(name);
  }

  static String? _basename(String name) {
    final last = name.split(RegExp(r'[/\\]')).last.trim();
    return last.isEmpty ? null : last;
  }
}
