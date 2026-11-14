import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/profile_api.dart';

/// ADR 0015 — a client profile section is loaded before it is saved.
///
/// Where a section's value came from. Only [rig] may be saved: [cache] is an
/// offline snapshot and [notLoaded] holds no value at all (never a default).
enum SectionSource { notLoaded, cache, rig }

class LoadedSection<T> {
  const LoadedSection({
    this.value,
    this.source = SectionSource.notLoaded,
    this.etag,
    this.lastFailure,
    this.conflict = false,
  });

  /// The section; null exactly when [source] is [SectionSource.notLoaded].
  final T? value;
  final SectionSource source;

  /// The rig's tag for the copy that was loaded; sent back as `If-Match`.
  final String? etag;

  /// Why the last load failed (cleared by the next good load).
  final Object? lastFailure;

  /// The rig refused the last save: another device saved a newer copy. The
  /// person's edits stay in [value] until they reload.
  final bool conflict;

  bool get canSave => source == SectionSource.rig;

  LoadedSection<T> copyWith({T? value, Object? lastFailure, bool? conflict}) =>
      LoadedSection(
        value: value ?? this.value,
        source: source,
        etag: etag,
        lastFailure: lastFailure ?? this.lastFailure,
        conflict: conflict ?? this.conflict,
      );
}

/// Save refused: the section is not loaded from the rig and a retry of the
/// load failed. Nothing was written.
class SectionNotLoadedException implements Exception {
  const SectionNotLoadedException(this.cause);
  final Object? cause;
  @override
  String toString() =>
      'Settings not loaded from the rig; nothing was saved'
      '${cause == null ? '' : ' ($cause)'}';
}

/// Save refused by the rig (412): a newer copy exists. Nothing was written.
class SectionConflictException implements Exception {
  const SectionConflictException();
  @override
  String toString() =>
      'These settings changed on the rig since you loaded them; nothing was saved.';
}

typedef Tagged<T> = ({T value, String? etag});

/// Load-before-save for one profile section. A subclass says how to fetch and
/// send; the rules live here so slice 2 reuses them.
abstract class SectionNotifier<T> extends Notifier<LoadedSection<T>> {
  Future<Tagged<T>> fetch(ProfileApi api);
  Future<Tagged<T>> send(ProfileApi api, T value, String? etag);

  @override
  LoadedSection<T> build() => const LoadedSection();

  // Bumped by every load and by reset: a result from an older rig/profile or
  // an older load must not land.
  int _generation = 0;
  Future<void>? _saveChain;

  /// Replace the section with the rig's copy (also discards edits and a
  /// conflict). A failure is kept in [LoadedSection.lastFailure]; a section
  /// that was loaded stays as it was. Returns whether the load succeeded.
  Future<bool> load(ProfileApi api) async {
    final gen = ++_generation;
    try {
      final r = await fetch(api);
      if (ref.mounted && gen == _generation) {
        state = LoadedSection(
          value: r.value,
          source: SectionSource.rig,
          etag: r.etag,
        );
      }
      return true;
    } catch (e) {
      if (ref.mounted && gen == _generation) {
        state = state.copyWith(lastFailure: e);
      }
      return false;
    }
  }

  /// Same as [load], for the live-sync table.
  Future<void> hydrateFromServer(ProfileApi api) => load(api);

  /// Apply an edit; ignored while there is nothing loaded to edit.
  void edit(T Function(T current) change) {
    final v = state.value;
    if (v == null) return;
    state = state.copyWith(value: change(v));
  }

  /// Back to not loaded (rig or profile changed).
  void reset() {
    _generation++;
    state = const LoadedSection();
  }

  /// Save the edits. Not loaded from the rig: load once more first; if that
  /// fails too, throw [SectionNotLoadedException] and write nothing. Sends the
  /// loaded ETag as `If-Match`; a 412 throws [SectionConflictException] and
  /// keeps the edits (no merge, no retry).
  Future<void> save(ProfileApi api) {
    final previous = (_saveChain ?? Future<void>.value()).catchError(
      (Object _) {},
    );
    final run = previous.then((_) async {
      if (!state.canSave) await load(api);
      if (!state.canSave) throw SectionNotLoadedException(state.lastFailure);
      final sent = state.value as T;
      final gen = _generation;
      final Tagged<T> r;
      try {
        r = await send(api, sent, state.etag);
      } on DioException catch (e) {
        if (e.response?.statusCode == 412 && ref.mounted) {
          state = state.copyWith(conflict: true);
          throw const SectionConflictException();
        }
        rethrow;
      }
      if (ref.mounted && gen == _generation) {
        // Edits made while the PUT was in flight stay; the tag moves on.
        state = LoadedSection(
          value: identical(state.value, sent) ? r.value : state.value,
          source: SectionSource.rig,
          etag: r.etag,
        );
      }
    });
    _saveChain = run;
    return run;
  }
}
