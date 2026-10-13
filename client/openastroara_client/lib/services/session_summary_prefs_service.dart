import 'dart:convert';
import 'dart:io';

import 'package:path_provider/path_provider.dart';

/// #1138 — which overnight summaries this device has already shown and the
/// user dismissed (by run id), so a card is shown once. Capped so the file
/// never grows past a season of nights.
class SessionSummaryPrefsService {
  SessionSummaryPrefsService({Future<Directory> Function()? supportDir})
    : _supportDir = supportDir ?? getApplicationSupportDirectory;
  final Future<Directory> Function() _supportDir;
  static const _fileName = 'session_summaries.json';
  static const int cap = 50;
  Future<void> _chain = Future<void>.value();

  Future<File> _file() async {
    final dir = await _supportDir();
    return File('${dir.path}/$_fileName');
  }

  Future<Set<String>> loadAcknowledged() async {
    try {
      final f = await _file();
      if (!await f.exists()) return <String>{};
      final decoded = jsonDecode(await f.readAsString());
      if (decoded is! Map) return <String>{};
      final ids = decoded['acknowledged_run_ids'];
      return ids is List ? ids.whereType<String>().toSet() : <String>{};
    } catch (_) {
      return <String>{};
    }
  }

  Future<void> saveAcknowledged(Set<String> ids) {
    final task = _chain.then((_) async {
      try {
        final f = await _file();
        final list = ids.toList();
        final kept = list.length > cap ? list.sublist(list.length - cap) : list;
        await f.writeAsString(
          jsonEncode({'acknowledged_run_ids': kept}),
          flush: true,
        );
      } catch (_) {
        // best effort — a failed write only risks showing a card twice
      }
    });
    _chain = task;
    return task;
  }
}
