import 'dart:convert';
import 'dart:io';

import 'package:path_provider/path_provider.dart';

/// Persists the Simple Mode toggle (the image-first shell, ALP-4810) so a
/// client that was left in Simple Mode comes back up in it after a relaunch.
///
/// Same small-JSON-file pattern as [NightModePrefsService]: a per-client
/// display preference, not a server setting and not the keyring.
class SimpleModePrefsService {
  /// [supportDir] overrides the app-support directory lookup (tests use a
  /// temp dir; production uses path_provider).
  ///
  /// [fileName] picks the flag's file: `simple_mode.json` for the mode
  /// itself, `simple_mode_feature.json` for the experimental feature toggle.
  SimpleModePrefsService({
    Future<Directory> Function()? supportDir,
    this.fileName = 'simple_mode.json',
  }) : _supportDir = supportDir ?? getApplicationSupportDirectory;

  final Future<Directory> Function() _supportDir;
  final String fileName;
  // Serializes save() calls so a fast toggle burst can't interleave writes.
  Future<void> _chain = Future<void>.value();

  Future<File> _file() async {
    final dir = await _supportDir();
    return File('${dir.path}/$fileName');
  }

  /// The saved flag, or false (the full UI, the desktop default) when nothing
  /// is stored yet or on any read/parse error.
  Future<bool> load() async {
    try {
      final f = await _file();
      if (!await f.exists()) return false;
      final decoded = jsonDecode(await f.readAsString());
      if (decoded is! Map) return false;
      return decoded['enabled'] is bool ? decoded['enabled'] as bool : false;
    } catch (_) {
      return false;
    }
  }

  Future<void> save(bool enabled) {
    final task = _chain.then((_) async {
      try {
        final f = await _file();
        await f.writeAsString(jsonEncode({'enabled': enabled}), flush: true);
      } catch (_) {
        /* best effort — a failed prefs write must not break the UI */
      }
    });
    _chain = task;
    return task;
  }
}
