import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../services/notifications_api.dart';
import '../saved_server_state.dart';

/// §46 notification inbox. The server has posted these all along — a guider
/// that gave up, a fault reaction, a sequence that stopped — and until now the
/// only way to read one was to be looking at the right screen when it
/// happened. This is where they wait for you.

/// Overridable in tests, mirroring `faultsApiFactoryProvider`.
final notificationsApiFactoryProvider =
    Provider<NotificationsClient Function(AraServer)>(
        (ref) => (server) => NotificationsApi(server));

final notificationsApiProvider = Provider<NotificationsClient?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(notificationsApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// How often the badge re-checks. The server pushes no notification event, so
/// this is the only thing keeping the bell honest; a minute is often enough
/// for something that already happened and slow enough to be invisible.
const Duration kNotificationPollInterval = Duration(minutes: 1);

/// Newest-first, dismissed entries excluded. Null = no server bound.
class NotificationInboxNotifier extends AsyncNotifier<List<AraNotification>?> {
  int _gen = 0;

  @override
  Future<List<AraNotification>?> build() async {
    final api = ref.watch(notificationsApiProvider);
    if (api == null) return null;
    final timer = Timer.periodic(kNotificationPollInterval, (_) => refresh());
    ref.onDispose(timer.cancel);
    final gen = ++_gen;
    final page = await api.list(limit: 50);
    if (gen != _gen) return state.value;
    return _visible(page.items);
  }

  static List<AraNotification> _visible(List<AraNotification> all) =>
      all.where((n) => !n.dismissed).toList(growable: false);

  Future<void> refresh() async {
    final api = ref.read(notificationsApiProvider);
    if (api == null) return;
    final gen = ++_gen;
    final next = await AsyncValue.guard(
        () async => _visible((await api.list(limit: 50)).items));
    if (ref.mounted && gen == _gen) state = next;
  }

  /// Mark one read. Applied locally first so the badge answers the tap
  /// immediately; the server is the one that makes it stick, and a failure
  /// puts the dot back rather than lying about it.
  Future<void> markRead(String id) => _mutate(
        id,
        optimistic: (n) => n.copyWith(read: true),
        call: (api) => api.markRead(id),
      );

  /// Dismiss one — it leaves the list entirely.
  Future<void> dismiss(String id) => _mutate(
        id,
        optimistic: null,
        call: (api) => api.dismiss(id),
      );

  /// Mark every unread entry read. Nothing to undo, so it just re-reads the
  /// list at the end rather than tracking each call.
  Future<void> markAllRead() async {
    final api = ref.read(notificationsApiProvider);
    final current = state.value;
    if (api == null || current == null) return;
    final unread = current.where((n) => !n.read).toList();
    if (unread.isEmpty) return;
    if (ref.mounted) {
      state = AsyncData(
          current.map((n) => n.read ? n : n.copyWith(read: true)).toList());
    }
    try {
      await Future.wait(unread.map((n) => api.markRead(n.id)));
    } catch (_) {
      // Whatever actually landed, the server knows — ask it.
    }
    await refresh();
  }

  Future<void> _mutate(
    String id, {
    required AraNotification? Function(AraNotification)? optimistic,
    required Future<AraNotification?> Function(NotificationsClient) call,
  }) async {
    final api = ref.read(notificationsApiProvider);
    final before = state.value;
    if (api == null || before == null) return;
    final after = <AraNotification>[
      for (final n in before)
        if (n.id != id)
          n
        else if (optimistic != null)
          optimistic(n)!,
    ];
    state = AsyncData(after);
    try {
      await call(api);
    } catch (_) {
      // Put it back exactly as it was — a failed dismiss that silently
      // vanished from the list would lose the entry until the next poll.
      if (ref.mounted) state = AsyncData(before);
    }
  }
}

final notificationInboxProvider =
    AsyncNotifierProvider<NotificationInboxNotifier, List<AraNotification>?>(
        NotificationInboxNotifier.new);

/// How many are waiting to be read — what the bell shows.
final unreadNotificationCountProvider = Provider<int>((ref) {
  final inbox = ref.watch(notificationInboxProvider).value;
  if (inbox == null) return 0;
  return inbox.where((n) => !n.read).length;
});

/// True when anything unread is an error or worse, so the bell can say
/// "something needs you" rather than just "something happened".
final hasUrgentNotificationProvider = Provider<bool>((ref) {
  final inbox = ref.watch(notificationInboxProvider).value;
  if (inbox == null) return false;
  return inbox.any((n) =>
      !n.read && n.severity.index >= NotificationSeverity.error.index);
});
