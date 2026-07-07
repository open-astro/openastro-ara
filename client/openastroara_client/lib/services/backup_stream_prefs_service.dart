import 'dart:convert';
import 'dart:io';

import 'package:path_provider/path_provider.dart';

/// Persists the §44 backup-stream toggle + folder so the mirror survives
/// closing and reopening Ara — the daemon-side queue already survives a
/// desktop crash, but the puller must come back up on its own for the
/// "crashed desktop resumes where it left off" promise to hold end to end.
///
/// Stored as a small JSON file in the app-support directory, same pattern as
/// [PlanetariumPrefsService]: device-local state that must not ride the
/// server profile (each desktop has its own folder + slot).
class BackupStreamPrefsService {
  /// [supportDir] overrides the app-support directory lookup (tests use a
  /// temp dir; production uses path_provider).
  BackupStreamPrefsService({Future<Directory> Function()? supportDir})
      : _supportDir = supportDir ?? getApplicationSupportDirectory;

  final Future<Directory> Function() _supportDir;
  static const _fileName = 'backup_stream.json';
  // Serializes save() calls so rapid toggle+folder edits can't interleave
  // their read-modify-write cycles.
  Future<void> _chain = Future<void>.value();

  Future<File> _file() async {
    final dir = await _supportDir();
    return File('${dir.path}/$_fileName');
  }

  /// The saved state, or the disabled defaults when nothing is stored yet
  /// (or on any read/parse error — prefs are non-critical, degrade to off).
  Future<({bool enabled, String root})> load() async {
    try {
      final f = await _file();
      if (!await f.exists()) return (enabled: false, root: '');
      final decoded = jsonDecode(await f.readAsString());
      if (decoded is! Map) return (enabled: false, root: '');
      return (
        enabled: decoded['enabled'] is bool ? decoded['enabled'] as bool : false,
        root: decoded['root'] is String ? decoded['root'] as String : '',
      );
    } catch (_) {
      return (enabled: false, root: '');
    }
  }

  Future<void> save({required bool enabled, required String root}) {
    final task = _chain.then((_) async {
      try {
        final f = await _file();
        await f.writeAsString(jsonEncode({'enabled': enabled, 'root': root}), flush: true);
      } catch (_) {/* best effort — a failed prefs write must not break the stream */}
    });
    _chain = task;
    return task;
  }
}
