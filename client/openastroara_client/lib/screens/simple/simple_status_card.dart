import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/simple_messages_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import 'simple_message_pills.dart';
import 'simple_preview_panel.dart' show SimpleStatusPill;
import 'simple_shell.dart' show kSimpleMessagesMaxWidth, simpleFrost;

/// The status card, top left (#1782): what the rig is doing (the status
/// lines, one per source) and what it needs from you (the messages, newest
/// first) in one frosted card, each entry behind a hairline. As wide as its
/// widest line, up to [kSimpleMessagesMaxWidth]; the full width while a
/// question holds actions; never wider than the room the shell gives it
/// (between the rails, once it moves beside the left one). With no line and
/// no message nothing is drawn.
///
/// The messages' and the status lines' state is unchanged; only the drawing
/// is shared. The shell places this one widget.
class SimpleStatusCard extends ConsumerStatefulWidget {
  const SimpleStatusCard({
    super.key,
    required this.maxHeight,
    this.onOpen,
    this.onFirstHeight,
  });

  /// The room above the left rail: messages beyond it fold into "+N more".
  final double maxHeight;

  /// Opens what a line or a message came from, on a tap.
  final ValueChanged<SimpleStatusOrigin>? onOpen;

  /// The top status line's height, 0 with none (a line grows with large text).
  final ValueChanged<double>? onFirstHeight;

  @override
  ConsumerState<SimpleStatusCard> createState() => _SimpleStatusCardState();
}

class _SimpleStatusCardState extends ConsumerState<SimpleStatusCard> {
  final _linesKey = GlobalKey();
  double _linesHeight = 0;

  @override
  Widget build(BuildContext context) {
    final messages = ref.watch(simpleMessagesProvider);
    final asking = messages.any(
      (m) => m.kind == SimpleMessageKind.question && m.actions.isNotEmpty,
    );
    // The status lines' own height comes off the messages' room.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      final h = _linesKey.currentContext?.size?.height ?? 0;
      if (mounted && h != _linesHeight) setState(() => _linesHeight = h);
    });
    return simpleFrost(
      _CardSurface(
        child: ConstrainedBox(
          constraints: BoxConstraints(
            minWidth: asking ? kSimpleMessagesMaxWidth : 0,
            maxWidth: kSimpleMessagesMaxWidth,
          ),
          child: IntrinsicWidth(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                SimpleStatusPill(
                  key: _linesKey,
                  onFirstHeight: widget.onFirstHeight,
                  onOpen: widget.onOpen,
                  hairlineBelow: messages.isNotEmpty,
                ),
                SimpleMessagePills(
                  inCard: true,
                  onOpen: widget.onOpen,
                  maxHeight: widget.maxHeight.isFinite
                      ? math.max(0, widget.maxHeight - _linesHeight)
                      : widget.maxHeight,
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// A 1 px line between two entries of the card.
class SimpleCardHairline extends StatelessWidget {
  const SimpleCardHairline({super.key});

  @override
  Widget build(BuildContext context) =>
      const SizedBox(height: 1, child: ColoredBox(color: AraColors.border));
}

/// Lets a child that measures itself (a LayoutBuilder) sit under an
/// [IntrinsicWidth]: it reports no intrinsic width, and the card's width is
/// then set by the other entries or, with a question, by its full width.
class NoIntrinsicWidth extends SingleChildRenderObjectWidget {
  const NoIntrinsicWidth({super.key, required Widget super.child});

  @override
  RenderObject createRenderObject(BuildContext context) =>
      _RenderNoIntrinsicWidth();
}

class _RenderNoIntrinsicWidth extends RenderProxyBox {
  @override
  double computeMinIntrinsicWidth(double height) => 0;

  @override
  double computeMaxIntrinsicWidth(double height) => 0;

  @override
  Size computeDryLayout(BoxConstraints constraints) =>
      child?.getDryLayout(constraints) ?? constraints.smallest;
}

/// The card's scrim and contour, painted only while it holds something.
class _CardSurface extends SingleChildRenderObjectWidget {
  const _CardSurface({required Widget super.child});

  @override
  RenderObject createRenderObject(BuildContext context) => _RenderCardSurface();
}

class _RenderCardSurface extends RenderProxyBox {
  @override
  void paint(PaintingContext context, Offset offset) {
    if (size.isEmpty) return;
    final box = RRect.fromRectAndRadius(
      offset & size,
      const Radius.circular(AraRadius.simplePanel),
    );
    context.canvas.drawRRect(box, Paint()..color = AraColors.overlayScrim);
    super.paint(context, offset);
    // On top, not a border, so the contour takes no room from the content.
    context.canvas.drawRRect(
      box.deflate(0.5),
      Paint()
        ..style = PaintingStyle.stroke
        ..color = AraColors.border,
    );
  }
}
