import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/draft_sequence.dart';
import '../../models/sequence/sequence_summary.dart';
import '../../services/session_summary_prefs_service.dart';
import '../../services/ws_event_stream.dart';
import '../../util/session_summary.dart';
import '../../services/notifications_api.dart';
import '../notifications/notifications_state.dart'
    show notificationsApiProvider;
import '../sequencer/sequence_list_state.dart'
    show SequenceWsEvents, sequenceApiProvider;
import '../ws/ws_providers.dart';

final sessionSummaryPrefsProvider = Provider<SessionSummaryPrefsService>(
  (ref) => SessionSummaryPrefsService(),
);

/// Test seam: "now" for the recency window.
final sessionSummaryClockProvider = Provider<DateTime Function()>(
  (ref) =>
      () => DateTime.now().toUtc(),
);

/// §58.13 / #1138 — the overnight card to show, or null. Recomputed each time
/// the WebSocket reaches `connected` (the morning reconnect): the sequence
/// list is read FRESH from the daemon (a tab's cached list still says what
/// the run was doing last evening), every sequence with an ended or parked
/// run is read, the newest one wins, and it is shown unless this device
/// already acknowledged it or watched it end live. Null between showings.
class SessionSummaryNotifier extends AsyncNotifier<SessionSummary?> {
  Future<Set<String>>? _acknowledged;
  final _seenEndingLive = <String>{};
  bool _inFlight = false;
  bool _rerun = false;

  @override
  Future<SessionSummary?> build() async {
    ref.listen<AsyncValue<WsConnectionState>>(wsConnectionStateProvider, (
      prev,
      next,
    ) {
      if (next.asData?.value == WsConnectionState.connected) {
        unawaited(_evaluate());
      }
    });
    // A run this client watched reach its end (or get parked) needs no
    // morning card — until it runs again: a pause the user resumed is not the
    // end of the night, so a later unwatched failure of the same run still
    // earns a card.
    ref.listen(wsEventsProvider, (prev, next) {
      final event = next.asData?.value;
      if (event == null || !event.type.startsWith(SequenceWsEvents.prefix)) {
        return;
      }
      final state = SequenceRunState.fromWire(event.payload['state']);
      final runId = event.payload['run_id'];
      if (runId is! String || state == null) return;
      if (isSummaryWorthyState(state)) {
        _seenEndingLive.add(runId);
        // A run watched reaching a TERMINAL state is persisted as seen: a
        // restart before the morning must not resurrect its card. A pause is
        // only remembered in memory — the user may resume it and leave.
        if (state != SequenceRunState.pausedAwaitingUser) {
          unawaited(_persistSeen(SessionSummary.ackKeyFor(runId, state)));
        }
      } else {
        _seenEndingLive.remove(runId);
      }
    });
    return null;
  }

  // The load FUTURE is cached, not its result: two overlapping first calls
  // (a terminal event's _persistSeen racing _evaluate) must share one set.
  Future<Set<String>> _ack() => _acknowledged ??= ref
      .read(sessionSummaryPrefsProvider)
      .loadAcknowledged();

  Future<void> _persistSeen(String ackKey) async {
    final ack = await _ack()
      ..add(ackKey);
    await ref.read(sessionSummaryPrefsProvider).saveAcknowledged(ack);
  }

  /// The inbox straight from the daemon: the cached notifier only refreshes on
  /// its poll, so right after a reconnect it still holds last evening's list
  /// (and an `AsyncError` from a poll that fired before Wi-Fi came back). A
  /// failed read means "no incidents", never "no card".
  Future<List<AraNotification>> _freshInbox() async {
    final api = ref.read(notificationsApiProvider);
    if (api == null) return const [];
    try {
      // One newest-first page: the card keeps the 6 newest incidents, so a
      // noisy night's earliest warnings falling off the page is by design.
      return (await api.list(limit: 100)).items;
    } catch (e) {
      debugPrint('[session-summary] inbox read failed: $e');
      return const [];
    }
  }

  /// Read the daemon's ended runs and surface the newest unseen one. A
  /// connect that lands mid-evaluation re-runs it once after, so the fresher
  /// look is never dropped.
  Future<void> _evaluate() async {
    if (_inFlight) {
      _rerun = true;
      return;
    }
    _inFlight = true;
    // Hold the autoDispose API alive for the whole read.
    final keep = ref.listen(sequenceApiProvider, (_, _) {});
    try {
      final api = ref.read(sequenceApiProvider);
      if (api == null) return;
      final list = (await api.list(limit: 50)).items;
      // Every sequence with an ended or parked run is read (the list is at
      // most 50 rows and the per-id read is cheap): choosing by the list's
      // edit time would miss a night on a sequence last edited weeks ago.
      final candidates = list
          .where(
            (s) =>
                !isDraftSequenceId(s.id) &&
                isSummaryWorthyState(s.currentRunState),
          )
          .toList();
      if (candidates.isEmpty) {
        if (ref.mounted) state = const AsyncData(null);
        return;
      }
      final notifications = await _freshInbox();
      // All the per-id reads at once: a connect on a large library should not
      // wait for 50 sequential round-trips.
      final summaries = (await Future.wait(
        candidates.map((seq) async {
          try {
            final run = await api.getRunState(seq.id);
            if (run == null) return null;
            return buildSessionSummary(
              sequence: seq,
              run: run,
              notifications: notifications,
            );
          } catch (e) {
            debugPrint('[session-summary] run state for ${seq.id} failed: $e');
            return null;
          }
        }),
      )).whereType<SessionSummary>().toList();
      // The newest UNSEEN one: filter first, then pick, so a seen newer run
      // never hides an older end the user has not been told about.
      final ack = await _ack();
      final now = ref.read(sessionSummaryClockProvider)();
      final unseen = summaries.where(
        (s) => shouldShowSessionSummary(
          s,
          acknowledgedKeys: ack,
          seenEndingLive: _seenEndingLive,
          now: now,
        ),
      );
      if (ref.mounted) state = AsyncData(newestSummary(unseen));
    } catch (e) {
      debugPrint('[session-summary] evaluation failed: $e');
    } finally {
      keep.close();
      _inFlight = false;
      if (_rerun) {
        _rerun = false;
        unawaited(_evaluate());
      }
    }
  }

  /// Test / manual hook: evaluate now (the listener normally waits for connect).
  Future<void> evaluateNow() => _evaluate();

  /// The user dismissed the card: never show this run IN THIS STATE again on
  /// this device (a resumed run that later fails is a new card).
  Future<void> acknowledge(SessionSummary summary) async {
    final ack = await _ack()
      ..add(summary.ackKey);
    // Clear only the card being dismissed: a re-evaluation may have swapped
    // in the same run in a later state, which is a different card.
    if (ref.mounted && state.asData?.value?.ackKey == summary.ackKey) {
      state = const AsyncData(null);
    }
    await ref.read(sessionSummaryPrefsProvider).saveAcknowledged(ack);
  }
}

final sessionSummaryProvider =
    AsyncNotifierProvider<SessionSummaryNotifier, SessionSummary?>(
      SessionSummaryNotifier.new,
    );
