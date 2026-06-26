import 'dart:async';
import 'dart:convert';

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
  StellariumServer? _server;
  final _searchCtrl = TextEditingController();
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
      _server = server;
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
    _searchCtrl.dispose();
    unawaited(_controller?.dispose());
    super.dispose();
  }

  // CEF can't receive keyboard text or a JS-bridge call in the multi-process
  // setup, so the typed search lives in Flutter and reaches the planetarium page
  // through the loopback server's one-shot command channel (the page polls it).
  void _pushCmd(Map<String, Object?> cmd) => _server?.pushCommand(jsonEncode(cmd));

  void _submitSearch() {
    final q = _searchCtrl.text.trim();
    if (q.isEmpty) return;
    _pushCmd({'type': 'search', 'q': q});
  }

  @override
  Widget build(BuildContext context) {
    if (_unavailable) return const _Unavailable();
    final controller = _controller;
    if (controller == null) return const _Loading();
    return ColoredBox(
      color: AraColors.bgPrimary,
      child: Column(
        children: [
          _SearchBar(
            controller: _searchCtrl,
            onSubmit: _submitSearch,
            onTonight: () => _pushCmd({'type': 'tonight'}),
          ),
          Expanded(child: controller.webviewWidget),
        ],
      ),
    );
  }
}

/// Thin top bar over the planetarium: a universal search field + a Tonight's Sky
/// toggle. The field is Flutter (so the keyboard works); submitting it hands the
/// query to the page via the loopback command channel.
class _SearchBar extends StatelessWidget {
  final TextEditingController controller;
  final VoidCallback onSubmit;
  final VoidCallback onTonight;

  const _SearchBar({
    required this.controller,
    required this.onSubmit,
    required this.onTonight,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      height: 44,
      padding: const EdgeInsets.symmetric(horizontal: 8),
      decoration: const BoxDecoration(
        color: AraColors.bgPanel,
        border: Border(bottom: BorderSide(color: AraColors.border)),
      ),
      child: Row(
        children: [
          Expanded(
            child: TextField(
              controller: controller,
              textInputAction: TextInputAction.search,
              onSubmitted: (_) => onSubmit(),
              style: const TextStyle(fontSize: 13),
              decoration: InputDecoration(
                isDense: true,
                prefixIcon: const Icon(Icons.search, size: 18),
                hintText:
                    'Search — M42, NGC 7000, Vega, Jupiter, 05:35 -05:23…',
                hintStyle: const TextStyle(
                    fontSize: 13, color: AraColors.textSecondary),
                filled: true,
                fillColor: AraColors.bgPrimary,
                contentPadding:
                    const EdgeInsets.symmetric(horizontal: 8, vertical: 8),
                border: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(6),
                  borderSide: const BorderSide(color: AraColors.border),
                ),
                suffixIcon: IconButton(
                  tooltip: 'Go',
                  icon: const Icon(Icons.arrow_forward, size: 18),
                  onPressed: onSubmit,
                ),
              ),
            ),
          ),
          const SizedBox(width: 8),
          OutlinedButton.icon(
            onPressed: onTonight,
            icon: const Icon(Icons.nights_stay_outlined, size: 16),
            label: const Text("Tonight's Sky"),
          ),
        ],
      ),
    );
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
