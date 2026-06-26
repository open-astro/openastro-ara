import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:webview_cef/webview_cef.dart';

import '../../services/stellarium_server.dart';
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
      unawaited(_runTimeNow(controller));
      // Re-apply the selected target on (re)mount so returning to Planning keeps
      // the view on it.
      final target = ref.read(skyTargetProvider);
      if (target != null) unawaited(_runCenter(controller, target));
      // Keep the clock live (~30 s cadence is smooth for a planning view).
      _clock = Timer.periodic(const Duration(seconds: 30), (_) {
        final c = _controller;
        if (c != null) unawaited(_runTimeNow(c));
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

  Future<void> _runTimeNow(WebViewController controller) async {
    try {
      await controller.executeJavaScript('window.araStel && window.araStel.setTimeNow();');
    } catch (e, st) {
      debugPrint('StellariumView: setTimeNow failed: $e\n$st');
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
    // A chosen target (Tonight's Sky, search) → fly the planetarium to it.
    ref.listen<SkyTarget?>(skyTargetProvider, (_, target) {
      if (target != null) _centerOn(target);
    });

    if (_unavailable) return const _Unavailable();
    final controller = _controller;
    if (controller == null) return const _Loading();
    return ColoredBox(color: AraColors.bgPrimary, child: controller.webviewWidget);
  }
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
