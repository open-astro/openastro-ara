import 'dart:convert';
import 'dart:io';

import 'package:path_provider/path_provider.dart';

/// A target the user typed in by coordinates (#1267 item 1). Stored J2000 so
/// it goes through the same run path as a catalogue object; [epochTyped]
/// remembers whether the user entered it as JNow, for the dialog to show the
/// position back the way it was pasted.
class CustomTarget {
  final String name;
  final double raDeg;
  final double decDeg;
  final bool typedAsJNow;
  final DateTime savedUtc;

  const CustomTarget({
    required this.name,
    required this.raDeg,
    required this.decDeg,
    required this.typedAsJNow,
    required this.savedUtc,
  });

  Map<String, Object?> toJson() => {
        'name': name,
        'ra_deg': raDeg,
        'dec_deg': decDeg,
        'jnow': typedAsJNow,
        'saved_utc': savedUtc.toUtc().toIso8601String(),
      };

  static CustomTarget? fromJson(Object? raw) {
    if (raw is! Map) return null;
    final name = raw['name'];
    final ra = raw['ra_deg'];
    final dec = raw['dec_deg'];
    if (name is! String || ra is! num || dec is! num) return null;
    if (!ra.isFinite || !dec.isFinite || dec.abs() > 90) return null;
    final saved = raw['saved_utc'];
    return CustomTarget(
      name: name,
      raDeg: ra.toDouble(),
      decDeg: dec.toDouble(),
      typedAsJNow: raw['jnow'] == true,
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
class CustomTargetsService {
  CustomTargetsService({Future<Directory> Function()? supportDir})
      : _supportDir = supportDir ?? getApplicationSupportDirectory;

  final Future<Directory> Function() _supportDir;
  static const _fileName = 'custom_targets.json';
  static const maxEntries = 20;
  Future<void> _chain = Future<void>.value();

  Future<File> _file() async =>
      File('${(await _supportDir()).path}/$_fileName');

  Future<List<CustomTarget>> load() async {
    try {
      final f = await _file();
      if (!await f.exists()) return const [];
      final decoded = jsonDecode(await f.readAsString());
      if (decoded is! List) return const [];
      return [for (final e in decoded) ?CustomTarget.fromJson(e)];
    } catch (_) {
      return const [];
    }
  }

  /// Put [target] at the head of the list, replacing any entry with the same
  /// name (so re-adding a saved target does not duplicate it — the name is
  /// the identity; a JNow entry re-converted seconds later differs in
  /// position by microarcseconds and must still be the same target).
  /// Returns the new list. Writes are chained so two quick adds cannot
  /// interleave their load/merge/write cycles.
  Future<List<CustomTarget>> remember(CustomTarget target) {
    final task = _chain.then((_) async {
      final current = await load();
      final merged = [
        target,
        ...current.where((t) => !_same(t, target)),
      ].take(maxEntries).toList();
      try {
        final f = await _file();
        await f.writeAsString(
          jsonEncode([for (final t in merged) t.toJson()]),
          flush: true,
        );
      } catch (_) {/* best effort */}
      return merged;
    });
    _chain = task.then((_) {});
    return task;
  }

  Future<List<CustomTarget>> forget(CustomTarget target) {
    final task = _chain.then((_) async {
      final kept =
          (await load()).where((t) => !_same(t, target)).toList();
      try {
        final f = await _file();
        await f.writeAsString(
          jsonEncode([for (final t in kept) t.toJson()]),
          flush: true,
        );
      } catch (_) {/* best effort */}
      return kept;
    });
    _chain = task.then((_) {});
    return task;
  }

  static bool _same(CustomTarget a, CustomTarget b) =>
      a.name.trim().toLowerCase() == b.name.trim().toLowerCase();
}
