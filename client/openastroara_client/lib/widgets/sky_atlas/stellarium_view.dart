import 'dart:async';
import 'dart:convert';
import 'dart:io' show Platform;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';
import 'package:webview_all/webview_all.dart' as wva;

import '../../models/sequence/slew_target_body.dart';
import '../../services/stellarium_server.dart';
import '../../state/saved_server_state.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../state/sky_atlas/site_location_state.dart';
import '../../theme/ara_colors.dart';

/// §36 Planetarium — the embedded Stellarium Web Engine (AGPL; see
/// `assets/stellarium/LICENSE-AGPL-3.0.txt`), rendered in the platform's **native
/// webview** via `webview_all` (WKWebView on macOS/iOS, WebView2 on Windows,
/// WebKitGTK on Linux, System WebView on Android). The page is **self-driven**:
/// this widget just loads it with the observer location and the daemon's API base
/// in the URL query, and the page does everything else (sets its observer, runs its
/// own on-screen controls, talks to the daemon API). There is deliberately no
/// Dart→page JS bridge — Flutter↔page traffic goes over the loopback server.
class StellariumView extends ConsumerStatefulWidget {
  const StellariumView({super.key});

  @override
  ConsumerState<StellariumView> createState() => _StellariumViewState();
}

class _StellariumViewState extends ConsumerState<StellariumView> {
  wva.WebViewController? _controller;
  StellariumServer? _server;
  StreamSubscription<Map<String, Object?>>? _eventSub;
  final _searchCtrl = TextEditingController();
  bool _unavailable = false;

  // Linux only: the URL we open in the system browser instead of embedding.
  // Flutter's GTK embedder can't give an embedded webview platform view a shared
  // GL surface (flutter/flutter#88168) — creating the WebKitGTK view poisons the
  // whole app's Skia GL context and the window goes blank. So on Linux we serve
  // the same loopback page and launch it in the user's browser (full WebGL2).
  String? _externalUrl;

  @override
  void initState() {
    super.initState();
    unawaited(_init());
  }

  Future<void> _init() async {
    // Pin the autoDispose site/server providers for the duration of init. Without
    // this, awaiting `siteLocationProvider.future` across the async gaps below
    // races the provider's autodisposal ("Cannot use the Ref … after it has been
    // disposed").
    final keepSite = ref.listenManual(siteLocationProvider, (_, _) {});
    final keepServers = ref.listenManual(savedServersProvider, (_, _) {});
    try {
      // Start the shared loopback asset server + build the page URL. The native
      // webview loads the self-driven page; all Flutter↔page comms go through this
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

      // Linux: don't embed (see [_externalUrl]). Open the loopback page in the
      // system browser instead; the Flutter↔page command/event channels are
      // loopback HTTP, so the search bar + add-to-sequence keep working against
      // the browser page.
      if (Platform.isLinux) {
        setState(() => _externalUrl = url);
        unawaited(_openInBrowser());
        return;
      }

      try {
        // Keep this minimal: webview_all notes some setters are unimplemented on
        // certain platforms, and calling one would abort the whole init. JS is on
        // by default in the native webviews; we only strictly need to load the URL.
        final controller = wva.WebViewController()..loadRequest(Uri.parse(url));
        if (!mounted) return;
        setState(() => _controller = controller);
      } catch (e, st) {
        debugPrint('StellariumView: webview init failed: $e\n$st');
        if (mounted) setState(() => _unavailable = true);
      }
    } finally {
      keepSite.close();
      keepServers.close();
    }
  }

  @override
  void dispose() {
    unawaited(_eventSub?.cancel());
    _searchCtrl.dispose();
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
    // NOTE: the page also sends `rotationDeg` (the framing position angle), but the
    // sequence target built below is a SlewScopeToRaDec instruction, which has no PA
    // field — carrying the framing PA into the Run needs a CenterAndRotate / rotator
    // instruction. Tracked in the NINA sequencer-fidelity epic (design/PORT_TODO.md);
    // until then the PA is deliberately not applied (the overlay is preview-only).

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

  // Linux: open the loopback planetarium page in the system browser.
  Future<void> _openInBrowser() async {
    final url = _externalUrl;
    if (url == null) return;
    try {
      final ok = await launchUrl(Uri.parse(url),
          mode: LaunchMode.externalApplication);
      if (!ok) debugPrint('StellariumView: launchUrl returned false for $url');
    } catch (e, st) {
      debugPrint('StellariumView: could not open browser: $e\n$st');
    }
  }

  @override
  Widget build(BuildContext context) {
    if (_unavailable) return const _Unavailable();
    final external = _externalUrl;
    final c = _controller;
    // Still initialising (server + URL not ready on either path).
    if (external == null && c == null) return const _Loading();

    return ColoredBox(
      color: AraColors.bgPrimary,
      child: Column(
        children: [
          // The search bar drives the page over the loopback command channel, so
          // it works for the embedded webview AND the Linux browser page.
          _SearchBar(
            controller: _searchCtrl,
            onSubmit: _submitSearch,
            onTonight: () => _pushCmd({'type': 'tonight'}),
          ),
          Expanded(
            child: external != null
                ? _BrowserLaunchPanel(onOpen: _openInBrowser)
                : wva.WebViewWidget(controller: c!),
          ),
        ],
      ),
    );
  }
}

/// Linux Planning surface. Flutter's GTK embedder can't give an embedded webview
/// a shared GL rendering surface (flutter/flutter#88168) — the WebKitGTK platform
/// view blanks the whole app — so on Linux the planetarium opens in the system
/// browser (full WebGL2) off the same loopback server. The page auto-opens once
/// on first load; this panel offers a manual reopen.
class _BrowserLaunchPanel extends StatelessWidget {
  final Future<void> Function() onOpen;

  const _BrowserLaunchPanel({required this.onOpen});

  @override
  Widget build(BuildContext context) {
    return Container(
      color: AraColors.bgPrimary,
      child: Center(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const Icon(Icons.public, size: 88, color: AraColors.textDisabled),
            const SizedBox(height: 12),
            Text('Planetarium opens in your browser',
                style: Theme.of(context).textTheme.titleMedium),
            const SizedBox(height: 6),
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 24),
              child: Text(
                'On Linux the sky map runs in your web browser for full WebGL2 '
                'support. The search bar above still controls it. It should have '
                'opened automatically — use the button if it didn’t.',
                textAlign: TextAlign.center,
                style: Theme.of(context)
                    .textTheme
                    .bodySmall
                    ?.copyWith(color: AraColors.textSecondary),
              ),
            ),
            const SizedBox(height: 16),
            FilledButton.icon(
              onPressed: () => onOpen(),
              icon: const Icon(Icons.open_in_new, size: 18),
              label: const Text('Open planetarium'),
            ),
          ],
        ),
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
                  'Reopen the app; if it persists, check that a system WebView is available.',
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
