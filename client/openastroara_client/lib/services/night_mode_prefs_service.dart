import 'dart:convert';
import 'dart:io';

import 'package:path_provider/path_provider.dart';

/// Persists the night-mode (red display) toggle so a dark-site setup comes
/// back up in night mode after a relaunch.
///
/// Stored as a small JSON file in the app-support directory, same pattern as
/// [PlanetariumPrefsService] / [BackupStreamPrefsService]. Deliberately NOT
/// flutter_secure_storage: that's the keyring, reserved for the saved-server
/// credentials — a display preference doesn't belong there (it can prompt for
/// keychain access on macOS, and silently never persists on a Linux box with
/// no secret service, which is exactly the field-laptop case night mode is
/// for).
class NightModePrefsService {
  /// [supportDir] overrides the app-support directory lookup (tests use a
  /// temp dir; production uses path_provider).
  NightModePrefsService({Future<Directory> Function()? supportDir})
    : _supportDir = supportDir ?? getApplicationSupportDirectory;

  final Future<Directory> Function() _supportDir;
  static const _fileName = 'night_mode.json';
  // Serializes save() calls so a fast toggle burst can't interleave writes.
  Future<void> _chain = Future<void>.value();

  Future<File> _file() async {
    final dir = await _supportDir();
    return File('${dir.path}/$_fileName');
  }

  /// The saved flag, or false when nothing is stored yet (or on any
  /// read/parse error — a display pref is non-critical, degrade to off).
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
