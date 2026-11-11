import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../state/simple_messages_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../util/reduce_motion.dart';
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
    // the Preflight row): the count rides at the end of that pill instead.
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
              shape: RoundedRectangleBorder(
                side: const BorderSide(color: AraColors.border),
                borderRadius: BorderRadius.circular(AraRadius.simplePanel),
              ),
              child: InkWell(
                key: const Key('simple-messages-more'),
                borderRadius: BorderRadius.circular(AraRadius.simplePanel),
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
      SimpleMessageKind.refused => (
        AraColors.accentWarning,
        AraColors.accentWarning,
      ),
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
    // A warning stays and can run long: its × and dot keep to its first line,
    // and its action goes under the words so they keep the width.
    final warning = m.kind == SimpleMessageKind.warning;
    final question = m.kind == SimpleMessageKind.question;
    // A question stacks too: its words take the pill's width, its answers go
    // underneath (#1745).
    final stacked = warning || question;
    final actionBelow = stacked && m.actions.isNotEmpty;
    // A warning's first line grows with the text size; its dot and × follow.
    final line = TextPainter(
      text: TextSpan(
        text: 'Ag',
        style: DefaultTextStyle.of(context).style.merge(kSimpleText),
      ),
      textScaler: MediaQuery.textScalerOf(context),
      textDirection: TextDirection.ltr,
    )..layout();
    final firstLineCentre = 14 + line.height / 2;
    line.dispose();
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
          borderRadius: BorderRadius.circular(AraRadius.simplePanel),
          border: Border.all(color: outline ?? AraColors.border),
        ),
        child: Stack(
          children: [
            Row(
              spacing: 10,
              crossAxisAlignment: stacked
                  ? CrossAxisAlignment.start
                  : CrossAxisAlignment.center,
              children: [
                Padding(
                  padding: EdgeInsets.only(top: stacked ? firstLineCentre - 5 : 0),
                  child: Icon(Icons.circle, size: 10, color: dot),
                ),
                Expanded(
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Padding(
                    // A warning's first line sits level with its ×.
                    padding: EdgeInsets.only(top: stacked ? 14 : 6, bottom: 6),
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
                      if (question && actionBelow)
                        Padding(
                          padding: const EdgeInsets.only(right: 10, bottom: 10),
                          child: _QuestionAnswers(
                            labels: [for (final a in m.actions) a.label],
                            onAnswer: (i) => _answer(notifier, i),
                          ),
                        )
                      else if (actionBelow)
                        Wrap(
                          children: [
                            for (var i = 0; i < m.actions.length; i++)
                              TextButton(
                                onPressed: () => _answer(notifier, i),
                                // Shrinks in a narrow pill rather than breaking the word.
                                child: FittedBox(
                                  fit: BoxFit.scaleDown,
                                  child: Text(m.actions[i].label, softWrap: false),
                                ),
                              ),
                          ],
                        ),
                    ],
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
                  Padding(
                    padding: EdgeInsets.only(
                      top: warning ? math.max(0, firstLineCentre - 24) : 0,
                    ),
                    child: IconButton(
                      tooltip: context.l10n.pillsDismiss,
                      icon: const Icon(Icons.close, size: 18),
                      onPressed: () => notifier.dismiss(m.id),
                    ),
                  ),
                if (m.actions.isNotEmpty && !actionBelow)
                  // A message's one action (Undo), there while the pill lasts.
                  TextButton(
                    onPressed: () => _answer(notifier, 0),
                    child: Text(m.actions.first.label),
                  )
                else if (m.kind == SimpleMessageKind.result ||
                    m.kind == SimpleMessageKind.refused)
                  const SizedBox(width: kSimpleInset - 4),
              ],
            ),
            // A result's time left, as a line along its foot.
            if ((m.kind == SimpleMessageKind.result ||
                    m.kind == SimpleMessageKind.refused) &&
                !reduced)
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
                        color: dot.withValues(alpha: 0.6),
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
    // A warning stays until it is closed or cleared; its action is help.
    if (message.kind != SimpleMessageKind.warning) notifier.dismiss(message.id);
    message.actions[i].onPressed();
  }
}

/// A question's answers under its words: one row across the pill's width
/// while every label fits whole, else one full-width button per line (#1745).
/// The filled primary answer stays last.
class _QuestionAnswers extends StatelessWidget {
  const _QuestionAnswers({required this.labels, required this.onAnswer});

  final List<String> labels;
  final ValueChanged<int> onAnswer;

  // A button's side padding (Material 3) plus the gap between buttons.
  static const _buttonChrome = 48.0;
  static const _gap = 6.0;

  @override
  Widget build(BuildContext context) {
    final style = DefaultTextStyle.of(context).style.merge(
      Theme.of(context).textTheme.labelLarge,
    );
    final scaler = MediaQuery.textScalerOf(context);
    double width(String label) {
      final p = TextPainter(
        text: TextSpan(text: label, style: style),
        textScaler: scaler,
        textDirection: TextDirection.ltr,
      )..layout();
      final w = p.width;
      p.dispose();
      return w + _buttonChrome;
    }

    Widget button(int i) {
      final label = Text(labels[i], textAlign: TextAlign.center);
      return i == labels.length - 1
          ? FilledButton(
              style: FilledButton.styleFrom(
                backgroundColor: AraColors.accentBusy,
                foregroundColor: AraColors.bgPrimary,
              ),
              onPressed: () => onAnswer(i),
              child: label,
            )
          : TextButton(onPressed: () => onAnswer(i), child: label);
    }

    return LayoutBuilder(
      builder: (context, box) {
        final needed = labels.fold<double>(0, (sum, l) => sum + width(l)) +
            _gap * (labels.length - 1);
        if (needed <= box.maxWidth) {
          return Row(
            spacing: _gap,
            children: [
              // Shared by need, so each label keeps the width it was measured at.
              for (var i = 0; i < labels.length; i++) Expanded(flex: width(labels[i]).ceil(), child: button(i)),
            ],
          );
        }
        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          spacing: _gap,
          children: [for (var i = 0; i < labels.length; i++) button(i)],
        );
      },
    );
  }
}
