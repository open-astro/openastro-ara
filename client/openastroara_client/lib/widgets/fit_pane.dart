import 'dart:math' as math;

import 'package:flutter/foundation.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/widgets.dart';

/// A pane that fits the window: its content fills the height it is given, and
/// only scrolls once the window is shorter than the content can shrink to.
///
/// The content passes the height down as a minimum: a [FitColumn] hands what
/// its other children leave to its [FitFill] child, and a [FitBand] at the
/// end of that chain builds at the height that remains — so an instrument
/// grows on a tall monitor and shrinks on a laptop, with no height guessed
/// from the window size.
class FitPane extends StatelessWidget {
  final EdgeInsets padding;
  final Widget child;
  const FitPane({super.key, this.padding = EdgeInsets.zero, required this.child});

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(
      builder: (context, c) => SingleChildScrollView(
        padding: padding,
        child: ConstrainedBox(
          constraints: BoxConstraints(
            minHeight: c.hasBoundedHeight ? math.max(0.0, c.maxHeight - padding.vertical) : 0,
          ),
          child: child,
        ),
      ),
    );
  }
}

/// A full-width column whose one [FitFill] child takes the height the
/// column's minimum leaves after the other children (which take their natural
/// height). Any child may build taller; the column then grows past the
/// minimum and the [FitPane] scrolls.
class FitColumn extends MultiChildRenderObjectWidget {
  const FitColumn({super.key, super.children});

  @override
  RenderObject createRenderObject(BuildContext context) => _RenderFitColumn();
}

/// Marks the [FitColumn] child that takes the leftover height.
class FitFill extends ParentDataWidget<_FitParentData> {
  const FitFill({super.key, required super.child});

  @override
  void applyParentData(RenderObject renderObject) {
    final data = renderObject.parentData! as _FitParentData;
    if (!data.fill) {
      data.fill = true;
      renderObject.parent?.markNeedsLayout();
    }
  }

  @override
  Type get debugTypicalAncestorWidgetClass => FitColumn;
}

/// The end of a [FitFill] chain: builds with the pane's width and the height
/// left for it. The builder clamps that height to whatever floor its content
/// needs; building taller makes the pane scroll.
class FitBand extends StatelessWidget {
  final Widget Function(BuildContext context, double width, double height) builder;
  const FitBand({super.key, required this.builder});

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(builder: (context, c) => builder(context, c.maxWidth, c.minHeight));
  }
}

/// Side-by-side columns that share one height: the height their parent's
/// minimum asks for, or the tallest column's own minimum when that is more.
/// Each child gets [flex] of the width after [spacing]; make the children
/// [FitColumn]s so the extra height reaches their [FitFill].
class FitRow extends MultiChildRenderObjectWidget {
  final List<int> flex;
  final double spacing;
  const FitRow({super.key, required this.flex, this.spacing = 0, super.children})
      : assert(flex.length == children.length);

  @override
  RenderObject createRenderObject(BuildContext context) =>
      _RenderFitRow(flex, spacing);

  @override
  void updateRenderObject(BuildContext context, RenderObject renderObject) {
    (renderObject as _RenderFitRow)
      ..flex = flex
      ..spacing = spacing;
  }
}

class _FitParentData extends ContainerBoxParentData<RenderBox> {
  bool fill = false;
}

class _RenderFitColumn extends RenderBox
    with
        ContainerRenderObjectMixin<RenderBox, _FitParentData>,
        RenderBoxContainerDefaultsMixin<RenderBox, _FitParentData> {
  @override
  void setupParentData(RenderBox child) {
    if (child.parentData is! _FitParentData) child.parentData = _FitParentData();
  }

  @override
  void performLayout() {
    final width = constraints.maxWidth;
    final natural = BoxConstraints.tightFor(width: width);
    var used = 0.0;
    RenderBox? fill;
    for (var child = firstChild; child != null; child = childAfter(child)) {
      if ((child.parentData! as _FitParentData).fill) {
        assert(fill == null, 'A FitColumn takes one FitFill child.');
        fill = child;
      } else {
        child.layout(natural, parentUsesSize: true);
        used += child.size.height;
      }
    }
    final target = math.max(0.0, constraints.minHeight - used);
    fill?.layout(
      BoxConstraints(
        minWidth: width,
        maxWidth: width,
        minHeight: target,
        // Under a bounded parent (a FitRow lining its columns up) the fill
        // takes exactly what is left.
        maxHeight: constraints.hasBoundedHeight
            ? math.max(target, constraints.maxHeight - used)
            : double.infinity,
      ),
      parentUsesSize: true,
    );
    var y = 0.0;
    for (var child = firstChild; child != null; child = childAfter(child)) {
      (child.parentData! as _FitParentData).offset = Offset(0, y);
      y += child.size.height;
    }
    size = constraints.constrain(Size(width, y));
  }

  @override
  double? computeDistanceToActualBaseline(TextBaseline baseline) =>
      defaultComputeDistanceToFirstActualBaseline(baseline);

  @override
  void paint(PaintingContext context, Offset offset) => defaultPaint(context, offset);

  @override
  bool hitTestChildren(BoxHitTestResult result, {required Offset position}) =>
      defaultHitTestChildren(result, position: position);
}

class _RenderFitRow extends RenderBox
    with
        ContainerRenderObjectMixin<RenderBox, _FitParentData>,
        RenderBoxContainerDefaultsMixin<RenderBox, _FitParentData> {
  _RenderFitRow(this._flex, this._spacing);

  List<int> _flex;
  set flex(List<int> value) {
    if (listEquals(_flex, value)) return;
    _flex = value;
    markNeedsLayout();
  }

  double _spacing;
  set spacing(double value) {
    if (_spacing == value) return;
    _spacing = value;
    markNeedsLayout();
  }

  @override
  void setupParentData(RenderBox child) {
    if (child.parentData is! _FitParentData) child.parentData = _FitParentData();
  }

  @override
  void performLayout() {
    final children = getChildrenAsList();
    final total = _flex.fold<int>(0, (a, b) => a + b);
    final free = math.max(0.0, constraints.maxWidth - _spacing * math.max(0, children.length - 1));
    final widths = [for (final f in _flex) total == 0 ? 0.0 : free * f / total];
    // First pass: each column at the parent's minimum, free to grow.
    var height = constraints.minHeight;
    for (var i = 0; i < children.length; i++) {
      children[i].layout(
        BoxConstraints(minWidth: widths[i], maxWidth: widths[i], minHeight: constraints.minHeight),
        parentUsesSize: true,
      );
      height = math.max(height, children[i].size.height);
    }
    height = constraints.constrainHeight(height);
    // Second pass: every column at the shared height, so the edges line up.
    var x = 0.0;
    for (var i = 0; i < children.length; i++) {
      final child = children[i];
      if (child.size.height != height) {
        child.layout(BoxConstraints.tightFor(width: widths[i], height: height), parentUsesSize: true);
      }
      (child.parentData! as _FitParentData).offset = Offset(x, 0);
      x += widths[i] + _spacing;
    }
    size = constraints.constrain(Size(constraints.maxWidth, height));
  }

  @override
  void paint(PaintingContext context, Offset offset) => defaultPaint(context, offset);

  @override
  bool hitTestChildren(BoxHitTestResult result, {required Offset position}) =>
      defaultHitTestChildren(result, position: position);
}
