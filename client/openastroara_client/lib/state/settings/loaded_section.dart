import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/profile_api.dart';
import '../profile_management_state.dart' show profileApiProvider;

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
  T? _held; // The rig's copy (last load or save); see [replaceAndSave].

  /// Replace the section with the rig's copy (also discards edits and a
  /// conflict). A failure is kept in [LoadedSection.lastFailure]; a section
  /// that was loaded stays as it was. Returns whether the load succeeded.
  Future<bool> load(ProfileApi api) async {
    final gen = ++_generation;
    try {
      final r = await fetch(api);
      if (ref.mounted && gen == _generation) {
        _held = r.value;
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

  /// Same as [load], for a screen that reads the rig's copy again.
  Future<void> hydrateFromServer(ProfileApi api) => load(api);

  /// Live sync (`profile.updated`): after any save in flight lands, fetch the
  /// rig's copy and take it only when its tag differs from the one held here.
  /// The announcement of this device's own save then changes nothing (edits
  /// made since stay), while a write by the daemon or another device in the
  /// same moments still lands. A save started while the fetch ran answers
  /// with the newer tag itself, so the fetched copy is dropped.
  Future<void> syncFromServer(ProfileApi api) async {
    await (_saveChain ?? Future<void>.value()).catchError((Object _) {});
    if (!ref.mounted) return;
    final gen = _generation;
    final chain = _saveChain;
    try {
      final r = await fetch(api);
      if (!ref.mounted || gen != _generation || !identical(chain, _saveChain)) {
        return;
      }
      if (state.canSave && r.etag != null && r.etag == state.etag) return;
      _generation++;
      _held = r.value;
      state = LoadedSection(
        value: r.value,
        source: SectionSource.rig,
        etag: r.etag,
      );
    } catch (e) {
      if (ref.mounted && gen == _generation) {
        state = state.copyWith(lastFailure: e);
      }
    }
  }

  /// Apply an edit; ignored unless the value came from the rig. An offline
  /// cache seed is read-only: an edit on it would be replaced by the retry-load
  /// of the next Save, which would then report Saved (ADR 0015).
  void edit(T Function(T current) change) {
    final v = state.value;
    if (v == null || !state.canSave) return;
    state = state.copyWith(value: change(v));
  }

  /// Offline planning: show the cache's copy of the section (readable, never
  /// saved). A copy from the rig, or a load still in flight, is never replaced.
  void seed(T value) {
    if (state.canSave) return;
    state = LoadedSection(value: value, source: SectionSource.cache);
  }

  ProfileApi _activeApi() =>
      ref.read(profileApiProvider) ??
      (throw StateError('Not connected — connect to your rig to save this.'));

  /// Save [next] whole (a picker, a form). A 412 keeps [next] and flags the
  /// conflict (ADR 0015 rule 6). Any other failure puts the rig's copy back,
  /// unless a later edit already replaced [next]: that edit's save decides.
  /// A [next] made from the offline cache copy is refused with nothing sent:
  /// the cache's other fields must not overwrite the rig's.
  Future<void> replaceAndSave(T next) async {
    if (state.source == SectionSource.cache) {
      throw SectionNotLoadedException(state.lastFailure);
    }
    try {
      await editAndSave((_) => next);
    } on SectionConflictException {
      rethrow;
    } catch (_) {
      final held = _held;
      if (held != null && identical(state.value, next)) edit((_) => held);
      rethrow;
    }
  }

  /// An auto-save row (a switch that saves on change): edit, then save.
  /// Not loaded from the rig: load once more first, as [save] does, so one
  /// failed load does not refuse every later change. Throws
  /// [SectionNotLoadedException] without editing when that fails too, so a
  /// refused toggle never shows as set.
  Future<void> editAndSave(T Function(T current) change) async {
    if (!state.canSave) await _loadOnce();
    if (!state.canSave) throw SectionNotLoadedException(state.lastFailure);
    edit(change);
    return save(_activeApi());
  }

  /// A Simple Mode row that saves each change: [change] applied to the rig's
  /// copy (loaded once more first when it isn't in) and saved whole, so a
  /// failed save puts the rig's copy back. A 412 reloads the rig's copy: the
  /// row has no edits worth keeping, and the newer copy shows at once.
  Future<void> changeAndSave(T Function(T current) change) async {
    if (!state.canSave) await _loadOnce();
    final v = state.value;
    if (v == null || !state.canSave) {
      throw SectionNotLoadedException(state.lastFailure);
    }
    try {
      await replaceAndSave(change(v));
    } on SectionConflictException {
      final api = ref.mounted ? ref.read(profileApiProvider) : null;
      if (api != null) await load(api);
      rethrow;
    }
  }

  /// The rig's copy for a dialog that forks a draft from it (the guider's
  /// Tune Guiding and setup wizard), never an unsaved edit: loaded once more
  /// first when it isn't in, as [save] does; null when that fails too.
  Future<T?> rigCopy() async {
    if (!state.canSave) await _loadOnce();
    return state.canSave ? (_held ?? state.value) : null;
  }

  /// One switch beside a form (the permanent pier next to the site fields):
  /// save [change] applied to the rig's copy alone, so edits not saved yet are
  /// not sent, then apply it to the shown value too, which keeps those edits.
  /// Loaded once more first when not in; refused, nothing sent, when that
  /// fails. A 412 flags the conflict and keeps the edits, as [save] does.
  Future<void> saveOnRigCopy(T Function(T current) change) async {
    if (!state.canSave) await _loadOnce();
    if (!state.canSave) throw SectionNotLoadedException(state.lastFailure);
    final api = _activeApi();
    final previous = (_saveChain ?? Future<void>.value()).catchError(
      (Object _) {},
    );
    final run = previous.then((_) async {
      // A copy from the rig that no load here produced (a notifier built
      // with it) is its own rig copy.
      final held = _held ?? state.value;
      if (held == null || !state.canSave) {
        throw SectionNotLoadedException(state.lastFailure);
      }
      final gen = _generation;
      final r = await _send(api, change(held), state.etag);
      if (ref.mounted && gen == _generation) {
        _held = r.value;
        final shown = state.value as T;
        state = LoadedSection(
          value: identical(shown, held) ? r.value : change(shown),
          source: SectionSource.rig,
          etag: r.etag,
        );
      }
    });
    _saveChain = run;
    return run;
  }

  /// A rig action that writes the section by a route of its own (the safety
  /// first-flip re-arm) and answers the rig's new copy with its tag. Runs after
  /// a save in flight; loaded once more first when not in, refused with
  /// nothing sent when that fails. [apply] puts the action's change on the
  /// shown copy, so edits not saved yet stay. The answered tag is kept only
  /// when [onlyAction] finds the rig's new copy is the one loaded here with
  /// that change; otherwise another device saved in between, so the old tag
  /// stays and the conflict is flagged: the next Save is refused as stale
  /// instead of writing over that copy.
  Future<void> actOnRig(
    Future<Tagged<T>> Function(ProfileApi api) action, {
    required T Function(T shown, T rig) apply,
    required bool Function(T loaded, T rig) onlyAction,
  }) async {
    if (!state.canSave) await _loadOnce();
    if (!state.canSave) throw SectionNotLoadedException(state.lastFailure);
    final api = _activeApi();
    final previous = (_saveChain ?? Future<void>.value()).catchError(
      (Object _) {},
    );
    final run = previous.then((_) async {
      final held = _held ?? state.value;
      if (held == null || !state.canSave) {
        throw SectionNotLoadedException(state.lastFailure);
      }
      final gen = _generation;
      final r = await action(api);
      if (!ref.mounted || gen != _generation) return;
      final shown = state.value as T;
      if (onlyAction(held, r.value)) {
        _held = r.value;
        state = LoadedSection(
          value: identical(shown, held) ? r.value : apply(shown, r.value),
          source: SectionSource.rig,
          etag: r.etag,
        );
      } else {
        state = LoadedSection(
          value: apply(shown, r.value),
          source: SectionSource.rig,
          etag: state.etag,
          conflict: true,
        );
      }
    });
    _saveChain = run;
    return run;
  }

  // A 412 flags the conflict (the shown value stays) and throws
  // [SectionConflictException]; any other failure is rethrown as it is.
  Future<Tagged<T>> _send(ProfileApi api, T value, String? etag) async {
    try {
      return await send(api, value, etag);
    } on DioException catch (e) {
      if (e.response?.statusCode == 412 && ref.mounted) {
        state = state.copyWith(conflict: true);
        throw const SectionConflictException();
      }
      rethrow;
    }
  }

  // Runs only when not loaded: a loaded section edits at once (no await).
  // Gone while the load ran: refused, nothing sent.
  // A load that a newer one superseded (a self-loading section's own first
  // load, started as the notifier was built) lands nothing: load once more.
  Future<void> _loadOnce() async {
    final api = ref.read(profileApiProvider);
    for (var i = 0; i < 2 && api != null && !state.canSave; i++) {
      if (!await load(api) || !ref.mounted) break;
    }
    if (!ref.mounted) throw const SectionNotLoadedException(null);
  }

  /// Back to not loaded (rig or profile changed).
  void reset() {
    _generation++;
    _held = null;
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
      final r = await _send(api, sent, state.etag);
      if (ref.mounted && gen == _generation) {
        _held = r.value;
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

/// A section read outside Settings (no panel opens it first): loads itself
/// once the notifier is built and whenever it holds no copy from the rig and
/// no failure, such as after the reset point (migrated_sections.dart), when the
/// rig connects, or over an offline cache seed once the rig is there. A failed
/// load stays failed until Retry or the next Save.
abstract class SelfLoadingSection<T> extends SectionNotifier<T> {
  @override
  LoadedSection<T> build() {
    listenSelf((_, next) => _loadIfDue(next));
    ref.listen(profileApiProvider, (_, _) => _loadIfDue(state));
    Future.microtask(() => ref.mounted ? _loadIfDue(state) : null);
    return const LoadedSection();
  }

  void _loadIfDue(LoadedSection<T> now) {
    final api = ref.read(profileApiProvider);
    if (api != null &&
        now.source != SectionSource.rig &&
        now.lastFailure == null) {
      load(api);
    }
  }
}
