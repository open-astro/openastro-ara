import 'dart:convert';
import 'dart:io';

import 'package:path_provider/path_provider.dart';

/// #1140 — client-local planning knobs (no profile section on the daemon:
/// planning math lives in the client, PORT_DECISIONS 2026-07-15). One JSON
/// file in app support, like the night-mode and backup-stream prefs.
class PlanningPrefs {
  /// Glover noise tolerance: the percent of stack noise the read noise may
  /// add. 5 is the classic figure; lower = longer subs for the same sky.
  final double noiseTolerancePct;
  const PlanningPrefs({this.noiseTolerancePct = defaultNoiseTolerancePct});

  static const double defaultNoiseTolerancePct = 5.0;
  static const double minNoiseTolerancePct = 1.0;
  static const double maxNoiseTolerancePct = 20.0;

  PlanningPrefs copyWith({double? noiseTolerancePct}) => PlanningPrefs(
    noiseTolerancePct: noiseTolerancePct ?? this.noiseTolerancePct,
  );

  Map<String, dynamic> toJson() => {'noise_tolerance_pct': noiseTolerancePct};

  /// Tolerant parse: a missing or hand-edited value falls back to the default
  /// (clamped like the write path).
  static PlanningPrefs fromJson(Object? json) {
    if (json is! Map) return const PlanningPrefs();
    final v = json['noise_tolerance_pct'];
    return PlanningPrefs(
      noiseTolerancePct: v is num && v.isFinite
          ? v.toDouble().clamp(minNoiseTolerancePct, maxNoiseTolerancePct)
          : defaultNoiseTolerancePct,
    );
  }
}

class PlanningPrefsService {
  PlanningPrefsService({Future<Directory> Function()? supportDir})
    : _supportDir = supportDir ?? getApplicationSupportDirectory;
  final Future<Directory> Function() _supportDir;
  static const _fileName = 'planning_prefs.json';
  Future<void> _chain = Future<void>.value();

  Future<File> _file() async {
    final dir = await _supportDir();
    return File('${dir.path}/$_fileName');
  }

  Future<PlanningPrefs> load() async {
    try {
      final f = await _file();
      if (!await f.exists()) return const PlanningPrefs();
      return PlanningPrefs.fromJson(jsonDecode(await f.readAsString()));
    } catch (_) {
      return const PlanningPrefs();
    }
  }

  Future<void> save(PlanningPrefs prefs) {
    final task = _chain.then((_) async {
      try {
        final f = await _file();
        await f.writeAsString(jsonEncode(prefs.toJson()), flush: true);
      } catch (_) {
        // best effort — a failed prefs write must not break planning
      }
    });
    _chain = task;
    return task;
  }
}
