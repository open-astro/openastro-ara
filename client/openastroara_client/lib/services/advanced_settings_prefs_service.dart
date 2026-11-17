import 'dart:convert';
import 'dart:io';

import 'package:path_provider/path_provider.dart';

/// Persists the one global Advanced toggle of Settings (#1544) as a small JSON
/// file in the app-support directory, the pattern of [NightModePrefsService].
/// It is a view preference of this device, not rig configuration, so it is not
/// in the profile.
class AdvancedSettingsPrefsService {
  /// [supportDir] overrides the app-support directory lookup (tests use a
  /// temp dir; production uses path_provider).
  AdvancedSettingsPrefsService({Future<Directory> Function()? supportDir})
    : _supportDir = supportDir ?? getApplicationSupportDirectory;

  final Future<Directory> Function() _supportDir;
  static const _fileName = 'advanced_settings.json';
  // Serializes save() calls so a fast toggle burst can't interleave writes.
  Future<void> _chain = Future<void>.value();

  Future<File> _file() async =>
      File('${(await _supportDir()).path}/$_fileName');

  /// The saved flag, or true (the default: every setting shows) when nothing
  /// is stored or the file cannot be read.
  Future<bool> load() async {
    try {
      final f = await _file();
      if (!await f.exists()) return true;
      final decoded = jsonDecode(await f.readAsString());
      return !(decoded is Map && decoded['advanced'] == false);
    } catch (_) {
      return true;
    }
  }

  Future<void> save(bool advanced) {
    final task = _chain.then((_) async {
      try {
        await (await _file()).writeAsString(
          jsonEncode({'advanced': advanced}),
          flush: true,
        );
      } catch (_) {
        /* best effort: a failed prefs write must not break the UI */
      }
    });
    _chain = task;
    return task;
  }
}
