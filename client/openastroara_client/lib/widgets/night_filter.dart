import 'package:flutter/rendering.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../state/night_mode_state.dart';

/// Night observing filter: fold every pixel's luminance into the red channel
/// and zero green and blue outright. Blue/green light is what actually resets
/// scotopic dark adaptation, so leaking half of it through would defeat the
/// point. Luminance weights keep the UI readable — a green "connected" chip
/// stays brighter than a dim border, so the interface still reads by
/// brightness once hue is gone.
const ColorFilter kNightColorFilter = ColorFilter.matrix(<double>[
  0.30, 0.59, 0.11, 0.0, 0.0, // R ← luminance
  0.00, 0.00, 0.00, 0.0, 0.0, // G
  0.00, 0.00, 0.00, 0.0, 0.0, // B
  0.00, 0.00, 0.00, 1.0, 0.0, // A
]);

/// Applies [kNightColorFilter] over [child] while night mode is on.
///
/// This sits in `MaterialApp.builder`, directly above the Navigator. It used
/// to be `night ? ColorFiltered(child) : child`, which changes the
/// Navigator's PARENT on every toggle; `WidgetsApp` keys the Navigator with a
/// `GlobalObjectKey`, so each toggle reparented the entire app tree — the
/// one GlobalKey move in the app, and a prime suspect for the inherited-
/// element assertions in #1111. Now the parent is always the same render
/// proxy, which pushes the colour-filter layer when enabled and paints
/// straight through otherwise. The Navigator never moves.
class NightFilter extends ConsumerWidget {
  const NightFilter({super.key, required this.child});

  final Widget child;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final enabled = switch (ref.watch(nightModeProvider)) {
      AsyncData(:final value) => value,
      _ => false,
    };
    return _NightFilterRenderWidget(enabled: enabled, child: child);
  }
}

class _NightFilterRenderWidget extends SingleChildRenderObjectWidget {
  const _NightFilterRenderWidget({required this.enabled, required super.child});

  final bool enabled;

  @override
  RenderNightFilter createRenderObject(BuildContext context) =>
      RenderNightFilter(enabled);

  @override
  void updateRenderObject(BuildContext context, RenderNightFilter renderObject) {
    renderObject.enabled = enabled;
  }
}

/// Render side of [NightFilter]; public so tests can read [enabled].
class RenderNightFilter extends RenderProxyBox {
  RenderNightFilter(this._enabled);

  bool get enabled => _enabled;
  bool _enabled;
  set enabled(bool value) {
    if (value == _enabled) return;
    _enabled = value;
    markNeedsPaint();
    markNeedsCompositingBitsUpdate();
  }

  @override
  bool get alwaysNeedsCompositing => _enabled;

  @override
  void paint(PaintingContext context, Offset offset) {
    if (!_enabled) {
      layer = null;
      super.paint(context, offset);
      return;
    }
    layer = context.pushColorFilter(
      offset,
      kNightColorFilter,
      super.paint,
      oldLayer: layer as ColorFilterLayer?,
    );
  }
}
