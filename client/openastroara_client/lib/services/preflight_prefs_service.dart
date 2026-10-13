import 'dart:convert';
import 'dart:io';

import 'package:path_provider/path_provider.dart';

/// §58.14 — which nights this device has already answered the pre-sleep card
/// for (see `preflightNightKey`). Capped so the file never grows past a season.
class PreflightPrefsService {
  PreflightPrefsService({Future<Directory> Function()? supportDir})
    : _supportDir = supportDir ?? getApplicationSupportDirectory;
  final Future<Directory> Function() _supportDir;
  static const _fileName = 'preflight_acknowledged.json';
  static const int cap = 60;
  Future<void> _chain = Future<void>.value();

  Future<File> _file() async {
    final dir = await _supportDir();
    return File('${dir.path}/$_fileName');
  }

  Future<Set<String>> loadAcknowledgedNights() async {
    try {
      final f = await _file();
      if (!await f.exists()) return <String>{};
      final decoded = jsonDecode(await f.readAsString());
      if (decoded is! Map) return <String>{};
      final nights = decoded['acknowledged_nights'];
      return nights is List ? nights.whereType<String>().toSet() : <String>{};
    } catch (_) {
      return <String>{};
    }
  }

  Future<void> saveAcknowledgedNights(Set<String> nights) {
    final task = _chain.then((_) async {
      try {
        final f = await _file();
        final list = nights.toList()..sort();
        final kept = list.length > cap ? list.sublist(list.length - cap) : list;
        await f.writeAsString(
          jsonEncode({'acknowledged_nights': kept}),
          flush: true,
        );
      } catch (_) {
        // best effort — a failed write only risks asking twice
      }
    });
    _chain = task;
    return task;
  }
}
