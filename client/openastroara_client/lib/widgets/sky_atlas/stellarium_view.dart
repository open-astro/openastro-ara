import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:webview_cef/webview_cef.dart';

import '../../services/stellarium_server.dart';
import '../../state/saved_server_state.dart';
import '../../state/sky_atlas/site_location_state.dart';
import '../../theme/ara_colors.dart';

/// §36 Planetarium — the embedded Stellarium Web Engine (AGPL; see
/// `assets/stellarium/LICENSE-AGPL-3.0.txt`), rendered in a CEF webview. The page
/// is **self-driven**: this widget just loads it with the observer location and
/// the daemon's API base in the URL query, and the page does everything else
/// (sets its observer, runs its own on-screen controls, talks to the daemon API).
/// There is deliberately no Dart→page JS bridge.
class StellariumView extends ConsumerStatefulWidget {
  const StellariumView({super.key});

  @override
  ConsumerState<StellariumView> createState() => _StellariumViewState();
}

// CEF's manager is a process-wide singleton; initialize it at most once. A FAILED
// init is not cached so a later mount can retry.
Future<void>? _managerInit;
Future<void> _ensureManagerInitialized() =>
    _managerInit ??= WebviewManager().initialize();

class _StellariumViewState extends ConsumerState<StellariumView> {
  WebViewController? _controller;
  bool _unavailable = false;

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
      // The page self-initialises from these query params: the observer site, and
      // the daemon API base it fetches Tonight's-Sky / posts GoTo to.
      final site = ref.read(siteLocationProvider).asData?.value ??
          await ref.read(siteLocationProvider.future);
      final servers = await ref.read(savedServersProvider.future);
      final api = servers.isNotEmpty ? servers.last.baseUrl : '';
      final query = {
        'lat': (site?.latitudeDeg ?? 0).toString(),
        'lon': (site?.longitudeDeg ?? 0).toString(),
        'elev': (site?.elevationM ?? 0).toString(),
        'api': api,
      }.entries.map((e) => '${e.key}=${Uri.encodeQueryComponent(e.value)}').join('&');
      url = '${server.baseUrl}/index.html?$query';
    } catch (e, st) {
      debugPrint('StellariumView: asset server / site read failed: $e\n$st');
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
    } catch (e, st) {
      debugPrint('StellariumView: browser init failed: $e\n$st');
      if (mounted) setState(() => _unavailable = true);
    }
  }

  @override
  void dispose() {
    unawaited(_controller?.dispose());
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
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
                  'The embedded planetarium renderer could not start on this host. '
                  'A Chromium runtime is required — install it, then reopen the app.',
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
