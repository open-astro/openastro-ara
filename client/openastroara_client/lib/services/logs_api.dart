import 'dart:io';

import 'package:dio/dio.dart';

import '../models/log_entry.dart';
import '../models/server.dart';
import 'content_disposition.dart';

/// Abstraction over the §29.9 daemon-log endpoints, so the Support tab can be
/// driven by a fake in widget tests (provider override).
abstract interface class LogsClient {
  /// `POST /api/v1/server/logs/tail` — the newest matching entries, newest-first.
  /// [minLevel] / [containsSubstring] empty or null means "no filter".
  Future<List<LogEntry>> tail({
    int? maxLines,
    String? minLevel,
    String? containsSubstring,
  });

  /// `GET /api/v1/server/logs/download` — stream the whole newest log file (or
  /// the named one) straight to [savePath], chunk by chunk, never buffering it
  /// whole in memory (the §29.9 sink rolls at 50 MB/file). Returns the
  /// server's `Content-Disposition` file name (informational — the file is at
  /// [savePath]).
  Future<String> downloadLogTo(String savePath, {String? logFileName});

  void close();
}

/// Dio-backed [LogsClient] against the active daemon.
class LogsApi implements LogsClient {
  final Dio _dio;

  LogsApi(AraServer server)
      : _dio = Dio(BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          // Small request bodies, but bound the send so a frozen daemon can't
          // block the write indefinitely.
          sendTimeout: const Duration(seconds: 10),
          // A whole-file download can be a few MB; give it room.
          receiveTimeout: const Duration(seconds: 30),
        ));

  @override
  Future<List<LogEntry>> tail({
    int? maxLines,
    String? minLevel,
    String? containsSubstring,
  }) async {
    final res = await _dio.post<List<dynamic>>(
      '/api/v1/server/logs/tail',
      data: <String, dynamic>{
        // The daemon defaults to 200 when absent; send it explicitly so the cap
        // is the client's, not an implicit server default.
        'max_lines': maxLines ?? 200,
        if (minLevel != null && minLevel.isNotEmpty) 'min_level': minLevel,
        if (containsSubstring != null && containsSubstring.isNotEmpty)
          'contains_substring': containsSubstring,
      },
    );
    final rows = res.data ?? const <dynamic>[];
    return rows
        .map((e) => LogEntry.fromJson(e as Map<String, dynamic>))
        .toList(growable: false);
  }

  @override
  Future<String> downloadLogTo(String savePath, {String? logFileName}) async {
    // dio.download streams the response body to the path (ResponseType.stream
    // under the hood) — memory stays flat no matter how large the log grew.
    final res = await _dio.download(
      '/api/v1/server/logs/download',
      savePath,
      queryParameters: <String, dynamic>{
        if (logFileName != null && logFileName.isNotEmpty)
          'logFileName': logFileName,
      },
    );
    // A 0-byte body means no log exists / a malformed response — remove the
    // empty file rather than leaving it under a "Saved" toast.
    final file = File(savePath);
    if (!await file.exists() || await file.length() == 0) {
      if (await file.exists()) {
        await file.delete();
      }
      throw StateError('Log download returned an empty body.');
    }
    return fileNameFromContentDisposition(
            res.headers.value('content-disposition')) ??
        'openastroara-daemon.log';
  }

  @override
  void close() => _dio.close(force: true);
}
