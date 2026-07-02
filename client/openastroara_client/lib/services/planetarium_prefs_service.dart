import 'dart:convert';
import 'dart:io';

import 'package:path_provider/path_provider.dart';

/// Persists the §36 planetarium's Display-panel layer toggles (deep-sky objects,
/// constellation lines/art, equatorial/azimuthal grids, DSS2, atmosphere,
/// landscape) — plus the Catalogs-panel overlay toggles, namespaced as
/// `cat:{id}` keys in the same map — so a user's choices survive closing and
/// reopening Ara.
///
/// Stored as a small JSON file (`layer -> bool`) in the app-support directory. We
/// deliberately do NOT use the page's own `localStorage`: the loopback asset server
/// binds a fresh ephemeral port each run, so the page's origin — and therefore its
/// `localStorage` — changes every launch. Instead the page reports changes back over
/// the loopback event channel and reads the saved state from its load URL. Absent
/// keys fall back to the planetarium's first-run defaults.
class PlanetariumPrefsService {
  /// [supportDir] overrides the app-support directory lookup (tests use a temp
  /// dir; production uses path_provider).
  PlanetariumPrefsService({Future<Directory> Function()? supportDir})
      : _supportDir = supportDir ?? getApplicationSupportDirectory;

  final Future<Directory> Function() _supportDir;
  static const _fileName = 'planetarium_display.json';
  // Serializes save() calls — see save().
  Future<void> _chain = Future<void>.value();

  Future<File> _file() async {
    final dir = await _supportDir();
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

  /// MERGE-on-save: [prefs] is laid over the stored map, so a key the page
  /// didn't repeat this time keeps its saved value instead of being dropped.
  /// That closes a real loss path: the page can only report toggles it knows
  /// about *this session* (e.g. a saved-on catalog whose boot-time re-fetch
  /// failed never lands in its state map), and a full-overwrite save would
  /// silently erase such keys the moment any unrelated toggle posted.
  /// Explicit "off" still persists — the page posts `false` values, it just
  /// can't be relied on to re-post every historical key.
  ///
  /// Saves are chained so two rapid posts can't interleave their
  /// load-merge-write cycles and drop each other's delta; the inner catch
  /// keeps the chain alive after a failed write (best effort — UI prefs).
  Future<void> save(Map<String, bool> prefs) {
    final task = _chain.then((_) async {
      try {
        final merged = await load()..addAll(prefs);
        final f = await _file();
        await f.writeAsString(jsonEncode(merged), flush: true);
      } catch (_) {/* best effort — non-critical UI prefs */}
    });
    _chain = task;
    return task;
  }
}
