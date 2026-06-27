import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:webview_cef/webview_cef.dart';
import 'package:webview_all/webview_all.dart' as wva;

import '../../models/sequence/slew_target_body.dart';
import '../../services/stellarium_server.dart';
import '../../state/saved_server_state.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../state/sky_atlas/site_location_state.dart';
import '../../theme/ara_colors.dart';

/// SPIKE FLAG (macOS A/B). When built with `--dart-define=WEBVIEW_ALL=true`, the
/// planetarium renders in a native-engine webview (`webview_all` → WKWebView on
/// macOS) instead of the CEF OSR webview. Default builds stay on CEF. This lets
/// us compare stability (the CEF offscreen-render path stalls under sustained GPU
/// load) without disturbing the shipping path. The page, the loopback asset
/// server, the search-command channel and the event channel are identical either
/// way — only the rendering widget differs.
const bool kUseWebViewAll = bool.fromEnvironment('WEBVIEW_ALL');

/// §36 Planetarium — the embedded Stellarium Web Engine (AGPL; see
/// `assets/stellarium/LICENSE-AGPL-3.0.txt`). The page is **self-driven**: this
/// widget just loads it with the observer location and the daemon's API base in
/// the URL query, and the page does everything else (sets its observer, runs its
/// own on-screen controls, talks to the daemon API). There is deliberately no
/// Dart→page JS bridge — Flutter↔page traffic goes over the loopback server.
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
  WebViewController? _controller; // CEF (default path)
  wva.WebViewController? _wvaController; // native webview (spike path)
  StellariumServer? _server;
  StreamSubscription<Map<String, Object?>>? _eventSub;
  final _searchCtrl = TextEditingController();
  bool _unavailable = false;

  @override
  void initState() {
    super.initState();
    unawaited(_init());
  }

  Future<void> _init() async {
    // Pin the autoDispose site/server providers for the duration of init. Without
    // this, awaiting `siteLocationProvider.future` across the async gaps below
    // races the provider's autodisposal ("Cannot use the Ref … after it has been
    // disposed"); the old CEF-first ordering only masked it by delaying the read.
    final keepSite = ref.listenManual(siteLocationProvider, (_, _) {});
    final keepServers = ref.listenManual(savedServersProvider, (_, _) {});
    try {
      // Start the shared loopback asset server + build the page URL. Both renderers
      // load the same self-driven page; all Flutter↔page comms go through this
      // server's loopback channels, not a webview JS bridge.
      final String url;
      try {
        final server = await StellariumServer.start();
        if (!mounted) return;
        _server = server;
        // Handle events the page posts back (e.g. framing → add-to-sequence).
        _eventSub = server.events.listen(_onPageEvent);
        // The page self-initialises from these query params: the observer site, and
        // the daemon API base it fetches Tonight's-Sky / posts GoTo to.
        final site = ref.read(siteLocationProvider).asData?.value ??
            await ref.read(siteLocationProvider.future);
        if (!mounted) return;
        final servers = await ref.read(savedServersProvider.future);
        if (!mounted) return;
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

      if (kUseWebViewAll) {
        await _initWebViewAll(url);
      } else {
        await _initCef(url);
      }
    } finally {
      keepSite.close();
      keepServers.close();
    }
  }

  // Default path: CEF OSR webview rendered into a Flutter texture.
  Future<void> _initCef(String url) async {
    try {
      await _ensureManagerInitialized();
    } catch (e, st) {
      _managerInit = null;
      debugPrint('StellariumView: CEF manager init failed: $e\n$st');
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

  // Spike path: native-engine webview (WKWebView on macOS) embedded as a platform
  // view. No OSR/IOSurface texture handoff, so it doesn't hit the CEF stall.
  Future<void> _initWebViewAll(String url) async {
    try {
      // Keep this minimal: webview_all notes some APIs are unimplemented on macOS
      // WKWebView, and an unimplemented setter would abort the whole init. JS is on
      // by default in WKWebView; we only strictly need to load the URL.
      final controller = wva.WebViewController()..loadRequest(Uri.parse(url));
      if (!mounted) return;
      setState(() => _wvaController = controller);
    } catch (e, st) {
      debugPrint('StellariumView(webview_all): init failed: $e\n$st');
      if (mounted) setState(() => _unavailable = true);
    }
  }

  @override
  void dispose() {
    unawaited(_eventSub?.cancel());
    _searchCtrl.dispose();
    unawaited(_controller?.dispose());
    // wva.WebViewController has no dispose() in the webview_flutter API; its
    // platform view is torn down with the widget.
    super.dispose();
  }

  // Handle an event the planetarium page posted back through the loopback
  // server's reverse channel. Today the only event is the framing panel's
  // "add to sequence": the page owns the framing geometry but not the daemon's
  // NINA sequence DOM, so it hands the target's coordinates here and we build +
  // create the sequence with the shared Dart builder.
  Future<void> _onPageEvent(Map<String, Object?> event) async {
    if (event['type'] != 'addToSequence') return;
    final raDeg = (event['raDeg'] as num?)?.toDouble();
    final decDeg = (event['decDeg'] as num?)?.toDouble();
    if (raDeg == null || decDeg == null) return;
    final name = (event['name'] as String?)?.trim();
    final targetName = (name == null || name.isEmpty) ? 'Target' : name;

    final api = ref.read(sequenceApiProvider);
    final messenger = ScaffoldMessenger.of(context);
    if (api == null) {
      messenger.showSnackBar(const SnackBar(
        content: Text('Connect to a server before creating a Run.'),
        backgroundColor: AraColors.accentError,
      ));
      return;
    }
    try {
      await api.create(
        targetName,
        buildSlewTargetBody(raDeg: raDeg, decDeg: decDeg, targetName: targetName),
      );
    } catch (e, st) {
      debugPrint('[planning] framing create-run failed: $e\n$st');
      if (mounted) {
        messenger.showSnackBar(const SnackBar(
          content: Text("Couldn't create the Run. Check the connection and try again."),
          backgroundColor: AraColors.accentError,
        ));
      }
      return;
    }
    if (!mounted) return;
    ref.invalidate(sequenceListProvider);
    messenger.showSnackBar(
      SnackBar(content: Text('Created a Run for "$targetName".')),
    );
  }

  // The webview can't receive keyboard text or a JS-bridge call, so the typed
  // search lives in Flutter and reaches the planetarium page through the loopback
  // server's one-shot command channel (the page polls it).
  void _pushCmd(Map<String, Object?> cmd) => _server?.pushCommand(jsonEncode(cmd));

  void _submitSearch() {
    final q = _searchCtrl.text.trim();
    if (q.isEmpty) return;
    _pushCmd({'type': 'search', 'q': q});
  }

  @override
  Widget build(BuildContext context) {
    if (_unavailable) return const _Unavailable();

    final Widget renderer;
    if (kUseWebViewAll) {
      final c = _wvaController;
      if (c == null) return const _Loading();
      renderer = wva.WebViewWidget(controller: c);
    } else {
      final c = _controller;
      if (c == null) return const _Loading();
      renderer = c.webviewWidget;
    }

    return ColoredBox(
      color: AraColors.bgPrimary,
      child: Column(
        children: [
          _SearchBar(
            controller: _searchCtrl,
            onSubmit: _submitSearch,
            onTonight: () => _pushCmd({'type': 'tonight'}),
            spike: kUseWebViewAll,
          ),
          Expanded(child: renderer),
        ],
      ),
    );
  }
}

/// Thin top bar over the planetarium: a universal search field + a Tonight's Sky
/// toggle. The field is Flutter (so the keyboard works); submitting it hands the
/// query to the page via the loopback command channel. When the [spike] renderer
/// is active a small badge marks it, so the A/B test is unambiguous.
class _SearchBar extends StatelessWidget {
  final TextEditingController controller;
  final VoidCallback onSubmit;
  final VoidCallback onTonight;
  final bool spike;

  const _SearchBar({
    required this.controller,
    required this.onSubmit,
    required this.onTonight,
    this.spike = false,
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
          if (spike) ...[
            const SizedBox(width: 8),
            Tooltip(
              message: 'Spike renderer: native WKWebView (webview_all)',
              child: Container(
                padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                decoration: BoxDecoration(
                  color: AraColors.accentInfo.withValues(alpha: 0.15),
                  borderRadius: BorderRadius.circular(6),
                  border: Border.all(color: AraColors.accentInfo),
                ),
                child: const Text('WKWebView',
                    style: TextStyle(fontSize: 11, color: AraColors.accentInfo)),
              ),
            ),
          ],
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
