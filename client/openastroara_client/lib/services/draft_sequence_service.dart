import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'package:path_provider/path_provider.dart';

import '../models/sequence/draft_sequence.dart';

/// §2/§28.9 offline planning — file-backed store for client-managed draft
/// sequences. One JSON file per draft under `sequence_drafts/` in the
/// app-support directory (same mechanism as [PlanetariumPrefsService]; a
/// directory of files rather than one blob because bodies can be tens of KB
/// and drafts are added/removed independently).
///
/// All reads degrade to "no drafts" and skip unparseable files; writes are
/// flushed so a draft survives an immediate app quit — losing a night plan the
/// user built offline is the one failure this store exists to prevent.
class DraftSequenceService {
  /// [supportDir] overrides the app-support directory lookup (tests use a temp
  /// dir; production uses path_provider).
  DraftSequenceService({Future<Directory> Function()? supportDir})
      : _supportDir = supportDir ?? getApplicationSupportDirectory;

  final Future<Directory> Function() _supportDir;
  static const _dirName = 'sequence_drafts';
  final _rand = Random();

  Future<Directory> _dir() async {
    final root = await _supportDir();
    return Directory('${root.path}/$_dirName').create(recursive: true);
  }

  File _fileFor(Directory dir, String id) =>
      // Strip the namespace prefix and any path-hostile chars from the
      // filename; the id inside the JSON stays canonical.
      File('${dir.path}/${id.replaceAll(RegExp('[^A-Za-z0-9_-]'), '_')}.json');

  /// Mint a new draft id (namespaced so it can never collide with a daemon
  /// sequence GUID at any call site).
  String newId() =>
      '$draftIdPrefix${DateTime.now().toUtc().millisecondsSinceEpoch}-'
      '${_rand.nextInt(0x7fffffff).toRadixString(16)}';

  /// All stored drafts, newest-first. Unreadable/corrupt files are skipped.
  Future<List<DraftSequence>> loadAll() async {
    try {
      final dir = await _dir();
      final drafts = <DraftSequence>[];
      await for (final entry in dir.list()) {
        if (entry is! File || !entry.path.endsWith('.json')) continue;
        try {
          final draft = DraftSequence.fromJson(
              jsonDecode(await entry.readAsString()));
          if (draft != null) drafts.add(draft);
        } catch (_) {/* skip a corrupt draft file, keep the rest */}
      }
      drafts.sort((a, b) => b.updatedUtc.compareTo(a.updatedUtc));
      return drafts;
    } catch (_) {
      return const [];
    }
  }

  /// Create-or-overwrite [draft] by id. Throws on write failure — callers
  /// surface it (a draft that silently failed to persist is a lost night plan).
  Future<void> save(DraftSequence draft) async {
    final dir = await _dir();
    await _fileFor(dir, draft.id)
        .writeAsString(jsonEncode(draft.toJson()), flush: true);
  }

  /// Remove a draft (after a successful push, or a user delete). Missing file
  /// is a no-op — the outcome the caller wanted already holds.
  Future<void> delete(String id) async {
    try {
      final dir = await _dir();
      final f = _fileFor(dir, id);
      if (await f.exists()) await f.delete();
    } catch (_) {/* best effort */}
  }
}
