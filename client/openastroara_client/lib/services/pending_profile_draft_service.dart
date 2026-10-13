import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'package:flutter/foundation.dart';
import 'package:path_provider/path_provider.dart';

import '../models/profile_draft.dart';
import '../models/profile_draft_codec.dart';

/// A wizard draft saved on this device because no daemon was reachable when
/// the user pressed Save (#1137, §2). It replays through the same
/// `saveWizardProfile` the connected wizard uses as soon as a server is
/// connected, then disappears.
class PendingProfileDraft {
  final String id;
  final String name;
  final DateTime savedUtc;
  final ProfileDraft draft;

  /// Where the user was: a Save & Exit mid-wizard (false) or the final Save
  /// (true). Only a final save provisions the guider / shoots darks on replay.
  final bool finalSave;

  const PendingProfileDraft({
    required this.id,
    required this.name,
    required this.savedUtc,
    required this.draft,
    required this.finalSave,
  });

  Map<String, dynamic> toJson() => {
    'id': id,
    'name': name,
    'saved_utc': savedUtc.toUtc().toIso8601String(),
    'final_save': finalSave,
    'draft': profileDraftToJson(draft),
  };

  static PendingProfileDraft? fromJson(Object? json) {
    if (json is! Map) return null;
    final id = json['id'];
    final draft = json['draft'];
    if (id is! String || id.isEmpty || draft is! Map) return null;
    return PendingProfileDraft(
      id: id,
      name: json['name'] is String
          ? json['name'] as String
          : 'Untitled profile',
      savedUtc: json['saved_utc'] is String
          ? (DateTime.tryParse(json['saved_utc'] as String)?.toUtc() ??
                DateTime.fromMillisecondsSinceEpoch(0, isUtc: true))
          : DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
      draft: profileDraftFromJson(Map<String, dynamic>.from(draft)),
      finalSave: json['final_save'] == true,
    );
  }
}

/// File-per-draft store under the app-support directory, the shape
/// `DraftSequenceService` uses for offline sequences.
class PendingProfileDraftService {
  /// [supportDir] overrides the app-support directory lookup (tests use a temp
  /// dir; production uses path_provider).
  PendingProfileDraftService({Future<Directory> Function()? supportDir})
    : _supportDir = supportDir ?? getApplicationSupportDirectory;

  final Future<Directory> Function() _supportDir;
  static const _dirName = 'profile_drafts';
  final _rand = Random();

  Future<Directory> _dir() async {
    final root = await _supportDir();
    return Directory('${root.path}/$_dirName').create(recursive: true);
  }

  File _fileFor(Directory dir, String id) =>
      File('${dir.path}/${id.replaceAll(RegExp('[^A-Za-z0-9_-]'), '_')}.json');

  String newId() =>
      'profile-draft-${DateTime.now().toUtc().millisecondsSinceEpoch}-'
      '${_rand.nextInt(0x7fffffff).toRadixString(16)}';

  /// All pending drafts, oldest first (replay in the order they were saved).
  /// Unreadable or corrupt files are skipped.
  Future<List<PendingProfileDraft>> loadAll() async {
    final dir = await _dir();
    final out = <PendingProfileDraft>[];
    await for (final entity in dir.list()) {
      if (entity is! File || !entity.path.endsWith('.json')) continue;
      try {
        final parsed = PendingProfileDraft.fromJson(
          jsonDecode(await entity.readAsString()),
        );
        if (parsed != null) out.add(parsed);
      } catch (e) {
        debugPrint('[profile-drafts] skipping unreadable ${entity.path}: $e');
      }
    }
    out.sort((a, b) => a.savedUtc.compareTo(b.savedUtc));
    return out;
  }

  Future<void> save(PendingProfileDraft draft) async {
    final f = _fileFor(await _dir(), draft.id);
    await f.writeAsString(jsonEncode(draft.toJson()), flush: true);
  }

  /// Remove a draft (after a successful replay, or a user delete). A missing
  /// file is not an error.
  Future<void> delete(String id) async {
    final f = _fileFor(await _dir(), id);
    if (await f.exists()) await f.delete();
  }
}
