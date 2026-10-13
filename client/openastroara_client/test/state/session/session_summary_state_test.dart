import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/services/notifications_api.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/services/session_summary_prefs_service.dart';
import 'package:openastroara/services/ws_event_stream.dart';
import 'package:openastroara/state/notifications/notifications_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/session/session_summary_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';

/// Only list() and getRunState matter here; the rest of the (large) interface
/// is unused.
class _RunStates implements SequenceClient {
  _RunStates(this.byId, this.listing);
  final Map<String, SequenceRunStateInfo?> byId;
  List<SequenceListItem> listing;
  int reads = 0;
  int lists = 0;

  @override
  Future<SequencePage> list({int limit = 50, String? cursor}) async {
    lists++;
    return SequencePage(items: listing);
  }

  @override
  Future<SequenceRunStateInfo?> getRunState(String id) async {
    reads++;
    return byId[id];
  }

  @override
  dynamic noSuchMethod(Invocation invocation) =>
      throw UnimplementedError('${invocation.memberName}');
}

class _List extends SequenceListNotifier {
  _List(this.items);
  final List<SequenceListItem> items;
  @override
  Future<List<SequenceListItem>?> build() async => items;
}

class _Inbox extends NotificationInboxNotifier {
  _Inbox(this.items);
  final List<AraNotification> items;
  @override
  Future<List<AraNotification>?> build() async => items;
}

class _MemPrefs extends SessionSummaryPrefsService {
  Set<String> acked = {};
  @override
  Future<Set<String>> loadAcknowledged() async => acked;
  @override
  Future<void> saveAcknowledged(Set<String> ids) async => acked = ids;
}

const _daemonList = [
  SequenceListItem(
    id: 'seq-1',
    name: 'M81 night',
    currentRunState: SequenceRunState.completed,
  ),
  SequenceListItem(
    id: 'seq-0',
    name: 'Old',
    currentRunState: SequenceRunState.stopped,
  ),
  SequenceListItem(
    id: 'seq-2',
    name: 'Idle',
    currentRunState: SequenceRunState.idle,
  ),
];

void main() {
  final start = DateTime.utc(2026, 10, 12, 22);
  final now = start.add(const Duration(hours: 10));
  final ended = SequenceRunStateInfo(
    sequenceId: 'seq-1',
    runId: 'r1',
    state: SequenceRunState.completed,
    currentTargetName: 'M81',
    startedUtc: start,
    completedUtc: start.add(const Duration(hours: 5)),
    framesCaptured: 47,
  );
  final older = SequenceRunStateInfo(
    sequenceId: 'seq-0',
    runId: 'r0',
    state: SequenceRunState.stopped,
    startedUtc: start.subtract(const Duration(days: 2)),
    completedUtc: start.subtract(const Duration(hours: 40)),
  );

  late StreamController<WsConnectionState> link;
  late StreamController<WsEvent> ws;
  late _MemPrefs prefs;
  late _RunStates api;

  ProviderContainer make({
    List<SequenceListItem> list = _daemonList,
    List<AraNotification> inbox = const [],
  }) {
    link = StreamController<WsConnectionState>.broadcast();
    ws = StreamController<WsEvent>.broadcast();
    prefs = _MemPrefs();
    api = _RunStates({'seq-1': ended, 'seq-0': older}, list);
    final c = ProviderContainer(
      overrides: [
        wsConnectionStateProvider.overrideWith((ref) => link.stream),
        wsEventsProvider.overrideWith((ref) => ws.stream),
        sequenceApiProvider.overrideWith((ref) => api),
        // The cached list a tab loaded last evening: deliberately STALE (the
        // run still "running") — the card must not read it.
        sequenceListProvider.overrideWith(
          () => _List(const [
            SequenceListItem(
              id: 'seq-1',
              name: 'M81 night',
              currentRunState: SequenceRunState.running,
            ),
          ]),
        ),
        notificationInboxProvider.overrideWith(() => _Inbox(inbox)),
        sessionSummaryPrefsProvider.overrideWithValue(prefs),
        sessionSummaryClockProvider.overrideWithValue(() => now),
      ],
    );
    addTearDown(c.dispose);
    addTearDown(link.close);
    addTearDown(ws.close);
    c.listen(sessionSummaryProvider, (_, _) {});
    return c;
  }

  Future<void> settle() async {
    for (var i = 0; i < 8; i++) {
      await Future<void>.delayed(Duration.zero);
    }
  }

  /// A real morning reconnect: the link dropped overnight, then came back.
  void reconnect() {
    link.add(WsConnectionState.reconnecting);
    link.add(WsConnectionState.connected);
  }

  test('connecting surfaces the newest ended run from a FRESH list; idle sequences are never read', () async {
    final c = make();
    await c.read(sessionSummaryProvider.future);
    link.add(WsConnectionState.connected);
    await settle();
    final s = c.read(sessionSummaryProvider).asData?.value;
    expect(
      s,
      isNotNull,
      reason: 'the stale cached list said "running"; the daemon says completed',
    );
    expect(
      s!.runId,
      'r1',
      reason: 'newest end wins over the older stopped run',
    );
    expect(s.framesCaptured, 47);
    expect(api.lists, 1);
    expect(api.reads, 2, reason: 'only the two ended runs were read');
  });

  test('the list is re-read on every connect: a run that ended between connects gets its card', () async {
    final c = make(
      list: const [
        SequenceListItem(
          id: 'seq-1',
          name: 'M81 night',
          currentRunState: SequenceRunState.running,
        ),
      ],
    );
    await c.read(sessionSummaryProvider.future);
    link.add(WsConnectionState.connected);
    await settle();
    expect(
      c.read(sessionSummaryProvider).asData?.value,
      isNull,
      reason: 'still running at the first look',
    );

    api.listing = const [
      SequenceListItem(
        id: 'seq-1',
        name: 'M81 night',
        currentRunState: SequenceRunState.completed,
      ),
    ];
    reconnect();
    await settle();
    expect(c.read(sessionSummaryProvider).asData?.value?.runId, 'r1');
    expect(api.lists, 2);
  });

  test('acknowledging hides it now and on the next connect', () async {
    final c = make();
    await c.read(sessionSummaryProvider.future);
    link.add(WsConnectionState.connected);
    await settle();
    await c.read(sessionSummaryProvider.notifier).acknowledge('r1');
    expect(c.read(sessionSummaryProvider).asData?.value, isNull);
    expect(prefs.acked, contains('r1'));

    reconnect();
    await settle();
    expect(
      c.read(sessionSummaryProvider).asData?.value,
      isNull,
      reason: 'once per night',
    );
  });

  test('a run this client watched end live gets no morning card', () async {
    final c = make();
    await c.read(sessionSummaryProvider.future);
    ws.add(
      WsEvent(
        type: 'sequence.completed',
        ts: now,
        seq: 1,
        payload: const {
          'sequence_id': 'seq-1',
          'run_id': 'r1',
          'state': 'completed',
        },
      ),
    );
    await settle();
    link.add(WsConnectionState.connected);
    await settle();
    expect(c.read(sessionSummaryProvider).asData?.value, isNull);
  });

  test('a pause watched live and then resumed does not suppress a later unwatched end', () async {
    final c = make();
    await c.read(sessionSummaryProvider.future);
    ws.add(
      WsEvent(
        type: 'sequence.paused',
        ts: now,
        seq: 1,
        payload: const {
          'sequence_id': 'seq-1',
          'run_id': 'r1',
          'state': 'pausedawaitinguser',
        },
      ),
    );
    ws.add(
      WsEvent(
        type: 'sequence.resumed',
        ts: now,
        seq: 2,
        payload: const {
          'sequence_id': 'seq-1',
          'run_id': 'r1',
          'state': 'running',
        },
      ),
    );
    await settle();
    // ...the client goes away; the run ends; the morning connect:
    link.add(WsConnectionState.connected);
    await settle();
    expect(c.read(sessionSummaryProvider).asData?.value?.runId, 'r1');
  });

  test('a stale end (older than 36 h) is history, not a card', () async {
    final c = make(
      list: const [
        SequenceListItem(
          id: 'seq-0',
          name: 'Old',
          currentRunState: SequenceRunState.stopped,
        ),
      ],
    );
    await c.read(sessionSummaryProvider.future);
    link.add(WsConnectionState.connected);
    await settle();
    expect(c.read(sessionSummaryProvider).asData?.value, isNull);
  });
}
