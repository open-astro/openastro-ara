import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/notifications_api.dart';
import '../../state/notifications/notifications_state.dart';
import '../../theme/ara_colors.dart';
import '../../util/friendly_error.dart';

/// §46 — the inbox the app never had. Things Ara decides on its own overnight
/// (the guider gave up, a fault reaction fired, the sequence stopped early)
/// were only ever visible to whoever happened to be watching the right screen.
/// The bell keeps them until you read them.

/// Top-bar bell with an unread badge. Silent when there is nothing waiting —
/// no badge, no colour, just the outline icon.
class NotificationBell extends ConsumerWidget {
  const NotificationBell({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final unread = ref.watch(unreadNotificationCountProvider);
    final urgent = ref.watch(hasUrgentNotificationProvider);
    return Stack(
      clipBehavior: Clip.none,
      children: [
        IconButton(
          icon: Icon(unread > 0 ? Icons.notifications : Icons.notifications_none,
              size: 18),
          tooltip: unread == 0
              ? 'Notifications'
              : unread == 1
                  ? '1 unread notification'
                  : '$unread unread notifications',
          onPressed: () => showNotificationCenter(context),
        ),
        if (unread > 0)
          Positioned(
            right: 4,
            top: 6,
            child: IgnorePointer(
              child: Container(
                padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 1),
                constraints: const BoxConstraints(minWidth: 15),
                decoration: BoxDecoration(
                  color: urgent ? AraColors.accentError : AraColors.accentInfo,
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Text(
                  unread > 99 ? '99+' : '$unread',
                  textAlign: TextAlign.center,
                  style: const TextStyle(
                      fontSize: 9,
                      height: 1.3,
                      fontWeight: FontWeight.w600,
                      color: Colors.white),
                ),
              ),
            ),
          ),
      ],
    );
  }
}

/// Opens the inbox as a panel anchored under the top bar, the way a menu
/// drops from the thing that opened it rather than taking over the screen.
Future<void> showNotificationCenter(BuildContext context) => showDialog<void>(
      context: context,
      barrierColor: Colors.transparent,
      builder: (_) => const _NotificationCenter(),
    );

class _NotificationCenter extends ConsumerWidget {
  const _NotificationCenter();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final async = ref.watch(notificationInboxProvider);
    final unread = ref.watch(unreadNotificationCountProvider);
    return Align(
      alignment: Alignment.topRight,
      child: Padding(
        padding: const EdgeInsets.only(top: 52, right: 12),
        child: Material(
          color: AraColors.bgPanel,
          elevation: 12,
          borderRadius: BorderRadius.circular(10),
          child: Container(
            width: 420,
            constraints: const BoxConstraints(maxHeight: 520),
            decoration: BoxDecoration(
              border: Border.all(color: AraColors.border),
              borderRadius: BorderRadius.circular(10),
            ),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                Padding(
                  padding: const EdgeInsets.fromLTRB(16, 12, 8, 8),
                  child: Row(
                    children: [
                      Expanded(
                        child: Text('Notifications',
                            style: theme.textTheme.titleMedium),
                      ),
                      if (unread > 0)
                        TextButton(
                          onPressed: () => ref
                              .read(notificationInboxProvider.notifier)
                              .markAllRead(),
                          style: TextButton.styleFrom(
                            padding: const EdgeInsets.symmetric(horizontal: 8),
                            minimumSize: Size.zero,
                            tapTargetSize: MaterialTapTargetSize.shrinkWrap,
                            textStyle: theme.textTheme.bodySmall,
                          ),
                          child: const Text('Mark all read'),
                        ),
                      IconButton(
                        icon: const Icon(Icons.close, size: 16),
                        tooltip: 'Close',
                        onPressed: () => Navigator.of(context).pop(),
                      ),
                    ],
                  ),
                ),
                const Divider(height: 1),
                Flexible(
                  child: async.when(
                    loading: () => const SizedBox(
                        height: 160,
                        child: Center(child: CircularProgressIndicator())),
                    error: (e, _) => _Empty(
                      icon: Icons.cloud_off,
                      title: friendlyError(e, action: 'load your notifications'),
                    ),
                    data: (items) {
                      if (items == null) {
                        return const _Empty(
                          icon: Icons.cloud_off,
                          title: 'Connect to your rig to see its notifications.',
                        );
                      }
                      if (items.isEmpty) {
                        return const _Empty(
                          icon: Icons.check_circle_outline,
                          title: "You're all caught up.",
                          detail: 'Anything Ara decides on its own — a guider '
                              'that gave up, a run that stopped early — waits '
                              'for you here.',
                        );
                      }
                      return ListView.separated(
                        shrinkWrap: true,
                        padding: EdgeInsets.zero,
                        itemCount: items.length,
                        separatorBuilder: (_, _) => const Divider(height: 1),
                        itemBuilder: (_, i) => _Row(item: items[i]),
                      );
                    },
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _Row extends ConsumerWidget {
  const _Row({required this.item});

  final AraNotification item;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final notifier = ref.read(notificationInboxProvider.notifier);
    return InkWell(
      onTap: item.read ? null : () => notifier.markRead(item.id),
      child: Container(
        color: item.read ? null : AraColors.accentInfo.withValues(alpha: 0.06),
        padding: const EdgeInsets.fromLTRB(14, 10, 6, 10),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Padding(
              padding: const EdgeInsets.only(top: 2),
              child: Icon(_icon(item.severity),
                  size: 16, color: _color(item.severity)),
            ),
            const SizedBox(width: 10),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    item.title,
                    style: theme.textTheme.bodyMedium?.copyWith(
                        fontWeight:
                            item.read ? FontWeight.w400 : FontWeight.w600),
                  ),
                  if (item.message.isNotEmpty) ...[
                    const SizedBox(height: 2),
                    Text(item.message,
                        style: theme.textTheme.bodySmall
                            ?.copyWith(color: AraColors.textSecondary)),
                  ],
                  const SizedBox(height: 4),
                  Text(
                    '${_categoryLabel(item.category)} · ${relativeTime(item.postedUtc)}',
                    style: theme.textTheme.bodySmall
                        ?.copyWith(color: AraColors.textDisabled),
                  ),
                ],
              ),
            ),
            IconButton(
              icon: const Icon(Icons.close, size: 14),
              tooltip: 'Dismiss',
              color: AraColors.textDisabled,
              onPressed: () => notifier.dismiss(item.id),
            ),
          ],
        ),
      ),
    );
  }

  static IconData _icon(NotificationSeverity s) => switch (s) {
        NotificationSeverity.info => Icons.info_outline,
        NotificationSeverity.warning => Icons.warning_amber_outlined,
        NotificationSeverity.error => Icons.error_outline,
        NotificationSeverity.critical => Icons.dangerous_outlined,
      };

  static Color _color(NotificationSeverity s) => switch (s) {
        NotificationSeverity.info => AraColors.accentInfo,
        NotificationSeverity.warning => AraColors.accentBusy,
        NotificationSeverity.error ||
        NotificationSeverity.critical =>
          AraColors.accentError,
      };

  static String _categoryLabel(NotificationCategory c) => switch (c) {
        NotificationCategory.equipment => 'Equipment',
        NotificationCategory.sequence => 'Run',
        NotificationCategory.storage => 'Storage',
        NotificationCategory.software => 'Ara',
        NotificationCategory.safety => 'Safety',
        NotificationCategory.alarm => 'Alarm',
      };
}

/// "4 minutes ago" beats a timestamp when you're reading the night back.
/// Falls back to a date once it stops being about tonight.
String relativeTime(DateTime when, {DateTime? now}) {
  final delta = (now ?? DateTime.now()).difference(when);
  if (delta.isNegative || delta.inSeconds < 45) return 'just now';
  if (delta.inMinutes < 60) {
    final m = delta.inMinutes;
    return m == 1 ? '1 minute ago' : '$m minutes ago';
  }
  if (delta.inHours < 24) {
    final h = delta.inHours;
    return h == 1 ? '1 hour ago' : '$h hours ago';
  }
  if (delta.inDays < 7) {
    final d = delta.inDays;
    return d == 1 ? 'yesterday' : '$d days ago';
  }
  String two(int n) => n.toString().padLeft(2, '0');
  return '${when.year}-${two(when.month)}-${two(when.day)}';
}

class _Empty extends StatelessWidget {
  const _Empty({required this.icon, required this.title, this.detail});

  final IconData icon;
  final String title;
  final String? detail;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 28, vertical: 36),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(icon, size: 26, color: AraColors.textDisabled),
          const SizedBox(height: 12),
          Text(title,
              textAlign: TextAlign.center,
              style: theme.textTheme.bodyLarge
                  ?.copyWith(color: AraColors.textSecondary)),
          if (detail != null) ...[
            const SizedBox(height: 6),
            Text(detail!,
                textAlign: TextAlign.center,
                style: theme.textTheme.bodySmall
                    ?.copyWith(color: AraColors.textDisabled)),
          ],
        ],
      ),
    );
  }
}
