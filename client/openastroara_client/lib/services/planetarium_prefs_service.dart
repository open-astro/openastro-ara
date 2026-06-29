import 'dart:convert';
import 'dart:io';

import 'package:path_provider/path_provider.dart';

/// Persists the §36 planetarium's Display-panel layer toggles (deep-sky objects,
/// constellation lines/art, equatorial/azimuthal grids, DSS2, atmosphere,
/// landscape) so a user's choices survive closing and reopening Ara.
///
/// Stored as a small JSON file (`layer -> bool`) in the app-support directory. We
/// deliberately do NOT use the page's own `localStorage`: the loopback asset server
/// binds a fresh ephemeral port each run, so the page's origin — and therefore its
/// `localStorage` — changes every launch. Instead the page reports changes back over
/// the loopback event channel and reads the saved state from its load URL. Absent
/// keys fall back to the planetarium's first-run defaults.
class PlanetariumPrefsService {
  static const _fileName = 'planetarium_display.json';

  Future<File> _file() async {
    final dir = await getApplicationSupportDirectory();
    return File('${dir.path}/$_fileName');
  }

  /// The saved layer toggles, or an empty map when nothing is stored yet (or on any
  /// read/parse error — display prefs are non-critical, so we degrade to defaults).
  Future<Map<String, bool>> load() async {
    try {
      final f = await _file();
      if (!await f.exists()) return {};
      final decoded = jsonDecode(await f.readAsString());
      if (decoded is! Map) return {};
      final out = <String, bool>{};
      decoded.forEach((k, v) {
        if (k is String && v is bool) out[k] = v;
      });
      return out;
    } catch (_) {
      return {};
    }
  }

  Future<void> save(Map<String, bool> prefs) async {
    try {
      final f = await _file();
      await f.writeAsString(jsonEncode(prefs), flush: true);
    } catch (_) {/* best effort — non-critical UI prefs */}
  }
}
