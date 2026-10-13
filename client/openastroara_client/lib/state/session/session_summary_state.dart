import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/draft_sequence.dart';
import '../../models/sequence/sequence_summary.dart';
import '../../services/session_summary_prefs_service.dart';
import '../../services/ws_event_stream.dart';
import '../../util/session_summary.dart';
import '../notifications/notifications_state.dart';
import '../sequencer/sequence_list_state.dart';
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
/// the WebSocket reaches `connected` (the morning reconnect): every sequence
/// the daemon lists with an ended or parked run is read, the newest one wins,
/// and it is shown unless this device already acknowledged it or watched it
/// end live. Null between showings.
class SessionSummaryNotifier extends AsyncNotifier<SessionSummary?> {
  Set<String>? _acknowledged;
  final _seenEndingLive = <String>{};
  bool _inFlight = false;

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
    // A run this client watched reach its end needs no morning card.
    ref.listen(wsEventsProvider, (prev, next) {
      final event = next.asData?.value;
      if (event == null || !event.type.startsWith(SequenceWsEvents.prefix)) {
        return;
      }
      final state = SequenceRunState.fromWire(event.payload['state']);
      final runId = event.payload['run_id'];
      if (runId is String && isSummaryWorthyState(state)) {
        _seenEndingLive.add(runId);
      }
    });
    return null;
  }

  Future<Set<String>> _ack() async => _acknowledged ??= await ref
      .read(sessionSummaryPrefsProvider)
      .loadAcknowledged();

  /// Read the daemon's ended runs and surface the newest unseen one.
  Future<void> _evaluate() async {
    if (_inFlight) return;
    _inFlight = true;
    try {
      final api = ref.read(sequenceApiProvider);
      if (api == null) return;
      final list =
          await ref.read(sequenceListProvider.future) ??
          const <SequenceListItem>[];
      final candidates = list
          .where(
            (s) =>
                !isDraftSequenceId(s.id) &&
                isSummaryWorthyState(s.currentRunState),
          )
          .take(8)
          .toList();
      if (candidates.isEmpty) return;
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
      if (newest == null || !ref.mounted) return;
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
      _inFlight = false;
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
