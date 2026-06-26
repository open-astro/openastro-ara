import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:webview_cef/webview_cef.dart';

import '../../services/stellarium_server.dart';
import '../../state/imaging/fov_box.dart';
import '../../state/sky_atlas/planning_time_state.dart';
import '../../state/sky_atlas/site_location_state.dart';
import '../../state/sky_atlas/sky_atlas_state.dart';
import '../../theme/ara_colors.dart';

/// §36 Planetarium — the embedded Stellarium Web Engine (AGPL; see
/// `assets/stellarium/LICENSE-AGPL-3.0.txt`). A CEF/Chromium browser renders the
/// engine into a Flutter texture, fed by the loopback [StellariumServer] so the
/// whole sky (stars, DSO, planets, the real horizon, satellites) works offline.
///
/// OpenAstro Ara drives the engine through the page's `window.araStel` bridge
/// (see `assets/stellarium/index.html`): here we point the observer at the active
/// profile's site and set the clock to now. GoTo, camera-FOV framing/mosaic, and
/// layer toggles layer on in later slices via the same bridge.
class StellariumView extends ConsumerStatefulWidget {
  const StellariumView({super.key});

  @override
  ConsumerState<StellariumView> createState() => _StellariumViewState();
}

// CEF's manager is a process-wide singleton; initialize it at most once. A FAILED
// init is not cached so a later mount can retry (e.g. after a missing runtime is
// installed).
Future<void>? _managerInit;
Future<void> _ensureManagerInitialized() =>
    _managerInit ??= WebviewManager().initialize();

class _StellariumViewState extends ConsumerState<StellariumView> {
  WebViewController? _controller;
  bool _unavailable = false;

  // A site pushed before the browser is ready is stashed and applied on init, so
  // an early profile read isn't dropped.
  SiteLocation? _pendingSite;

  // Re-points the observer's clock to "now" on a cadence so the live sky keeps
  // moving while the planetarium is open.
  Timer? _clock;

  @override
  void initState() {
    super.initState();
    unawaited(_init());
  }

  Future<void> _init() async {
    try {
      await _ensureManagerInitialized();
    } catch (e, st) {
      _managerInit = null;
      debugPrint('StellariumView: CEF manager init failed: $e\n$st');
      if (mounted) setState(() => _unavailable = true);
      return;
    }
    if (!mounted) return;

    final String url;
    try {
      final server = await StellariumServer.start();
      url = '${server.baseUrl}/index.html';
    } catch (e, st) {
      debugPrint('StellariumView: asset server failed to start: $e\n$st');
      if (mounted) setState(() => _unavailable = true);
      return;
    }
    if (!mounted) return;

    try {
      final controller = WebviewManager().createWebView(loading: const _Loading());
      await controller.initialize(url);
      if (!mounted) {
        await controller.dispose();
        return;
      }
      setState(() => _controller = controller);
      // Apply the site stashed before init, or — since the tab unmounts on switch —
      // the provider's current value so returning to Planning re-points the sky.
      final site = _pendingSite ?? ref.read(siteLocationProvider).asData?.value;
      _pendingSite = null;
      if (site != null) unawaited(_runSite(controller, site));
      unawaited(_runTime(controller));
      // Re-apply the selected target on (re)mount so returning to Planning keeps
      // the view on it.
      final target = ref.read(skyTargetProvider);
      if (target != null) unawaited(_runCenter(controller, target));
      _updateFraming();
      // Keep the clock live (~30 s cadence is smooth for a planning view).
      _clock = Timer.periodic(const Duration(seconds: 30), (_) {
        final c = _controller;
        if (c != null) unawaited(_runTime(c));
      });
    } catch (e, st) {
      debugPrint('StellariumView: browser init failed: $e\n$st');
      if (mounted) setState(() => _unavailable = true);
    }
  }

  void _setSite(SiteLocation site) {
    final controller = _controller;
    if (controller == null) {
      _pendingSite = site;
      return;
    }
    unawaited(_runSite(controller, site));
  }

  Future<void> _runSite(WebViewController controller, SiteLocation s) async {
    try {
      await controller.executeJavaScript(
          'window.araStel && window.araStel.setLocation(${s.latitudeDeg}, ${s.longitudeDeg}, ${s.elevationM});');
    } catch (e, st) {
      // Degrade-not-crash: a failed observer update leaves the sky where it was.
      debugPrint('StellariumView: setLocation failed: $e\n$st');
    }
  }

  // Push the planning time to the engine: a pinned time holds the sky there, else
  // the live clock. Called on the periodic tick (so "live" keeps moving) and
  // whenever the planning time changes.
  Future<void> _runTime(WebViewController controller) async {
    try {
      final pinned = ref.read(planningTimeProvider);
      final js = pinned == null
          ? 'window.araStel && window.araStel.setTimeNow();'
          : 'window.araStel && window.araStel.setTimeUnixMs(${pinned.millisecondsSinceEpoch});';
      await controller.executeJavaScript(js);
    } catch (e, st) {
      debugPrint('StellariumView: set time failed: $e\n$st');
    }
  }

  // Fly the view to a chosen target. A target set before the browser is ready is
  // re-applied from the provider on init (the provider retains the last one).
  void _centerOn(SkyTarget target) {
    final controller = _controller;
    if (controller == null) return;
    unawaited(_runCenter(controller, target));
  }

  Future<void> _runCenter(WebViewController controller, SkyTarget t) async {
    try {
      await controller.executeJavaScript(
          'window.araStel && window.araStel.centerRaDec(${t.raDeg}, ${t.decDeg});');
    } catch (e, st) {
      debugPrint('StellariumView: centerRaDec failed: $e\n$st');
    }
  }

  // Draw the camera framing/mosaic overlay centred on the selected target, using
  // the FOV box (size/rotation/mosaic) from the optics + framing controls. Cleared
  // when Frame mode is off (no box) or nothing is selected.
  void _updateFraming() {
    final controller = _controller;
    if (controller == null) return;
    unawaited(_runFraming(
        controller, ref.read(frameFovBoxProvider), ref.read(skyTargetProvider)));
  }

  Future<void> _runFraming(
      WebViewController controller, FovBox? box, SkyTarget? target) async {
    try {
      if (box == null || target == null) {
        await controller.executeJavaScript('window.araStel && window.araStel.clearFraming();');
        return;
      }
      await controller.executeJavaScript('window.araStel && window.araStel.setFraming({'
          'raDeg: ${target.raDeg}, decDeg: ${target.decDeg}, '
          'fovWidthDeg: ${box.widthDeg}, fovHeightDeg: ${box.heightDeg}, '
          'rotationDeg: ${box.rotationDeg}, cols: ${box.cols}, rows: ${box.rows}, '
          'overlapPct: ${box.overlapPct}});');
    } catch (e, st) {
      debugPrint('StellariumView: framing update failed: $e\n$st');
    }
  }

  Future<void> _js(String code) async {
    final controller = _controller;
    if (controller == null) return;
    try {
      await controller.executeJavaScript(code);
    } catch (e, st) {
      debugPrint('StellariumView: nav js failed: $e\n$st');
    }
  }

  // On-screen navigation for touch/VNC where scroll-zoom + drag-pan aren't easy.
  void _zoom(double factor) => unawaited(_js('window.araStel && window.araStel.zoomBy($factor);'));
  void _pan(double dAz, double dAlt) =>
      unawaited(_js('window.araStel && window.araStel.panBy($dAz, $dAlt);'));

  @override
  void dispose() {
    _clock?.cancel();
    unawaited(_controller?.dispose());
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    // Re-point the observer when the active profile's site resolves/changes.
    ref.listen<AsyncValue<SiteLocation?>>(siteLocationProvider, (_, next) {
      final site = next.asData?.value;
      if (site != null) _setSite(site);
    });
    // Planning time changed (Now / Tonight / nudge) → repoint the engine clock.
    ref.listen<DateTime?>(planningTimeProvider, (_, _) {
      final c = _controller;
      if (c != null) unawaited(_runTime(c));
    });
    // A chosen target (Tonight's Sky, search) → fly the planetarium to it, and
    // re-centre the framing overlay (if Frame mode is on) on the new target.
    ref.listen<SkyTarget?>(skyTargetProvider, (_, target) {
      if (target != null) _centerOn(target);
      _updateFraming();
    });
    // The camera FOV box (size/rotation/mosaic, or null when Frame mode is off) →
    // draw or clear the framing overlay.
    ref.listen<FovBox?>(frameFovBoxProvider, (_, _) => _updateFraming());

    if (_unavailable) return const _Unavailable();
    final controller = _controller;
    if (controller == null) return const _Loading();
    return ColoredBox(
      color: AraColors.bgPrimary,
      child: Stack(
        children: [
          Positioned.fill(child: controller.webviewWidget),
          // Touch/VNC navigation overlay: a pan d-pad (lower-left) and zoom
          // buttons (lower-right), since scroll-zoom + drag-pan are awkward remote.
          Positioned(
            left: 12,
            bottom: 12,
            child: _PanPad(onPan: _pan),
          ),
          Positioned(
            right: 12,
            bottom: 12,
            child: _ZoomControls(onZoom: _zoom),
          ),
        ],
      ),
    );
  }
}

/// A compact 4-way pan d-pad. Each press nudges the view ~10° in az/alt.
class _PanPad extends StatelessWidget {
  const _PanPad({required this.onPan});
  final void Function(double dAz, double dAlt) onPan;

  static const _step = 10.0;

  @override
  Widget build(BuildContext context) {
    Widget btn(IconData icon, double dAz, double dAlt) => _NavButton(
        icon: icon, onPressed: () => onPan(dAz, dAlt));
    return Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        btn(Icons.keyboard_arrow_up, 0, _step),
        Row(mainAxisSize: MainAxisSize.min, children: [
          btn(Icons.keyboard_arrow_left, -_step, 0),
          const SizedBox(width: 40),
          btn(Icons.keyboard_arrow_right, _step, 0),
        ]),
        btn(Icons.keyboard_arrow_down, 0, -_step),
      ],
    );
  }
}

/// Zoom in / out buttons (factor < 1 zooms in).
class _ZoomControls extends StatelessWidget {
  const _ZoomControls({required this.onZoom});
  final void Function(double factor) onZoom;

  @override
  Widget build(BuildContext context) => Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          _NavButton(icon: Icons.add, onPressed: () => onZoom(0.6)),
          const SizedBox(height: 6),
          _NavButton(icon: Icons.remove, onPressed: () => onZoom(1.0 / 0.6)),
        ],
      );
}

class _NavButton extends StatelessWidget {
  const _NavButton({required this.icon, required this.onPressed});
  final IconData icon;
  final VoidCallback onPressed;

  @override
  Widget build(BuildContext context) => Material(
        color: Colors.black54,
        shape: const CircleBorder(),
        clipBehavior: Clip.antiAlias,
        child: IconButton(
          icon: Icon(icon, size: 22, color: Colors.white),
          onPressed: onPressed,
          visualDensity: VisualDensity.compact,
        ),
      );
}

class _Loading extends StatelessWidget {
  const _Loading();

  @override
  Widget build(BuildContext context) => const ColoredBox(
        color: AraColors.bgPrimary,
        child: Center(child: CircularProgressIndicator()),
      );
}

class _Unavailable extends StatelessWidget {
  const _Unavailable();

  @override
  Widget build(BuildContext context) => Container(
        color: AraColors.bgPrimary,
        child: Center(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              const Icon(Icons.public_off, size: 96, color: AraColors.textDisabled),
              const SizedBox(height: 12),
              Text('Planetarium unavailable',
                  style: Theme.of(context).textTheme.titleMedium),
              const SizedBox(height: 6),
              Padding(
                padding: const EdgeInsets.symmetric(horizontal: 24),
                child: Text(
                  'The embedded Stellarium renderer could not start on this host. '
                  'A Chromium runtime is required for the in-app planetarium — '
                  'install it, then close and reopen the app.',
                  textAlign: TextAlign.center,
                  style: Theme.of(context)
                      .textTheme
                      .bodySmall
                      ?.copyWith(color: AraColors.textSecondary),
                ),
              ),
            ],
          ),
        ),
      );
}
