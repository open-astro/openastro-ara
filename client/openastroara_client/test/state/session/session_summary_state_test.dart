import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/models/cursor_page.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/services/notifications_api.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/services/session_summary_prefs_service.dart';
import 'package:openastroara/services/ws_event_stream.dart';
import 'package:openastroara/state/notifications/notifications_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/session/session_summary_state.dart';
import 'package:openastroara/util/session_summary.dart';
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

/// The daemon's notification feed as the card reads it (fresh, not the
/// polled cache); can be made to fail like a poll that fired before Wi-Fi.
class _Feed implements NotificationsClient {
  _Feed(this.items, {this.fail = false});
  List<AraNotification> items;
  bool fail;
  int lists = 0;
  @override
  Future<CursorPage<AraNotification>> list({
    int limit = 50,
    String? cursor,
    bool? unreadOnly,
  }) async {
    lists++;
    if (fail) throw StateError('daemon unreachable');
    return CursorPage(items: items, nextCursor: null, hasMore: false);
  }

  @override
  Future<AraNotification?> markRead(String id) async => null;
  @override
  Future<AraNotification?> dismiss(String id, {String? reason}) async => null;
  @override
  void close() {}
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
  late _Feed feed;

  AraNotification shutdownRecord(DateTime at) => AraNotification(
    id: 'shutdown',
    postedUtc: at,
    severity: NotificationSeverity.warning,
    category: NotificationCategory.safety,
    title: 'Unattended shutdown executed',
    message: 'put to bed',
    read: false,
    dismissed: false,
    relatedEntityType: 'sequence',
    relatedEntityId: 'seq-1',
  );

  ProviderContainer make({
    List<SequenceListItem> list = _daemonList,
    List<AraNotification> inbox = const [],
    bool inboxFails = false,
  }) {
    link = StreamController<WsConnectionState>.broadcast();
    ws = StreamController<WsEvent>.broadcast();
    prefs = _MemPrefs();
    api = _RunStates({'seq-1': ended, 'seq-0': older}, list);
    feed = _Feed(inbox, fail: inboxFails);
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
        // The polled inbox cache is STALE too (empty since last evening).
        notificationInboxProvider.overrideWith(() => _Inbox(const [])),
        notificationsApiProvider.overrideWithValue(feed),
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
    await c
        .read(sessionSummaryProvider.notifier)
        .acknowledge(c.read(sessionSummaryProvider).asData!.value!);
    expect(c.read(sessionSummaryProvider).asData?.value, isNull);
    expect(prefs.acked, contains('r1:completed'));

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

  test('incidents come from the daemon feed, not the polled cache; a failed feed still yields the card', () async {
    final c = make(
      inbox: [shutdownRecord(start.add(const Duration(hours: 4)))],
    );
    await c.read(sessionSummaryProvider.future);
    link.add(WsConnectionState.connected);
    await settle();
    final s = c.read(sessionSummaryProvider).asData?.value;
    expect(s?.incidents.map((n) => n.title), ['Unattended shutdown executed']);
    expect(feed.lists, 1);

    final c2 = make(inboxFails: true);
    await c2.read(sessionSummaryProvider.future);
    link.add(WsConnectionState.connected);
    await settle();
    final s2 = c2.read(sessionSummaryProvider).asData?.value;
    expect(
      s2?.runId,
      'r1',
      reason: 'an unreadable inbox is "no incidents", not "no card"',
    );
    expect(s2?.incidents, isEmpty);
  });

  test('a run watched ending live is remembered across a restart (persisted as acknowledged)', () async {
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
    expect(prefs.acked, contains('r1:completed'));
  });

  test('dismissing a paused card does not swallow the same run\'s later unwatched failure', () async {
    final parked = SequenceRunStateInfo(
      sequenceId: 'seq-1',
      runId: 'r1',
      state: SequenceRunState.pausedAwaitingUser,
      currentTargetName: 'M81',
      startedUtc: start,
      framesCaptured: 20,
    );
    final c = make(
      list: const [
        SequenceListItem(
          id: 'seq-1',
          name: 'M81 night',
          currentRunState: SequenceRunState.pausedAwaitingUser,
        ),
      ],
    );
    api.byId['seq-1'] = parked;
    await c.read(sessionSummaryProvider.future);
    link.add(WsConnectionState.connected);
    await settle();
    expect(
      c.read(sessionSummaryProvider).asData?.value?.state,
      SequenceRunState.pausedAwaitingUser,
    );
    await c
        .read(sessionSummaryProvider.notifier)
        .acknowledge(c.read(sessionSummaryProvider).asData!.value!);
    expect(prefs.acked, contains('r1:pausedAwaitingUser'));

    // The user resumes r1 and leaves; it fails at 03:00; the morning connect:
    api.byId['seq-1'] = SequenceRunStateInfo(
      sequenceId: 'seq-1',
      runId: 'r1',
      state: SequenceRunState.failed,
      startedUtc: start,
      completedUtc: start.add(const Duration(hours: 5)),
      framesCaptured: 31,
    );
    api.listing = const [
      SequenceListItem(
        id: 'seq-1',
        name: 'M81 night',
        currentRunState: SequenceRunState.failed,
      ),
    ];
    reconnect();
    await settle();
    final s = c.read(sessionSummaryProvider).asData?.value;
    expect(
      s?.state,
      SequenceRunState.failed,
      reason: 'the night\'s failure is a new card',
    );
    expect(s?.framesCaptured, 31);
  });

  test('acknowledging a card keys on THAT card, even when the provider has moved on', () async {
    final c = make();
    await c.read(sessionSummaryProvider.future);
    link.add(WsConnectionState.connected);
    await settle();
    final shown = c.read(sessionSummaryProvider).asData!.value!;
    // A re-evaluation swapped in nothing (or another run) before the user tapped.
    final parked = SessionSummary(
      sequenceId: 'seq-9',
      runId: 'r9',
      sequenceName: 'Other',
      state: SequenceRunState.pausedAwaitingUser,
      endedUtc: now,
    );
    await c.read(sessionSummaryProvider.notifier).acknowledge(parked);
    expect(
      prefs.acked,
      contains('r9:pausedAwaitingUser'),
      reason: 'not runId:completed',
    );
    expect(
      c.read(sessionSummaryProvider).asData?.value?.runId,
      shown.runId,
      reason: 'a different card stays up',
    );
  });

  test(
    'a night on a sequence last edited weeks ago still gets its card',
    () async {
      final c = make(
        list: const [
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
        ],
      );
      await c.read(sessionSummaryProvider.future);
      link.add(WsConnectionState.connected);
      await settle();
      expect(
        api.reads,
        2,
        reason: 'every ended run is read; none is skipped on edit time',
      );
      expect(c.read(sessionSummaryProvider).asData?.value?.runId, 'r1');
    },
  );
}
