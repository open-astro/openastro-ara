import 'dart:convert';
import 'dart:io';

import 'package:path_provider/path_provider.dart';

/// §2 offline planning — local snapshot of the daemon's profiles so an offline
/// session still knows the user's gear. One JSON file in app-support:
///
/// ```json
/// {
///   "active_id": "…",
///   "profiles": [{"id": "…", "name": "…"}],
///   "sections": {
///     "<profileId>": {"optics": {…}, "imaging_defaults": {…},
///                      "autofocus": {…}, "site": {…}, "filter_set": {…}}
///   }
/// }
/// ```
///
/// Section payloads are the daemon's own wire JSON (the [ProfileApi] codecs
/// round-trip them), captured whenever a profile's sections are fetched while
/// connected. Sections accumulate per profile id, so any profile that has been
/// active at least once on this machine can seed an offline session; merges
/// are whole-section (a refresh replaces that profile's snapshot, apart from
/// the sections that refresh could not read, which keep their cached copy).
///
/// Reads degrade to an empty cache; writes are best-effort but chained so
/// rapid captures can't interleave.
class ProfileCacheService {
  /// [supportDir] overrides the app-support directory lookup (tests use a temp
  /// dir; production uses path_provider).
  ProfileCacheService({Future<Directory> Function()? supportDir})
      : _supportDir = supportDir ?? getApplicationSupportDirectory;

  final Future<Directory> Function() _supportDir;
  static const _fileName = 'profile_cache.json';
  Future<void> _chain = Future<void>.value();

  Future<File> _file() async {
    final dir = await _supportDir();
    return File('${dir.path}/$_fileName');
  }

  /// The cached snapshot, or an empty map on any read/parse failure.
  Future<Map<String, dynamic>> load() async {
    try {
      final f = await _file();
      if (!await f.exists()) return {};
      final decoded = jsonDecode(await f.readAsString());
      return decoded is Map ? Map<String, dynamic>.from(decoded) : {};
    } catch (_) {
      return {};
    }
  }

  /// Replace the cached profile list + active id (keeps existing sections for
  /// ids still present; drops sections for profiles the daemon deleted).
  Future<void> saveList(
      String? activeId, List<({String id, String name})> profiles) {
    return _mutate((cache) {
      cache['active_id'] = activeId;
      cache['profiles'] = [
        for (final p in profiles) {'id': p.id, 'name': p.name},
      ];
      final ids = {for (final p in profiles) p.id};
      final sections = cache['sections'];
      if (sections is Map) {
        sections.removeWhere((k, _) => !ids.contains(k));
      }
      return cache;
    });
  }

  /// Merge one profile's section snapshot (wire-shape JSON) into the cache.
  /// The snapshot replaces the profile's old one, except for the sections
  /// named in [keep] (ones this capture could not read): those keep the copy
  /// the cache already holds.
  Future<void> saveSections(
    String profileId,
    Map<String, dynamic> sections, {
    Set<String> keep = const {},
  }) {
    return _mutate((cache) {
      final all = cache['sections'];
      final map = all is Map ? Map<String, dynamic>.from(all) : <String, dynamic>{};
      final old = map[profileId];
      // Stamp when this gear snapshot was taken so offline surfaces can say
      // "cached N days ago" — a stale ranking should look stale.
      map[profileId] = {
        if (old is Map)
          for (final k in keep)
            if (old[k] != null) k: old[k],
        ...sections,
        'captured_utc': DateTime.now().toUtc().toIso8601String(),
      };
      cache['sections'] = map;
      return cache;
    });
  }

  Future<void> _mutate(
      Map<String, dynamic> Function(Map<String, dynamic>) apply) {
    final task = _chain.then((_) async {
      try {
        final cache = apply(await load());
        final f = await _file();
        await f.writeAsString(jsonEncode(cache), flush: true);
      } catch (_) {/* best effort — the cache is a convenience, never truth */}
    });
    _chain = task;
    return task;
  }
}
