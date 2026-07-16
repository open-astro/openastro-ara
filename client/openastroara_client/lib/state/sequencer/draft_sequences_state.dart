import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/draft_sequence.dart';
import '../../services/draft_sequence_service.dart';
import 'sequence_list_state.dart';

final draftSequenceServiceProvider =
    Provider<DraftSequenceService>((ref) => DraftSequenceService());

/// §2/§28.9 offline planning — the locally-stored draft sequences (newest
/// first). Lives beside [sequenceListProvider]: that is the daemon's list,
/// this is the client-managed one; the Load dialog shows both.
class DraftSequencesNotifier extends AsyncNotifier<List<DraftSequence>> {
  @override
  Future<List<DraftSequence>> build() =>
      ref.watch(draftSequenceServiceProvider).loadAll();

  /// Create a new draft from a built body; returns its id. Throws on a failed
  /// disk write — the caller surfaces it.
  Future<String> create(String name, Map<String, dynamic> body,
      {String? pushKey}) async {
    final svc = ref.read(draftSequenceServiceProvider);
    final draft = DraftSequence(
      id: svc.newId(),
      name: name,
      updatedUtc: DateTime.now().toUtc(),
      body: body,
      pushKey: pushKey,
    );
    await svc.save(draft);
    await _reload();
    return draft.id;
  }

  /// Overwrite an existing draft's body (editor Save on a loaded draft).
  /// Unknown id is treated as create-with-that-id so a Save can't be dropped —
  /// deliberately, this RESURRECTS a draft that was deleted while still open
  /// in the editor: losing the deletion beats losing the user's edits.
  Future<void> saveBody(String id, Map<String, dynamic> body) async {
    final svc = ref.read(draftSequenceServiceProvider);
    final current = state.value ?? await svc.loadAll();
    DraftSequence? existing;
    for (final d in current) {
      if (d.id == id) {
        existing = d;
        break;
      }
    }
    await svc.save(DraftSequence(
      id: id,
      name: existing?.name ?? '',
      updatedUtc: DateTime.now().toUtc(),
      body: body,
      pushKey: existing?.pushKey,
    ));
    await _reload();
  }

  Future<void> delete(String id) async {
    await ref.read(draftSequenceServiceProvider).delete(id);
    await _reload();
  }

  /// Push a draft to the connected daemon as a real sequence, then delete the
  /// local copy. Returns the daemon's new sequence id. Throws on transport
  /// failure — the draft is only deleted after the create succeeded, so a
  /// failed push never loses the local plan.
  Future<String> push(String id) async {
    final api = ref.read(sequenceApiProvider);
    if (api == null) {
      throw StateError('Connect to a server before pushing a draft.');
    }
    DraftSequence? draft;
    for (final d in state.value ?? const <DraftSequence>[]) {
      if (d.id == id) {
        draft = d;
        break;
      }
    }
    if (draft == null) throw StateError('That draft no longer exists.');
    final newId = await api.create(
        draft.name.isEmpty ? 'Offline draft' : draft.name, draft.body,
        // Stable across retries: a push whose response was lost dedupes on
        // the daemon instead of duplicating. A degraded connected create's
        // original key wins (the daemon may have already applied it).
        idempotencyKey: draft.pushKey ?? draft.id);
    await ref.read(draftSequenceServiceProvider).delete(id);
    await _reload();
    // The daemon now owns it — surface it in the server list.
    ref.invalidate(sequenceListProvider);
    return newId;
  }

  Future<void> _reload() async {
    state = AsyncValue.data(
        await ref.read(draftSequenceServiceProvider).loadAll());
  }
}

final draftSequencesProvider =
    AsyncNotifierProvider<DraftSequencesNotifier, List<DraftSequence>>(
        DraftSequencesNotifier.new);
