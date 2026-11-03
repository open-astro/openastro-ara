import 'dart:convert';
import 'dart:io';

import 'package:path_provider/path_provider.dart';

/// Remembers the name of the last rig this client connected to (ALP-4890),
/// so the next launch can connect to it when the scan finds it. Only the
/// name: its address can change between nights (#1129).
///
/// Same small-JSON-file pattern as [SimpleModePrefsService].
class LastRigService {
  /// [supportDir] overrides the app-support directory lookup (tests use a
  /// temp dir; production uses path_provider).
  LastRigService({Future<Directory> Function()? supportDir})
      : _supportDir = supportDir ?? getApplicationSupportDirectory;

  final Future<Directory> Function() _supportDir;

  Future<File> _file() async =>
      File('${(await _supportDir()).path}/last_rig.json');

  /// The saved name, or null when nothing is stored or on any read error.
  Future<String?> load() async {
    try {
      final f = await _file();
      if (!await f.exists()) return null;
      final decoded = jsonDecode(await f.readAsString());
      return decoded is Map && decoded['name'] is String
          ? decoded['name'] as String
          : null;
    } catch (_) {
      return null;
    }
  }

  Future<void> save(String name) async {
    try {
      await (await _file())
          .writeAsString(jsonEncode({'name': name}), flush: true);
    } catch (_) {
      /* best effort — a failed write only means one more tap next launch */
    }
  }
}
