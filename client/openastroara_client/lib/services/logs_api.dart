import 'dart:typed_data';

import 'package:dio/dio.dart';

import '../models/log_entry.dart';
import '../models/server.dart';

/// Result of a daemon-log download: the raw bytes plus the server-supplied
/// file name (from `Content-Disposition`), used as the default Save-As name.
typedef LogDownload = ({Uint8List bytes, String fileName});

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

  /// `GET /api/v1/server/logs/download` — the whole newest log file (or the
  /// named one), with its server file name for the Save dialog.
  Future<LogDownload> downloadLog({String? logFileName});

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
  Future<LogDownload> downloadLog({String? logFileName}) async {
    // The whole file is buffered into a single Uint8List. The §29.9 sink rolls at
    // 50 MB/file, so that's the hard upper bound — fine for a desktop client;
    // streaming straight to the chosen path is tracked as a follow-up in PORT_TODO.
    final res = await _dio.get<List<int>>(
      '/api/v1/server/logs/download',
      queryParameters: <String, dynamic>{
        if (logFileName != null && logFileName.isNotEmpty)
          'logFileName': logFileName,
      },
      options: Options(responseType: ResponseType.bytes),
    );
    final bytes = Uint8List.fromList(res.data ?? const <int>[]);
    final name = _fileNameFromContentDisposition(
            res.headers.value('content-disposition')) ??
        'openastroara-daemon.log';
    return (bytes: bytes, fileName: name);
  }

  @override
  void close() => _dio.close(force: true);

  // Pull the file name out of a Content-Disposition header, if present. Prefers
  // the RFC 5987 `filename*=UTF-8''<pct-encoded>` form (what ASP.NET emits for a
  // non-ASCII name), falling back to the plain `filename="..."`.
  static String? _fileNameFromContentDisposition(String? header) {
    if (header == null) return null;
    // Require the RFC 5987 charset'language' prefix so a malformed prefix-less
    // `filename*=...` doesn't get matched here (it falls through to plain/default).
    final extended =
        RegExp("filename\\*=[^']*'[^']*'([^;]+)", caseSensitive: false)
            .firstMatch(header);
    if (extended != null) {
      // RFC 5987 ext-values are percent-encoded and never quoted, so just trim +
      // decode — no quote-stripping (which the plain branch also doesn't do).
      final raw = extended.group(1)!.trim();
      try {
        return Uri.decodeComponent(raw);
      } catch (_) {
        return raw;
      }
    }
    final plain = RegExp('filename="?([^";]+)"?', caseSensitive: false)
        .firstMatch(header);
    return plain?.group(1)?.trim();
  }
}
