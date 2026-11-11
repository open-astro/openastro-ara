import 'dart:convert';
import 'dart:io';

import 'package:path_provider/path_provider.dart';

/// #1644 — which synced preference keys hold a local change the rig has not
/// stored yet (the write failed, or no rig was reachable). The next successful
/// client-settings load sends them before any server value is applied.
/// Device-local by nature: one small JSON file in the app-support directory.
class SyncedPrefsPendingService {
  SyncedPrefsPendingService({Future<Directory> Function()? supportDir})
    : _supportDir = supportDir ?? getApplicationSupportDirectory;
  final Future<Directory> Function() _supportDir;
  static const _fileName = 'synced_prefs_pending.json';
  Future<void> _chain = Future<void>.value();

  Future<File> _file() async {
    final dir = await _supportDir();
    return File('${dir.path}/$_fileName');
  }

  Future<Set<String>> load() async {
    try {
      final f = await _file();
      if (!await f.exists()) return <String>{};
      final decoded = jsonDecode(await f.readAsString());
      final keys = decoded is Map ? decoded['keys'] : null;
      return keys is List ? keys.whereType<String>().toSet() : <String>{};
    } catch (_) {
      return <String>{};
    }
  }

  Future<void> mark(String key) => _update((keys) => keys.add(key));

  Future<void> clear(String key) => _update((keys) => keys.remove(key));

  Future<void> _update(void Function(Set<String>) change) {
    final task = _chain.then((_) async {
      try {
        final keys = await load();
        change(keys);
        final f = await _file();
        await f.writeAsString(
          jsonEncode({'keys': keys.toList()..sort()}),
          flush: true,
        );
      } catch (_) {
        // best effort — a lost marker only means the next change re-marks it
      }
    });
    _chain = task;
    return task;
  }
}
