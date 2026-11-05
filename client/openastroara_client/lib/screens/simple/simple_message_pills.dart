import '../../util/reduce_motion.dart';
import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../state/simple_messages_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import 'simple_preview_panel.dart' show SimpleTapTarget, kSimpleText;
import 'simple_shell.dart' show kSimpleInset, simpleFrost;

/// A pill's height when its words take one line (two lines grow it).
const double kSimpleMessageHeight = 48;

/// The message pills in the top-left corner (ALP-4825): newest on top, as
/// many as fit [maxHeight] (above the left rail), the rest folded into one
/// "+N more" pill that shows them all.
class SimpleMessagePills extends ConsumerStatefulWidget {
  const SimpleMessagePills({super.key, required this.maxHeight, this.onOpen});

  final double maxHeight;

  /// Opens what a message came from, on a tap (user, 2026-11-04).
  final ValueChanged<SimpleStatusOrigin>? onOpen;

  @override
  ConsumerState<SimpleMessagePills> createState() => _SimpleMessagePillsState();
}

class _SimpleMessagePillsState extends ConsumerState<SimpleMessagePills> {
  bool _all = false;

  // An error fades after a while on screen (user, 2026-11-04: not a sticky
  // pill); the timers live with the pills, so none outlives the screen.
  final _fades = <int, Timer>{};

  void _fadeErrors(List<SimpleMessage> messages) {
    final ids = {for (final m in messages) m.id};
    _fades.removeWhere((id, t) {
      if (ids.contains(id)) return false;
      t.cancel();
      return true;
    });
    for (final m in messages) {
      if (m.kind != SimpleMessageKind.error ||
          m.sticky ||
          _fades.containsKey(m.id)) {
        continue;
      }
      _fades[m.id] = Timer(
        kSimpleErrorLife,
        () => ref.read(simpleMessagesProvider.notifier).dismiss(m.id),
      );
    }
  }

  @override
  void dispose() {
    for (final t in _fades.values) {
      t.cancel();
    }
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final messages = ref.watch(simpleMessagesProvider);
    _fadeErrors(messages);
    if (messages.isEmpty) return const SizedBox.shrink();
    // Budgeted at one line a pill plus the gap; a two-line pill can push the
    // stack a few pixels past it.
    const step = kSimpleMessageHeight + AraSpace.s8;
    // No limit (the immersive view): every message shows.
    var fit = widget.maxHeight.isFinite
        ? ((widget.maxHeight + AraSpace.s8) / step).floor()
        : messages.length;
    if (fit < messages.length) fit = ((widget.maxHeight - 44) / step).floor();
    fit = fit.clamp(1, messages.length);
    final folded = !_all && fit < messages.length;
    final shown = folded ? messages.take(fit).toList() : messages;
    // No room under the newest pill for the fold (a short window, or under
    // the Night Setup row): the count rides at the end of that pill instead.
    final inline = folded && widget.maxHeight < step + 44;
    final more = messages.length - fit;
    void toggle() => setState(() => _all = !_all);
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      spacing: AraSpace.s8,
      children: [
        for (final m in shown)
          _Pill(
            key: ValueKey(m.id),
            message: m,
            more: inline && m == shown.first ? more : null,
            onMore: toggle,
            onOpen: m.origin == null || widget.onOpen == null
                ? null
                : () => widget.onOpen!(m.origin!),
          ),
        if ((folded && !inline) || (_all && messages.length > fit))
          Semantics(
            button: true,
            child: simpleFrost(
              Material(
              color: AraColors.overlayScrim,
              borderRadius: BorderRadius.circular(10),
              child: InkWell(
                key: const Key('simple-messages-more'),
                borderRadius: BorderRadius.circular(10),
                onTap: toggle,
                child: Container(
                  constraints: const BoxConstraints(minHeight: 44),
                  padding: const EdgeInsets.symmetric(horizontal: 14),
                  alignment: Alignment.center,
                  child: Text(
                    folded
                        ? context.l10n.pillsMore(more)
                        : context.l10n.pillsShowFewer,
                    style: kSimpleText.copyWith(color: AraColors.textSecondary),
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

class _Pill extends ConsumerWidget {
  const _Pill({
    super.key,
    required this.message,
    this.more,
    required this.onMore,
    this.onOpen,
  });

  final SimpleMessage message;

  /// Opens what it came from.
  final VoidCallback? onOpen;

  /// Messages folded behind this one, shown as "+N" at its end when there is
  /// no room for the fold below it.
  final int? more;
  final VoidCallback onMore;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final m = message;
    final notifier = ref.read(simpleMessagesProvider.notifier);
    final (dot, outline) = switch (m.kind) {
      SimpleMessageKind.result => (AraColors.accentConnected, null),
      SimpleMessageKind.error => (AraColors.accentError, AraColors.accentError),
      SimpleMessageKind.question => (
        AraColors.accentBusy,
        AraColors.accentBusy,
      ),
      SimpleMessageKind.warning => (
        AraColors.accentWarning,
        AraColors.accentWarning,
      ),
    };
    final reduced = reduceMotionOf(context);
    return Semantics(
      // Read out as it appears.
      liveRegion: true,
      container: true,
      button: onOpen != null,
      onTap: onOpen,
      child: SimpleTapTarget(
        onTap: onOpen,
        child: simpleFrost(
        Container(
        key: Key('simple-message-${m.id}'),
        constraints: const BoxConstraints(minHeight: kSimpleMessageHeight),
        padding: const EdgeInsets.only(left: 14, right: 4),
        decoration: BoxDecoration(
          color: AraColors.overlayScrim,
          borderRadius: BorderRadius.circular(10),
          border: Border.all(color: outline ?? Colors.transparent),
        ),
        child: Stack(
          children: [
            Row(
              spacing: 10,
              children: [
                Icon(Icons.circle, size: 10, color: dot),
                Expanded(
                  child: Padding(
                    padding: const EdgeInsets.symmetric(vertical: 6),
                    child: Text.rich(
                      TextSpan(
                        text: m.title,
                        children: [
                          if (m.detail != null) ...[
                            const TextSpan(
                              text: ' · ',
                              style: TextStyle(color: AraColors.textDisabled),
                            ),
                            TextSpan(
                              text: m.detail,
                              style: TextStyle(
                                color: m.kind == SimpleMessageKind.error
                                    ? AraColors.errorText
                                    : AraColors.textSecondary,
                              ),
                            ),
                          ],
                        ],
                      ),
                      // Whole, however long: an Emergency Stop's summary
                      // was cut at two lines (user, 2026-11-04).
                      style: kSimpleText,
                    ),
                  ),
                ),
                if (more != null)
                  TextButton(
                    key: const Key('simple-messages-more'),
                    onPressed: onMore,
                    child: Text(
                      context.l10n.pillsMoreShort(more!),
                      semanticsLabel: context.l10n.pillsMoreSemantics(more!),
                    ),
                  ),
                if (m.kind == SimpleMessageKind.error ||
                    m.kind == SimpleMessageKind.warning)
                  IconButton(
                    tooltip: context.l10n.pillsDismiss,
                    icon: const Icon(Icons.close, size: 18),
                    onPressed: () => notifier.dismiss(m.id),
                  ),
                if (m.kind == SimpleMessageKind.question)
                  Padding(
                    padding: const EdgeInsets.only(right: 4),
                    child: Row(
                      mainAxisSize: MainAxisSize.min,
                      spacing: 6,
                      children: [
                        for (var i = 0; i < m.actions.length; i++)
                          i == m.actions.length - 1
                              ? FilledButton(
                                  style: FilledButton.styleFrom(
                                    backgroundColor: AraColors.accentBusy,
                                    foregroundColor: AraColors.bgPrimary,
                                  ),
                                  onPressed: () => _answer(notifier, i),
                                  child: Text(m.actions[i].label),
                                )
                              : TextButton(
                                  onPressed: () => _answer(notifier, i),
                                  child: Text(m.actions[i].label),
                                ),
                      ],
                    ),
                  )
                else if (m.kind == SimpleMessageKind.result &&
                    m.actions.isNotEmpty)
                  // A result's one action (Undo), there while the pill lasts.
                  TextButton(
                    onPressed: () => _answer(notifier, 0),
                    child: Text(m.actions.first.label),
                  )
                else if (m.kind == SimpleMessageKind.result)
                  const SizedBox(width: kSimpleInset - 4),
              ],
            ),
            // A result's time left, as a line along its foot.
            if (m.kind == SimpleMessageKind.result && !reduced)
              Positioned(
                left: 0,
                right: 14,
                bottom: 4,
                child: TweenAnimationBuilder<double>(
                  tween: Tween(begin: 1, end: 0),
                  duration: kSimpleResultLife,
                  builder: (_, v, _) => Align(
                    alignment: Alignment.centerLeft,
                    child: FractionallySizedBox(
                      widthFactor: v,
                      child: Container(
                        height: 2,
                        color: AraColors.accentConnected.withValues(alpha: 0.6),
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
    );
  }

  void _answer(SimpleMessagesNotifier notifier, int i) {
    notifier.dismiss(message.id);
    message.actions[i].onPressed();
  }
}
