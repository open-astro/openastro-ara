import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/planetarium_overlay.dart';
import '../../state/app_shell_state.dart';
import '../../theme/ara_colors.dart';

/// Route observer that lets the Linux planetarium overlay hide itself when a
/// full-screen route or dialog (Image Library, Stats, Wizard, command palette…)
/// is pushed over the app shell — otherwise the native GTK webview, which is
/// composited *above* Flutter, would punch through the route on top of it.
/// Registered in `main.dart`'s `MaterialApp.navigatorObservers`; harmless on
/// non-Linux platforms (nothing subscribes there).
final RouteObserver<ModalRoute<void>> planetariumRouteObserver =
    RouteObserver<ModalRoute<void>>();

/// Linux Planning surface: an in-window placeholder that drives the native
/// WebKitGTK overlay (see `services/planetarium_overlay.dart` and
/// `linux/runner/planetarium_overlay.cc`).
///
/// The widget itself paints only the planetarium's background — the actual sky
/// map is a native webview GTK composites on top of `FlView`. This widget's job
/// is to keep that overlay (a) loaded with the loopback URL, (b) positioned over
/// this widget's on-screen rect, and (c) visible only while Planning is the
/// active tab and no route covers it. The webview is never torn down on a
/// tab-switch, so the atlas keeps its state (matching the IndexedStack contract).
class LinuxPlanetariumOverlay extends ConsumerStatefulWidget {
  final String url;

  const LinuxPlanetariumOverlay({super.key, required this.url});

  @override
  ConsumerState<LinuxPlanetariumOverlay> createState() =>
      _LinuxPlanetariumOverlayState();
}

class _LinuxPlanetariumOverlayState
    extends ConsumerState<LinuxPlanetariumOverlay>
    with WidgetsBindingObserver, RouteAware {
  static const _overlay = PlanetariumOverlay();

  // Planning is tab 0 in AppShell (see [selectedTabIndexProvider]).
  static const _planningTabIndex = 0;

  Rect? _lastBounds;
  bool _routeOnTop = false;
  bool? _lastVisible;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    // Load the page once (creates the native webview on the first call), then
    // push the initial geometry after the first layout.
    _overlay.setUrl(widget.url);
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) _pushBounds();
    });
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    // Unsubscribe before re-subscribing: didChangeDependencies can fire with a
    // different ModalRoute instance, and RouteObserver keys subscriptions by route.
    // Without this, `this` could stay bound to a stale route and fire didPushNext/
    // didPopNext (or setState-after-dispose) spuriously.
    planetariumRouteObserver.unsubscribe(this);
    final route = ModalRoute.of(context);
    if (route != null) planetariumRouteObserver.subscribe(this, route);
  }

  @override
  void didUpdateWidget(LinuxPlanetariumOverlay oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.url != widget.url) _overlay.setUrl(widget.url);
  }

  @override
  void dispose() {
    planetariumRouteObserver.unsubscribe(this);
    WidgetsBinding.instance.removeObserver(this);
    // Don't leave the native webview floating once Planning is gone. Fire-and-
    // forget: dispose() can't be async, and the MethodChannel hop is best-effort.
    unawaited(_overlay.setVisible(false));
    super.dispose();
  }

  // Window resize / DPI change → the slot moved or grew; re-push its rect.
  @override
  void didChangeMetrics() {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) _pushBounds();
    });
  }

  // A route was pushed over the app shell — hide the overlay so it doesn't show
  // through the route on top. Hide SYNCHRONOUSLY here, not via the post-frame path:
  // the GTK webview is composited above Flutter's GL surface at the X11 level, so a
  // one-frame deferral would flash the sky map through the covering route. (Hiding
  // is always safe; re-show stays deferred so it honours the isPlanning gate.)
  @override
  void didPushNext() {
    _applyVisibility(false);   // immediate hide + keeps _lastVisible in sync
    setState(() => _routeOnTop = true);
  }

  // The covering route was popped — the app shell is frontmost again. The deferred
  // re-show in build() reapplies the correct visibility (isPlanning && !_routeOnTop);
  // a one-frame delay on *show* is invisible, so no synchronous call is needed.
  @override
  void didPopNext() => setState(() => _routeOnTop = false);

  void _pushBounds() {
    final box = context.findRenderObject();
    if (box is! RenderBox || !box.hasSize) return;
    final rect = box.localToGlobal(Offset.zero) & box.size;
    if (rect == _lastBounds) return;
    _lastBounds = rect;
    _overlay.setBounds(rect);
  }

  void _applyVisibility(bool visible) {
    if (visible == _lastVisible) return;
    _lastVisible = visible;
    _overlay.setVisible(visible);
  }

  @override
  Widget build(BuildContext context) {
    final isPlanning =
        ref.watch(selectedTabIndexProvider) == _planningTabIndex;
    final visible = isPlanning && !_routeOnTop;
    // Geometry and visibility can only be read/applied after this frame lays the
    // slot out. Re-push bounds when becoming visible in case the window resized
    // while Planning was hidden.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      if (visible) _pushBounds();
      _applyVisibility(visible);
    });
    // Background only — the live sky map is the native webview on top of this.
    return const ColoredBox(color: AraColors.bgPrimary, child: SizedBox.expand());
  }
}
