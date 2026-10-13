import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../screens/wizard/wizard_save.dart';
import '../services/pending_profile_draft_service.dart';
import '../services/profile_api.dart';
import '../services/ws_event_stream.dart';
import 'profile_management_state.dart';
import 'ws/ws_providers.dart';

/// §2 / #1137 — wizard drafts saved on this device because no daemon was in
/// reach, and their replay the moment one is.
final pendingProfileDraftServiceProvider = Provider<PendingProfileDraftService>(
  (ref) => PendingProfileDraftService(),
);

/// The drafts waiting to be applied, oldest first.
class PendingProfileDraftsNotifier
    extends AsyncNotifier<List<PendingProfileDraft>> {
  @override
  Future<List<PendingProfileDraft>> build() =>
      ref.read(pendingProfileDraftServiceProvider).loadAll();

  Future<void> add(PendingProfileDraft draft) async {
    await ref.read(pendingProfileDraftServiceProvider).save(draft);
    await refresh();
  }

  Future<void> remove(String id) async {
    await ref.read(pendingProfileDraftServiceProvider).delete(id);
    await refresh();
  }

  Future<void> refresh() async {
    state = AsyncData(
      await ref.read(pendingProfileDraftServiceProvider).loadAll(),
    );
  }
}

final pendingProfileDraftsProvider =
    AsyncNotifierProvider<
      PendingProfileDraftsNotifier,
      List<PendingProfileDraft>
    >(PendingProfileDraftsNotifier.new);

/// Outcome of the last replay, for the shell to surface once (null = nothing
/// to say). Cleared by [PendingProfileReplayNoticeNotifier.consume].
class PendingProfileReplayNoticeNotifier extends Notifier<String?> {
  @override
  String? build() => null;

  void post(String message) => state = message;

  String? consume() {
    final m = state;
    state = null;
    return m;
  }
}

final pendingProfileReplayNoticeProvider =
    NotifierProvider<PendingProfileReplayNoticeNotifier, String?>(
      PendingProfileReplayNoticeNotifier.new,
    );

/// Replay every pending draft through the SAME `saveWizardProfile` the
/// connected wizard uses. A draft that applied is deleted; one that failed
/// stays for the next connect (its `savedProfileId` is kept, so a retry
/// re-applies sections onto the profile it already created instead of
/// orphaning another). Returns (applied, failed) counts.
Future<(int applied, int failed)> replayPendingProfileDrafts(
  PendingProfileDraftService store,
  ProfileApi api,
) async {
  var applied = 0;
  var failed = 0;
  for (final pending in await store.loadAll()) {
    try {
      await saveWizardProfile(api, pending.draft);
      await store.delete(pending.id);
      applied++;
    } catch (e) {
      // Keep the draft, but persist the savedProfileId the attempt may have
      // stamped so the next replay re-uses that profile.
      debugPrint('[profile-drafts] replay of "${pending.name}" failed: $e');
      try {
        await store.save(pending);
      } catch (_) {}
      failed++;
    }
  }
  return (applied, failed);
}

/// Root-mounted: when the active server's WebSocket reaches `connected`, apply
/// whatever drafts are waiting. Listening on the link (not just "a server is
/// saved") is what makes this a replay on RECONNECT — a saved-but-unreachable
/// rig at the dark site stays pending until the drive home.
final pendingProfileDraftReplayProvider = Provider<void>((ref) {
  var inFlight = false;
  ref.listen<AsyncValue<WsConnectionState>>(wsConnectionStateProvider, (
    prev,
    next,
  ) async {
    if (next.asData?.value != WsConnectionState.connected || inFlight) return;
    final api = ref.read(profileApiProvider);
    if (api == null) return;
    final store = ref.read(pendingProfileDraftServiceProvider);
    final pending = await store.loadAll();
    if (pending.isEmpty) return;
    inFlight = true;
    try {
      final (applied, failed) = await replayPendingProfileDrafts(store, api);
      if (applied > 0) {
        ref.invalidate(profileManagementProvider);
      }
      unawaited(ref.read(pendingProfileDraftsProvider.notifier).refresh());
      final parts = <String>[
        if (applied > 0)
          '$applied profile${applied == 1 ? '' : 's'} saved offline '
              '${applied == 1 ? 'was' : 'were'} applied to the rig.',
        if (failed > 0)
          '$failed profile${failed == 1 ? '' : 's'} saved offline could not be '
              'applied yet and will be retried on the next connect.',
      ];
      if (parts.isNotEmpty) {
        ref
            .read(pendingProfileReplayNoticeProvider.notifier)
            .post(parts.join(' '));
      }
    } finally {
      inFlight = false;
    }
  });
});
