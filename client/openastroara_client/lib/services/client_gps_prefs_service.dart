import 'dart:convert';
import 'dart:io';

import 'package:path_provider/path_provider.dart';

/// The client-side USB GPS preference: whether to read a dongle on this
/// computer, and which serial port. Client-local (not a profile field): the
/// dongle is plugged into this machine, not the rig.
class ClientGpsPrefs {
  const ClientGpsPrefs({this.enabled = false, this.port});
  final bool enabled;
  final String? port;

  ClientGpsPrefs copyWith({bool? enabled, String? port, bool clearPort = false}) => ClientGpsPrefs(
        enabled: enabled ?? this.enabled,
        port: clearPort ? null : (port ?? this.port),
      );
}

/// JSON file in the app-support directory, same pattern as
/// [NightModePrefsService]: best-effort, never throws into the UI.
class ClientGpsPrefsService {
  ClientGpsPrefsService({Future<Directory> Function()? supportDir})
      : _supportDir = supportDir ?? getApplicationSupportDirectory;

  final Future<Directory> Function() _supportDir;
  static const _fileName = 'client_gps.json';
  Future<void> _chain = Future<void>.value();

  Future<File> _file() async => File('${(await _supportDir()).path}/$_fileName');

  Future<ClientGpsPrefs> load() async {
    try {
      final f = await _file();
      if (!await f.exists()) return const ClientGpsPrefs();
      final decoded = jsonDecode(await f.readAsString());
      if (decoded is! Map) return const ClientGpsPrefs();
      final port = decoded['port'];
      return ClientGpsPrefs(
        enabled: decoded['enabled'] == true,
        port: port is String && port.isNotEmpty ? port : null,
      );
    } catch (_) {
      return const ClientGpsPrefs();
    }
  }

  Future<void> save(ClientGpsPrefs prefs) {
    final task = _chain.then((_) async {
      try {
        final f = await _file();
        await f.writeAsString(
          jsonEncode({'enabled': prefs.enabled, 'port': ?prefs.port}),
          flush: true,
        );
      } catch (_) {
        /* best effort */
      }
    });
    _chain = task;
    return task;
  }
}
