import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/server.dart';
import '../screens/wizard/wizard_save.dart';
import '../services/pending_profile_draft_service.dart';
import '../services/profile_api.dart';
import '../services/ws_event_stream.dart';
import 'profile_management_state.dart';
import 'saved_server_state.dart';
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

/// The key a draft is pinned to: the server's host and port.
String pendingDraftServerKey(AraServer server) =>
    '${server.hostname}:${server.port}';

/// Replay every pending draft meant for [serverKey] through the SAME
/// `saveWizardProfile` the connected wizard uses. A draft saved against another
/// server is HELD (its half-created profile lives there, and its sections must
/// not land on this rig's active profile); a draft with no server recorded was
/// saved fully offline and may go anywhere. A draft that applied is deleted.
///
/// A failure is retried ONLY when it was a transport failure (the daemon never
/// answered): the draft stays with its `savedProfileId`, so the next connect
/// re-applies the sections onto the profile it already created instead of
/// orphaning another — after checking that profile is still the ACTIVE one
/// (another client may have switched profiles since): it is re-selected when it
/// still exists, or the id is dropped and a fresh profile created when it is
/// gone. Anything else (a 4xx, an older daemon missing an endpoint, a bad
/// value) is a REJECTION: retrying would fail the same way on every reconnect
/// and re-select a profile the user may have moved away from, so the draft is
/// dropped and the notice says so. The profile it did create stays on the rig
/// to be finished from Setup.
/// Returns (applied, failed, held, rejected) counts.
Future<(int applied, int failed, int held, int rejected)>
    replayPendingProfileDrafts(
  PendingProfileDraftService store,
  ProfileApi api, {
  required String serverKey,
}) async {
  var applied = 0;
  var failed = 0;
  var held = 0;
  var rejected = 0;
  for (final pending in await store.loadAll()) {
    if (pending.serverKey != null && pending.serverKey != serverKey) {
      held++;
      continue;
    }
    try {
      final saved = pending.draft.savedProfileId;
      if (saved != null) {
        final profiles = await api.listProfiles();
        if (profiles.activeId != saved) {
          if (profiles.profiles.any((p) => p.id == saved)) {
            await api.selectProfile(saved);
          } else {
            pending.draft.savedProfileId = null; // gone: start a fresh profile
          }
        }
      }
      await saveWizardProfile(api, pending.draft);
      await store.delete(pending.id);
      applied++;
    } catch (e) {
      if (isProfileReplayTransportFailure(e)) {
        // Keep the draft, but persist the savedProfileId the attempt may have
        // stamped so the next replay re-uses that profile — and once a profile
        // EXISTS on this rig, pin the draft here: a draft saved with no server
        // (serverKey null) that half-landed on rig A must not create a fresh
        // profile on rig B next time and leave A's orphan behind.
        debugPrint('[profile-drafts] replay of "${pending.name}" failed in transit: $e');
        final pinned = pending.serverKey == null && pending.draft.savedProfileId != null
            ? PendingProfileDraft(
                id: pending.id,
                name: pending.name,
                savedUtc: pending.savedUtc,
                draft: pending.draft,
                finalSave: pending.finalSave,
                serverKey: serverKey,
              )
            : pending;
        try {
          await store.save(pinned);
        } catch (_) {}
        failed++;
      } else {
        debugPrint('[profile-drafts] replay of "${pending.name}" rejected, dropping it: $e');
        try {
          await store.delete(pending.id);
        } catch (_) {}
        rejected++;
      }
    }
  }
  return (applied, failed, held, rejected);
}

/// True when the daemon never answered: every failed section had no response,
/// or the single request that failed (create, list, select) got none. A reply
/// with any status code is a rejection, not a transport failure.
bool isProfileReplayTransportFailure(Object e) =>
    e is ProfileSaveTransportFailure ||
    (e is DioException && e.response == null);

/// Root-mounted: when the active server's WebSocket reaches `connected`, apply
/// whatever drafts are waiting for THAT server. Listening on the link (not just
/// "a server is saved") is what makes this a replay on RECONNECT — a
/// saved-but-unreachable rig at the dark site stays pending until the drive home.
final pendingProfileDraftReplayProvider = Provider<void>((ref) {
  var inFlight = false;
  ref.listen<AsyncValue<WsConnectionState>>(wsConnectionStateProvider, (
    prev,
    next,
  ) async {
    if (next.asData?.value != WsConnectionState.connected || inFlight) return;
    final api = ref.read(profileApiProvider);
    final server = ref.read(activeServerProvider);
    if (api == null || server == null) return;
    // Claim before the first await: two quick `connected` emissions must not
    // both start a replay and create the profile twice.
    inFlight = true;
    try {
      final store = ref.read(pendingProfileDraftServiceProvider);
      final (applied, failed, held, rejected) =
          await replayPendingProfileDrafts(
        store,
        api,
        serverKey: pendingDraftServerKey(server),
      );
      if (applied + failed + held + rejected == 0) return; // nothing pending
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
        if (held > 0)
          '$held profile${held == 1 ? '' : 's'} saved offline '
              'belong${held == 1 ? 's' : ''} to a different rig and will be '
              'applied when you connect to it.',
        if (rejected > 0)
          '$rejected profile${rejected == 1 ? '' : 's'} saved offline '
              '${rejected == 1 ? 'was' : 'were'} rejected by the rig and '
              'discarded; finish ${rejected == 1 ? 'it' : 'them'} from Setup.',
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
