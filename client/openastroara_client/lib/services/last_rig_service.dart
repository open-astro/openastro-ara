import 'dart:convert';
import 'dart:io';

import 'package:path_provider/path_provider.dart';

/// The last rig this client connected to: its name and the daemon's
/// server_uuid (null when remembered before uuids were kept).
class LastRig {
  const LastRig(this.name, {this.uuid});
  final String name;
  final String? uuid;

  @override
  bool operator ==(Object other) => other is LastRig && other.name == name && other.uuid == uuid;

  @override
  int get hashCode => Object.hash(name, uuid);

  @override
  String toString() => 'LastRig($name, uuid=$uuid)';
}

/// Remembers the last rig this client connected to (ALP-4890), so the next
/// launch can connect to it when the scan finds it. Its name and uuid only:
/// its address can change between nights (#1129).
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

  /// The saved rig, or null when nothing is stored or on any read error.
  Future<LastRig?> load() async {
    try {
      final f = await _file();
      if (!await f.exists()) return null;
      final decoded = jsonDecode(await f.readAsString());
      if (decoded is! Map || decoded['name'] is! String) return null;
      final uuid = decoded['uuid'];
      return LastRig(decoded['name'] as String, uuid: uuid is String ? uuid : null);
    } catch (_) {
      return null;
    }
  }

  Future<void> save(LastRig rig) async {
    try {
      await (await _file()).writeAsString(
          jsonEncode({'name': rig.name, if (rig.uuid != null) 'uuid': rig.uuid}),
          flush: true);
    } catch (_) {
      /* best effort — a failed write only means one more tap next launch */
    }
  }
}
