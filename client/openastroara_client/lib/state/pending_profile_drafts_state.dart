import 'dart:async';

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
/// saved fully offline and may go anywhere. A draft that applied is deleted; one
/// that failed stays for the next connect with its `savedProfileId`, so a retry
/// re-applies sections onto the profile it already created instead of orphaning
/// another — after checking that profile is still the ACTIVE one (another client
/// may have switched profiles since): it is re-selected when it still exists,
/// or the id is dropped and a fresh profile created when it is gone.
/// Returns (applied, failed, held) counts.
Future<(int applied, int failed, int held)> replayPendingProfileDrafts(
  PendingProfileDraftService store,
  ProfileApi api, {
  required String serverKey,
}) async {
  var applied = 0;
  var failed = 0;
  var held = 0;
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
      // Keep the draft, but persist the savedProfileId the attempt may have
      // stamped so the next replay re-uses that profile.
      debugPrint('[profile-drafts] replay of "${pending.name}" failed: $e');
      try {
        await store.save(pending);
      } catch (_) {}
      failed++;
    }
  }
  return (applied, failed, held);
}

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
      if ((await store.loadAll()).isEmpty) return;
      final (applied, failed, held) = await replayPendingProfileDrafts(
        store,
        api,
        serverKey: pendingDraftServerKey(server),
      );
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
