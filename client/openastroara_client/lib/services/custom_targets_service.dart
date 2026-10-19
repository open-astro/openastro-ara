import 'dart:convert';
import 'dart:io';

import 'package:path_provider/path_provider.dart';

/// A target the user typed in by coordinates (#1267 item 1). Stored J2000 so
/// it goes through the same run path as a catalogue object; [typedAsJNow]
/// records whether the user entered it as JNow. It is only recorded: picking
/// a recent target refills the stored J2000 position with J2000 selected (a
/// JNow position re-derived on a later date would not match what was pasted).
class CustomTarget {
  final String name;
  final double raDeg;
  final double decDeg;
  final bool typedAsJNow;
  final DateTime savedUtc;

  /// The other names the target answers to (a resolved target's Simbad or
  /// index aliases; empty for a typed position). #1267 item 5.
  final List<String> aliases;

  /// Where the target came from: `typed`, `index`, `cache` or `simbad`.
  final String source;

  const CustomTarget({
    required this.name,
    required this.raDeg,
    required this.decDeg,
    required this.typedAsJNow,
    required this.savedUtc,
    this.aliases = const [],
    this.source = 'typed',
  });

  /// The local mirror's JSON (and the profile's wire shape, which uses the
  /// same keys but `typed_as_j_now`; [toWire] / [fromWire] handle that).
  Map<String, Object?> toJson() => {
        'name': name,
        'ra_deg': raDeg,
        'dec_deg': decDeg,
        'jnow': typedAsJNow,
        'saved_utc': savedUtc.toUtc().toIso8601String(),
        if (aliases.isNotEmpty) 'aliases': aliases,
        'source': source,
      };

  /// The profile section's wire shape (`PUT /profile/saved-targets`).
  Map<String, Object?> toWire() => {
        'name': name,
        'aliases': aliases,
        'ra_deg': raDeg,
        'dec_deg': decDeg,
        'source': source,
        'saved_utc': savedUtc.toUtc().toIso8601String(),
        'typed_as_j_now': typedAsJNow,
      };

  static CustomTarget? fromWire(Object? raw) {
    if (raw is! Map) return null;
    return fromJson({...raw, 'jnow': raw['typed_as_j_now'] == true});
  }

  static CustomTarget? fromJson(Object? raw) {
    if (raw is! Map) return null;
    final name = raw['name'];
    final ra = raw['ra_deg'];
    final dec = raw['dec_deg'];
    if (name is! String || ra is! num || dec is! num) return null;
    if (!ra.isFinite || !dec.isFinite || dec.abs() > 90) return null;
    final saved = raw['saved_utc'];
    final aliases = raw['aliases'];
    final source = raw['source'];
    return CustomTarget(
      name: name,
      raDeg: ra.toDouble(),
      decDeg: dec.toDouble(),
      typedAsJNow: raw['jnow'] == true,
      aliases: aliases is List
          ? aliases.whereType<String>().toList(growable: false)
          : const [],
      source: source is String && source.isNotEmpty ? source : 'typed',
      savedUtc: saved is String
          ? (DateTime.tryParse(saved)?.toUtc() ??
              DateTime.fromMillisecondsSinceEpoch(0, isUtc: true))
          : DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
    );
  }
}

/// Persists the recent custom targets as a small JSON list in the app-support
/// directory (the same home as the planetarium display prefs), newest first,
/// capped at [maxEntries]. Offline-first: a target prepared at home is on the
/// list at the dark site. Best effort — a read or write error degrades to an
/// empty list, never a crash.
/// One scope of the mirror: a (server, profile) pair's targets and the names
/// the profile held at the last successful sync (the three-way merge base).
class ScopedTargets {
  final List<CustomTarget> targets;
  final Set<String> base;
  const ScopedTargets(this.targets, this.base);
  static const empty = ScopedTargets([], {});
}

/// The daemon's own rules (SavedTargetsValidator), applied before a PUT so one
/// entry typed before the rules existed cannot make every push fail.
bool isAcceptableSavedTarget(CustomTarget t) {
  final name = t.name.trim();
  if (name.isEmpty || name.length > 128) return false;
  if (name.codeUnits.any((c) => c < 32 || c == 127)) return false;
  if (!t.raDeg.isFinite || !t.decDeg.isFinite || t.decDeg.abs() > 90) return false;
  return const {'typed', 'index', 'cache', 'simbad'}.contains(t.source);
}

/// The local mirror of the profile's saved targets (#1267 item 5), scoped per
/// (server, profile) so one rig's list never leaks into another's, with the
/// names held at the last sync so a forget made elsewhere or offline sticks.
/// One JSON file: `{"last_scope": key, "scopes": {key: {"targets": [...],
/// "base": [names]}}}`. A pre-#1267 file (a bare list) is adopted into the
/// first scope used. Writes are serialised and atomic (temp + rename).
class CustomTargetsService {
  CustomTargetsService({Future<Directory> Function()? supportDir})
      : _supportDir = supportDir ?? getApplicationSupportDirectory;

  final Future<Directory> Function() _supportDir;
  static const _fileName = 'custom_targets.json';

  /// The daemon's cap; the dialog shows the newest few.
  static const maxEntries = 200;
  Future<void> _chain = Future<void>.value();

  Future<File> _file() async =>
      File('${(await _supportDir()).path}/$_fileName');

  Future<Map<String, Object?>> _read() async {
    try {
      final f = await _file();
      if (!await f.exists()) return {'last_scope': null, 'scopes': <String, Object?>{}};
      final decoded = jsonDecode(await f.readAsString());
      if (decoded is List) {
        // A pre-#1267 mirror: one device-wide list. Adopted by the first
        // scope that reads it (see loadScope).
        return {'last_scope': null, 'scopes': <String, Object?>{}, 'legacy': decoded};
      }
      if (decoded is Map) {
        return {
          'last_scope': decoded['last_scope'],
          'scopes': decoded['scopes'] is Map ? Map<String, Object?>.from(decoded['scopes'] as Map) : <String, Object?>{},
          if (decoded['legacy'] is List) 'legacy': decoded['legacy'],
        };
      }
    } catch (_) {/* unreadable: start empty */}
    return {'last_scope': null, 'scopes': <String, Object?>{}};
  }

  Future<void> _write(Map<String, Object?> doc) async {
    try {
      final f = await _file();
      final part = File('${f.path}.part');
      await part.writeAsString(jsonEncode(doc), flush: true);
      await part.rename(f.path);
    } catch (_) {/* best effort */}
  }

  static ScopedTargets _scopeOf(Map<String, Object?> doc, String? scope) {
    final scopes = doc['scopes'] as Map<String, Object?>;
    final raw = scope == null ? null : scopes[scope];
    if (raw is Map) {
      final t = raw['targets'];
      final b = raw['base'];
      return ScopedTargets(
        t is List ? [for (final e in t) ?CustomTarget.fromJson(e)] : const [],
        b is List ? {for (final e in b) if (e is String) e} : const {},
      );
    }
    final legacy = doc['legacy'];
    if (legacy is List) {
      return ScopedTargets([for (final e in legacy) ?CustomTarget.fromJson(e)], const {});
    }
    return ScopedTargets.empty;
  }

  static Map<String, Object?> _withScope(Map<String, Object?> doc, String scope, ScopedTargets s) {
    final scopes = Map<String, Object?>.from(doc['scopes'] as Map<String, Object?>);
    scopes[scope] = {
      'targets': [for (final t in s.targets) t.toJson()],
      'base': s.base.toList(),
    };
    return {'last_scope': scope, 'scopes': scopes};
  }

  static String nameKey(CustomTarget t) => t.name.trim().toLowerCase();

  /// The scope's targets and base; [scope] null = the scope used last (offline).
  /// A legacy device-wide list answers for a scope that has nothing yet.
  Future<ScopedTargets> loadScope(String? scope) async {
    final doc = await _read();
    return _scopeOf(doc, scope ?? doc['last_scope'] as String?);
  }

  /// The scope's targets, newest first (the old device-wide read).
  Future<List<CustomTarget>> load({String? scope}) async =>
      (await loadScope(scope)).targets;

  Future<ScopedTargets> _mutate(String? scope,
      ScopedTargets Function(ScopedTargets current) apply) {
    final task = _chain.then((_) async {
      final doc = await _read();
      final key = scope ?? doc['last_scope'] as String? ?? 'local';
      final next = apply(_scopeOf(doc, key));
      await _write(_withScope(doc, key, next));
      return next;
    });
    _chain = task.then((_) {});
    return task;
  }

  Future<List<CustomTarget>> remember(CustomTarget target, {String? scope}) async =>
      (await _mutate(scope, (cur) => ScopedTargets([
            target,
            ...cur.targets.where((t) => nameKey(t) != nameKey(target)),
          ].take(maxEntries).toList(), cur.base)))
          .targets;

  Future<List<CustomTarget>> forget(CustomTarget target, {String? scope}) async =>
      (await _mutate(scope, (cur) => ScopedTargets(
            cur.targets.where((t) => nameKey(t) != nameKey(target)).toList(),
            cur.base,
          )))
          .targets;

  /// After a successful sync: the scope's targets ARE the profile's list, and
  /// the base is what the profile holds now.
  Future<List<CustomTarget>> commitSync(String scope, List<CustomTarget> synced) async =>
      (await _mutate(scope, (_) => ScopedTargets(
            synced.take(maxEntries).toList(),
            {for (final t in synced) nameKey(t)},
          )))
          .targets;

  /// Replace a scope's targets, keeping its base (a failed push keeps the
  /// merged list locally).
  Future<List<CustomTarget>> replaceAll(List<CustomTarget> targets, {String? scope}) async =>
      (await _mutate(scope, (cur) => ScopedTargets(targets.take(maxEntries).toList(), cur.base)))
          .targets;
}
