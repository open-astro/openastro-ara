import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/draft_sequence.dart';
import '../../models/sequence/sequence_summary.dart';
import '../../services/session_summary_prefs_service.dart';
import '../../services/ws_event_stream.dart';
import '../../util/session_summary.dart';
import '../notifications/notifications_state.dart';
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
  Set<String>? _acknowledged;
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
      } else {
        _seenEndingLive.remove(runId);
      }
    });
    return null;
  }

  Future<Set<String>> _ack() async => _acknowledged ??= await ref
      .read(sessionSummaryPrefsProvider)
      .loadAcknowledged();

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
      final candidates = list
          .where(
            (s) =>
                !isDraftSequenceId(s.id) &&
                isSummaryWorthyState(s.currentRunState),
          )
          .take(8)
          .toList();
      if (candidates.isEmpty) {
        if (ref.mounted) state = const AsyncData(null);
        return;
      }
      final notifications =
          await ref.read(notificationInboxProvider.future) ?? const [];
      final summaries = <SessionSummary>[];
      for (final seq in candidates) {
        try {
          final run = await api.getRunState(seq.id);
          if (run == null) continue;
          final s = buildSessionSummary(
            sequence: seq,
            run: run,
            notifications: notifications,
          );
          if (s != null) summaries.add(s);
        } catch (e) {
          debugPrint('[session-summary] run state for ${seq.id} failed: $e');
        }
      }
      final newest = newestSummary(summaries);
      if (!ref.mounted) return;
      if (newest == null) {
        state = const AsyncData(null);
        return;
      }
      final show = shouldShowSessionSummary(
        newest,
        acknowledgedRunIds: await _ack(),
        seenEndingLive: _seenEndingLive,
        now: ref.read(sessionSummaryClockProvider)(),
      );
      if (ref.mounted) state = AsyncData(show ? newest : null);
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

  /// The user dismissed the card: never show this run again on this device.
  Future<void> acknowledge(String runId) async {
    final ack = await _ack()
      ..add(runId);
    if (ref.mounted && state.asData?.value?.runId == runId) {
      state = const AsyncData(null);
    }
    await ref.read(sessionSummaryPrefsProvider).saveAcknowledged(ack);
  }
}

final sessionSummaryProvider =
    AsyncNotifierProvider<SessionSummaryNotifier, SessionSummary?>(
      SessionSummaryNotifier.new,
    );
