import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/cursor_page.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/notifications_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/notifications/notifications_state.dart';
import 'package:openastroara/state/saved_server_state.dart';

class _FakeSavedServerService implements SavedServerService {
  _FakeSavedServerService(List<AraServer> stored) : _stored = [...stored];
  final List<AraServer> _stored;
  @override
  Future<List<AraServer>> loadAll() async => List.unmodifiable(_stored);
  @override
  Future<void> saveAll(List<AraServer> servers) async => _stored
    ..clear()
    ..addAll(servers);
  @override
  Future<void> add(AraServer server) async => _stored.add(server);
}

AraNotification _n(String id,
        {bool read = false,
        bool dismissed = false,
        NotificationSeverity severity = NotificationSeverity.info}) =>
    AraNotification(
      id: id,
      postedUtc: DateTime.utc(2026, 8, 2, 3),
      severity: severity,
      category: NotificationCategory.equipment,
      title: 'Guider gave up',
      message: 'It could not find a star after three tries.',
      read: read,
      dismissed: dismissed,
    );

class _FakeClient implements NotificationsClient {
  _FakeClient(this.items);

  List<AraNotification> items;
  final List<String> markedRead = <String>[];
  final List<String> dismissed = <String>[];
  bool failMutations = false;

  @override
  Future<CursorPage<AraNotification>> list({
    int limit = 50,
    String? cursor,
    bool? unreadOnly,
  }) async =>
      CursorPage(items: items, nextCursor: null, hasMore: false);

  @override
  Future<AraNotification?> markRead(String id) async {
    if (failMutations) throw StateError('nope');
    markedRead.add(id);
    return null;
  }

  @override
  Future<AraNotification?> dismiss(String id, {String? reason}) async {
    if (failMutations) throw StateError('nope');
    dismissed.add(id);
    return null;
  }

  @override
  void close() {}
}

ProviderContainer _container(_FakeClient client) {
  final container = ProviderContainer(overrides: [
    savedServerServiceProvider.overrideWithValue(
        _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
    notificationsApiFactoryProvider.overrideWithValue((_) => client),
  ]);
  addTearDown(container.dispose);
  return container;
}

void main() {
  test('dismissed entries never reach the inbox', () async {
    final client = _FakeClient([_n('a'), _n('b', dismissed: true)]);
    final container = _container(client);
    await container.read(savedServersProvider.future);
    final items = await container.read(notificationInboxProvider.future);
    expect(items!.map((n) => n.id), ['a']);
  });

  test('the badge counts only unread, and reddens on error or worse', () async {
    final client = _FakeClient([
      _n('a'),
      _n('b', read: true),
      _n('c', severity: NotificationSeverity.critical),
    ]);
    final container = _container(client);
    await container.read(savedServersProvider.future);
    await container.read(notificationInboxProvider.future);
    expect(container.read(unreadNotificationCountProvider), 2);
    expect(container.read(hasUrgentNotificationProvider), isTrue);
  });

  test('nothing unread and urgent means no alarm colour', () async {
    final client = _FakeClient([
      _n('a', read: true, severity: NotificationSeverity.critical),
    ]);
    final container = _container(client);
    await container.read(savedServersProvider.future);
    await container.read(notificationInboxProvider.future);
    expect(container.read(unreadNotificationCountProvider), 0);
    expect(container.read(hasUrgentNotificationProvider), isFalse);
  });

  test('marking read answers the tap before the server does', () async {
    final client = _FakeClient([_n('a'), _n('b')]);
    final container = _container(client);
    await container.read(savedServersProvider.future);
    await container.read(notificationInboxProvider.future);
    await container.read(notificationInboxProvider.notifier).markRead('a');
    expect(container.read(unreadNotificationCountProvider), 1);
    expect(client.markedRead, ['a']);
  });

  test('a failed dismiss puts the entry back instead of losing it', () async {
    final client = _FakeClient([_n('a'), _n('b')])..failMutations = true;
    final container = _container(client);
    await container.read(savedServersProvider.future);
    await container.read(notificationInboxProvider.future);
    await container.read(notificationInboxProvider.notifier).dismiss('a');
    expect(container.read(notificationInboxProvider).value!.map((n) => n.id),
        ['a', 'b'],
        reason: 'the server refused, so the inbox is unchanged');
  });

  test('mark all read clears the badge', () async {
    final client = _FakeClient([_n('a'), _n('b'), _n('c', read: true)]);
    final container = _container(client);
    await container.read(savedServersProvider.future);
    await container.read(notificationInboxProvider.future);
    // The server keeps serving the original page, so a real refresh would
    // undo this; the fake mirrors what a real server returns after the calls.
    client.items = [_n('a', read: true), _n('b', read: true), _n('c', read: true)];
    await container.read(notificationInboxProvider.notifier).markAllRead();
    expect(client.markedRead, containsAll(<String>['a', 'b']));
    expect(client.markedRead, isNot(contains('c')));
    expect(container.read(unreadNotificationCountProvider), 0);
  });

  test('severity and category survive the wire', () {
    final parsed = AraNotification.fromJson(const {
      'id': 'x',
      'posted_utc': '2026-08-02T03:00:00Z',
      'severity': 'critical',
      'category': 'safety',
      'title': 'Clouds rolled in',
      'message': 'The run was parked.',
      'read': false,
      'dismissed': false,
    });
    expect(parsed.severity, NotificationSeverity.critical);
    expect(parsed.category, NotificationCategory.safety);
    expect(parsed.postedUtc.isUtc, isFalse, reason: 'shown in local time');
  });

  test('an unknown severity from a newer server degrades to info', () {
    final parsed = AraNotification.fromJson(const {
      'id': 'x',
      'posted_utc': '2026-08-02T03:00:00Z',
      'severity': 'apocalyptic',
      'category': 'wormhole',
      'title': 't',
      'message': 'm',
    });
    expect(parsed.severity, NotificationSeverity.info);
    expect(parsed.category, NotificationCategory.software);
  });
}
