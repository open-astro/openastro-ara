import 'dart:ui' show ImageFilter;

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
///
/// Everything here moves: the panel grows out of the bell rather than
/// appearing, rows fade as they leave rather than snapping, and the badge
/// settles into its new number. None of it is decoration — motion is what
/// tells you *what just happened* without a word of explanation.

/// Panel geometry. One place, so the open animation and the layout can't drift.
const double _panelWidth = 400;
const double _panelMaxHeight = 540;
const Duration _openDuration = Duration(milliseconds: 220);
const Duration _rowLeaveDuration = Duration(milliseconds: 180);

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
        // The badge grows in rather than blinking on, and slides to its new
        // number — a count that changes silently is a count you don't notice.
        Positioned(
          right: 3,
          top: 5,
          child: IgnorePointer(
            child: AnimatedScale(
              scale: unread > 0 ? 1 : 0,
              duration: const Duration(milliseconds: 220),
              curve: Curves.easeOutBack,
              child: AnimatedContainer(
                duration: const Duration(milliseconds: 220),
                curve: Curves.easeOut,
                padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 1),
                constraints: const BoxConstraints(minWidth: 16),
                decoration: BoxDecoration(
                  color: urgent ? AraColors.accentError : AraColors.accentInfo,
                  borderRadius: BorderRadius.circular(8),
                ),
                child: AnimatedSwitcher(
                  duration: const Duration(milliseconds: 180),
                  transitionBuilder: (child, anim) => FadeTransition(
                    opacity: anim,
                    child: SizeTransition(
                        sizeFactor: anim, axis: Axis.vertical, child: child),
                  ),
                  child: Text(
                    unread > 99 ? '99+' : '$unread',
                    key: ValueKey<int>(unread),
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
          ),
        ),
      ],
    );
  }
}

/// Opens the inbox as a panel anchored under the top bar, growing out of the
/// corner it was opened from the way a menu does — not landing on top of the
/// app like a dialog.
Future<void> showNotificationCenter(BuildContext context) =>
    showGeneralDialog<void>(
      context: context,
      barrierDismissible: true,
      barrierLabel: 'Close notifications',
      barrierColor: Colors.black.withValues(alpha: 0.12),
      transitionDuration: _openDuration,
      pageBuilder: (_, _, _) => const _NotificationCenter(),
      transitionBuilder: (_, animation, _, child) {
        final eased =
            CurvedAnimation(parent: animation, curve: Curves.easeOutCubic);
        return FadeTransition(
          opacity: eased,
          child: ScaleTransition(
            scale: Tween<double>(begin: 0.94, end: 1).animate(eased),
            alignment: Alignment.topRight,
            child: child,
          ),
        );
      },
    );

class _NotificationCenter extends ConsumerWidget {
  const _NotificationCenter();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(notificationInboxProvider);
    final unread = ref.watch(unreadNotificationCountProvider);
    return Align(
      alignment: Alignment.topRight,
      child: Padding(
        padding: const EdgeInsets.only(top: 50, right: 10),
        child: ClipRRect(
          borderRadius: BorderRadius.circular(12),
          child: BackdropFilter(
            // The app stays faintly visible behind the panel, so it reads as
            // something laid over your session rather than a new place.
            filter: ImageFilter.blur(sigmaX: 18, sigmaY: 18),
            child: Container(
              width: _panelWidth,
              constraints: const BoxConstraints(maxHeight: _panelMaxHeight),
              decoration: BoxDecoration(
                color: AraColors.bgPanel.withValues(alpha: 0.92),
                borderRadius: BorderRadius.circular(12),
                border: Border.all(
                    color: AraColors.border.withValues(alpha: 0.8), width: 0.5),
                boxShadow: const [
                  BoxShadow(
                      color: Color(0x66000000), blurRadius: 28, offset: Offset(0, 8)),
                ],
              ),
              child: Material(
                type: MaterialType.transparency,
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    _Header(unread: unread),
                    Flexible(
                      child: AnimatedSize(
                        duration: const Duration(milliseconds: 180),
                        curve: Curves.easeOut,
                        alignment: Alignment.topCenter,
                        child: async.when(
                          loading: () => const SizedBox(
                              height: 170,
                              child: Center(
                                  child: SizedBox(
                                      width: 22,
                                      height: 22,
                                      child: CircularProgressIndicator(
                                          strokeWidth: 2)))),
                          error: (e, _) => _Empty(
                            icon: Icons.cloud_off,
                            title:
                                friendlyError(e, action: 'load your notifications'),
                          ),
                          data: (items) => _Body(items: items),
                        ),
                      ),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _Header extends ConsumerWidget {
  const _Header({required this.unread});

  final int unread;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 13, 8, 11),
      child: Row(
        children: [
          Expanded(
            child: Text('Notifications',
                style: theme.textTheme.titleMedium
                    ?.copyWith(fontWeight: FontWeight.w600, letterSpacing: -0.2)),
          ),
          // Appears only when there is something to mark, and fades rather
          // than popping into the row.
          AnimatedOpacity(
            opacity: unread > 0 ? 1 : 0,
            duration: const Duration(milliseconds: 180),
            child: IgnorePointer(
              ignoring: unread == 0,
              child: TextButton(
                onPressed: () =>
                    ref.read(notificationInboxProvider.notifier).markAllRead(),
                style: TextButton.styleFrom(
                  padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                  minimumSize: Size.zero,
                  tapTargetSize: MaterialTapTargetSize.shrinkWrap,
                  textStyle: theme.textTheme.bodySmall,
                  foregroundColor: AraColors.accentInfo,
                ),
                child: const Text('Mark all read'),
              ),
            ),
          ),
          const SizedBox(width: 2),
          IconButton(
            icon: const Icon(Icons.close, size: 15),
            tooltip: 'Close',
            visualDensity: VisualDensity.compact,
            color: AraColors.textSecondary,
            onPressed: () => Navigator.of(context).pop(),
          ),
        ],
      ),
    );
  }
}

class _Body extends ConsumerWidget {
  const _Body({required this.items});

  final List<AraNotification>? items;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final list = items;
    if (list == null) {
      return const _Empty(
        icon: Icons.cloud_off,
        title: 'Connect to your rig to see its notifications.',
      );
    }
    if (list.isEmpty) {
      return const _Empty(
        icon: Icons.check_circle_outline,
        title: "You're all caught up.",
        detail: 'Anything Ara decides on its own — a guider that gave up, a '
            'run that stopped early — waits for you here.',
      );
    }
    final truncated = ref.read(notificationInboxProvider.notifier).truncated;
    // Day headings the way you'd say them, so a long backlog reads as a
    // timeline instead of one undifferentiated wall.
    final rows = <Widget>[];
    String? lastDay;
    for (final n in list) {
      final day = _dayLabel(n.postedUtc);
      if (day != lastDay) {
        rows.add(_DayHeading(label: day));
        lastDay = day;
      }
      rows.add(_Row(key: ValueKey<String>(n.id), item: n));
    }
    if (truncated) {
      rows.add(Padding(
        padding: const EdgeInsets.fromLTRB(16, 12, 16, 16),
        child: Text(
          'Showing the newest ${list.length}. Older ones are still on your rig.',
          style: theme.textTheme.bodySmall
              ?.copyWith(color: AraColors.textDisabled),
        ),
      ));
    }
    return ListView(
      shrinkWrap: true,
      padding: const EdgeInsets.only(bottom: 6),
      children: rows,
    );
  }
}

String _dayLabel(DateTime when, {DateTime? now}) {
  final today = now ?? DateTime.now();
  final day = DateTime(when.year, when.month, when.day);
  final delta = DateTime(today.year, today.month, today.day).difference(day).inDays;
  if (delta <= 0) return 'Today';
  if (delta == 1) return 'Yesterday';
  if (delta < 7) return _weekdays[day.weekday - 1];
  return '${_months[day.month - 1]} ${day.day}';
}

const _weekdays = [
  'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday',
];
const _months = [
  'January', 'February', 'March', 'April', 'May', 'June',
  'July', 'August', 'September', 'October', 'November', 'December',
];

class _DayHeading extends StatelessWidget {
  const _DayHeading({required this.label});

  final String label;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.fromLTRB(16, 14, 16, 4),
        child: Text(
          label.toUpperCase(),
          style: Theme.of(context).textTheme.labelSmall?.copyWith(
                color: AraColors.textDisabled,
                fontSize: 10,
                letterSpacing: 0.8,
                fontWeight: FontWeight.w600,
              ),
        ),
      );
}

/// One entry. Hovering reveals the dismiss control — at rest the row is just
/// what happened. Dismissing collapses the row instead of making the list jump.
class _Row extends ConsumerStatefulWidget {
  const _Row({super.key, required this.item});

  final AraNotification item;

  @override
  ConsumerState<_Row> createState() => _RowState();
}

class _RowState extends ConsumerState<_Row> {
  bool _hovered = false;
  bool _leaving = false;

  Future<void> _dismiss() async {
    setState(() => _leaving = true);
    // Let the collapse play out before the list rebuilds without this row.
    await Future<void>.delayed(_rowLeaveDuration);
    if (!mounted) return;
    await ref.read(notificationInboxProvider.notifier).dismiss(widget.item.id);
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final item = widget.item;
    return AnimatedSize(
      duration: _rowLeaveDuration,
      curve: Curves.easeInOut,
      child: AnimatedOpacity(
        opacity: _leaving ? 0 : 1,
        duration: _rowLeaveDuration,
        child: _leaving
            ? const SizedBox(width: double.infinity)
            : MouseRegion(
                onEnter: (_) => setState(() => _hovered = true),
                onExit: (_) => setState(() => _hovered = false),
                child: GestureDetector(
                  onTap: item.read
                      ? null
                      : () => ref
                          .read(notificationInboxProvider.notifier)
                          .markRead(item.id),
                  child: AnimatedContainer(
                    duration: const Duration(milliseconds: 140),
                    margin: const EdgeInsets.symmetric(horizontal: 8, vertical: 1),
                    padding: const EdgeInsets.fromLTRB(10, 9, 6, 9),
                    decoration: BoxDecoration(
                      color: _hovered
                          ? AraColors.textSecondary.withValues(alpha: 0.08)
                          : Colors.transparent,
                      borderRadius: BorderRadius.circular(8),
                    ),
                    child: Row(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Padding(
                          padding: const EdgeInsets.only(top: 1),
                          child: Icon(_icon(item.severity),
                              size: 15, color: _color(item.severity)),
                        ),
                        const SizedBox(width: 10),
                        Expanded(
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Text(
                                item.title,
                                style: theme.textTheme.bodyMedium?.copyWith(
                                  height: 1.25,
                                  fontWeight: item.read
                                      ? FontWeight.w400
                                      : FontWeight.w600,
                                ),
                              ),
                              if (item.message.isNotEmpty) ...[
                                const SizedBox(height: 2),
                                Text(item.message,
                                    style: theme.textTheme.bodySmall?.copyWith(
                                        height: 1.35,
                                        color: AraColors.textSecondary)),
                              ],
                              const SizedBox(height: 3),
                              Text(
                                '${_categoryLabel(item.category)} · ${relativeTime(item.postedUtc)}',
                                style: theme.textTheme.bodySmall?.copyWith(
                                    fontSize: 11,
                                    color: AraColors.textDisabled),
                              ),
                            ],
                          ),
                        ),
                        // Unread marker and dismiss share one slot: the dot is
                        // what you see at rest, the X is what you get when you
                        // reach for the row.
                        SizedBox(
                          width: 26,
                          child: Center(
                            child: AnimatedSwitcher(
                              duration: const Duration(milliseconds: 140),
                              child: _hovered
                                  ? IconButton(
                                      key: const ValueKey('dismiss'),
                                      icon: const Icon(Icons.close, size: 13),
                                      tooltip: 'Dismiss',
                                      padding: EdgeInsets.zero,
                                      constraints: const BoxConstraints(
                                          minWidth: 24, minHeight: 24),
                                      visualDensity: VisualDensity.compact,
                                      color: AraColors.textSecondary,
                                      onPressed: _dismiss,
                                    )
                                  : item.read
                                      ? const SizedBox(
                                          key: ValueKey('none'), width: 8)
                                      : Container(
                                          key: const ValueKey('unread'),
                                          width: 7,
                                          height: 7,
                                          decoration: const BoxDecoration(
                                            color: AraColors.accentInfo,
                                            shape: BoxShape.circle,
                                          ),
                                        ),
                            ),
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
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
      padding: const EdgeInsets.fromLTRB(30, 34, 30, 40),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(icon, size: 26, color: AraColors.textDisabled),
          const SizedBox(height: 12),
          Text(title,
              textAlign: TextAlign.center,
              style: theme.textTheme.bodyLarge
                  ?.copyWith(color: AraColors.textSecondary, height: 1.35)),
          if (detail != null) ...[
            const SizedBox(height: 7),
            Text(detail!,
                textAlign: TextAlign.center,
                style: theme.textTheme.bodySmall
                    ?.copyWith(color: AraColors.textDisabled, height: 1.4)),
          ],
        ],
      ),
    );
  }
}
