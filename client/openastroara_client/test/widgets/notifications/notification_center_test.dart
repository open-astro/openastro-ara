import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/cursor_page.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/notifications_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/notifications/notifications_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/widgets/notifications/notification_center.dart';

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

class _FakeClient implements NotificationsClient {
  _FakeClient(this.items);
  List<AraNotification> items;
  final List<String> dismissed = <String>[];

  @override
  Future<CursorPage<AraNotification>> list({
    int limit = 50,
    String? cursor,
    bool? unreadOnly,
  }) async =>
      CursorPage(items: items, nextCursor: null, hasMore: false);

  @override
  Future<AraNotification?> markRead(String id) async => null;

  @override
  Future<AraNotification?> dismiss(String id, {String? reason}) async {
    dismissed.add(id);
    items = items.where((n) => n.id != id).toList();
    return null;
  }

  @override
  void close() {}
}

AraNotification _n(String id,
        {required String title,
        bool read = false,
        Duration age = const Duration(minutes: 4)}) =>
    AraNotification(
      id: id,
      postedUtc: DateTime.now().subtract(age),
      severity: NotificationSeverity.warning,
      category: NotificationCategory.equipment,
      title: title,
      message: 'It could not find a star after three tries.',
      read: read,
      dismissed: false,
    );

Future<void> _pump(WidgetTester tester, _FakeClient client) async {
  await tester.pumpWidget(ProviderScope(
    overrides: [
      savedServerServiceProvider.overrideWithValue(
          _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
      notificationsApiFactoryProvider.overrideWithValue((_) => client),
    ],
    child: const MaterialApp(
      home: Scaffold(appBar: null, body: Center(child: NotificationBell())),
    ),
  ));
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('the bell stays quiet when nothing is waiting', (tester) async {
    await _pump(tester, _FakeClient(const []));
    expect(find.byIcon(Icons.notifications_none), findsOneWidget);
    expect(find.text('1'), findsNothing);
  });

  testWidgets('unread count rides on the bell', (tester) async {
    await _pump(
        tester,
        _FakeClient([
          _n('a', title: 'Guider gave up'),
          _n('b', title: 'Disk nearly full', read: true),
        ]));
    expect(find.text('1'), findsOneWidget);
  });

  testWidgets('an empty inbox says so instead of showing a blank list',
      (tester) async {
    await _pump(tester, _FakeClient(const []));
    await tester.tap(find.byType(IconButton));
    await tester.pumpAndSettle();
    expect(find.text("You're all caught up."), findsOneWidget);
  });

  testWidgets('opening the bell lists what happened, newest wording intact',
      (tester) async {
    await _pump(tester, _FakeClient([_n('a', title: 'Guider gave up')]));
    await tester.tap(find.byType(IconButton).first);
    await tester.pumpAndSettle();
    expect(find.text('Guider gave up'), findsOneWidget);
    expect(find.textContaining('Equipment · 4 minutes ago'), findsOneWidget);
  });

  testWidgets('dismiss appears when you reach for the row, not before',
      (tester) async {
    final client = _FakeClient([_n('a', title: 'Guider gave up')]);
    await _pump(tester, client);
    await tester.tap(find.byType(IconButton).first);
    await tester.pumpAndSettle();

    // At rest the row is just what happened — an unread dot, no controls.
    expect(find.byTooltip('Dismiss'), findsNothing);

    final mouse = await tester.createGesture(kind: PointerDeviceKind.mouse);
    await mouse.addPointer(location: Offset.zero);
    addTearDown(mouse.removePointer);
    await mouse.moveTo(tester.getCenter(find.text('Guider gave up')));
    await tester.pumpAndSettle();
    expect(find.byTooltip('Dismiss'), findsOneWidget);

    await tester.tap(find.byTooltip('Dismiss'));
    await tester.pumpAndSettle();
    expect(client.dismissed, ['a']);
    expect(find.text('Guider gave up'), findsNothing);
    expect(find.text("You're all caught up."), findsOneWidget);
  });

  testWidgets('a backlog reads as a timeline, not one undifferentiated wall',
      (tester) async {
    await _pump(
        tester,
        _FakeClient([
          _n('a', title: 'Guider gave up'),
          _n('b', title: 'Disk nearly full', age: const Duration(days: 1)),
        ]));
    await tester.tap(find.byType(IconButton).first);
    await tester.pumpAndSettle();
    expect(find.text('TODAY'), findsOneWidget);
    expect(find.text('YESTERDAY'), findsOneWidget);
  });

  test('relative time reads the way you would say it', () {
    final now = DateTime(2026, 8, 2, 22);
    expect(relativeTime(now.subtract(const Duration(seconds: 5)), now: now),
        'just now');
    expect(relativeTime(now.subtract(const Duration(minutes: 1)), now: now),
        '1 minute ago');
    expect(relativeTime(now.subtract(const Duration(minutes: 40)), now: now),
        '40 minutes ago');
    expect(relativeTime(now.subtract(const Duration(hours: 3)), now: now),
        '3 hours ago');
    expect(relativeTime(now.subtract(const Duration(days: 1)), now: now),
        'yesterday');
    expect(relativeTime(now.subtract(const Duration(days: 30)), now: now),
        '2026-07-03');
  });
}
