import 'dart:async';
import 'dart:convert';
import 'dart:io' show Platform;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:webview_all/webview_all.dart' as wva;

import '../../services/planetarium_prefs_service.dart';
import '../../services/stellarium_server.dart';
import '../../state/saved_server_state.dart';
import '../../state/sequencer/create_imaging_run.dart';
import '../../state/sky_atlas/site_location_state.dart';
import '../../state/sky_atlas/sky_atlas_state.dart';
import '../../theme/ara_colors.dart';
import 'linux_planetarium_overlay.dart';
import 'tonight_sky_panel.dart';

/// §36 Planetarium — the embedded Stellarium Web Engine (AGPL; see
/// `assets/stellarium/LICENSE-AGPL-3.0.txt`), rendered in the platform's **native
/// webview**. macOS/iOS (WKWebView) and Windows (WebView2) embed via `webview_all`.
/// Linux uses a **native GTK overlay** instead (`LinuxPlanetariumOverlay` +
/// `linux/runner/planetarium_overlay.cc`): a real `WebKitWebView` composited over
/// `FlView`, because `webview_all`'s texture-based platform view blanks the whole
/// app on Flutter's GTK embedder (flutter/flutter#88168). The page is **self-driven**:
/// this widget just loads it with the observer location and the daemon's API base
/// in the URL query, and the page does everything else (sets its observer, runs its
/// own on-screen controls, talks to the daemon API). There is deliberately no
/// Dart→page JS bridge — Flutter↔page traffic goes over the loopback server.
///
/// Two pieces of Flutter chrome wrap the renderer: a top search bar, and the
/// docked **Tonight's Sky** panel (§36.8). The "Tonight's Sky" button toggles
/// `skyAtlasModeProvider`; in `tonightsSky` mode the [TonightSkyPanel] docks on
/// the right while the planetarium stays in an `Expanded` (its rect shrinks and
/// the native-overlay bounds recompute through the existing bounds logic). The
/// panel occupies its own rect beside the webview — never overlaid on it —
/// because the native webview composites ABOVE Flutter, so an overlaid panel
/// wouldn't reliably paint (platform-view occlusion). Panel→page actions (the
/// recentre button) ride [planetariumCommandProvider] over the loopback server,
/// since there is no Dart→page JS bridge.
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
  final _prefsService = PlanetariumPrefsService();
  bool _unavailable = false;

  // Linux only: the loopback URL handed to the native GTK overlay
  // ([LinuxPlanetariumOverlay]). Flutter's GTK embedder can't give an embedded
  // webview platform view a shared GL surface (flutter/flutter#88168) — creating
  // the texture-based WebKitGTK view poisons the whole app's Skia GL context and
  // the window goes blank. So on Linux we composite a native WebKitWebView over
  // FlView in its own GTK surface instead of going through a Flutter platform view.
  String? _linuxOverlayUrl;

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
        final site =
            ref.read(siteLocationProvider).asData?.value ??
            await ref.read(siteLocationProvider.future);
        if (!mounted) return;
        final servers = await ref.read(savedServersProvider.future);
        if (!mounted) return;
        final api = servers.isNotEmpty ? servers.last.baseUrl : '';
        // Saved Display-panel toggles + `cat:`-namespaced Catalogs overlays
        // (empty on first run → the page keeps its defaults). The page applies
        // these on load and posts changes back via _onPageEvent, so a user's
        // layer + catalog choices survive relaunch.
        final savedPrefs = await _prefsService.load();
        if (!mounted) return;
        final query =
            {
                  'lat': (site?.latitudeDeg ?? 0).toString(),
                  'lon': (site?.longitudeDeg ?? 0).toString(),
                  'elev': (site?.elevationM ?? 0).toString(),
                  'api': api,
                  'prefs': jsonEncode(savedPrefs),
                }.entries
                .map((e) => '${e.key}=${Uri.encodeQueryComponent(e.value)}')
                .join('&');
        url = '${server.baseUrl}/index.html?$query';
      } catch (e, st) {
        debugPrint('StellariumView: asset server / site read failed: $e\n$st');
        if (mounted) setState(() => _unavailable = true);
        return;
      }
      if (!mounted) return;

      // Linux: don't go through a Flutter platform view (see [_linuxOverlayUrl]).
      // Hand the loopback URL to the native GTK overlay, which composites a real
      // WebKitWebView over FlView in-window. The Flutter↔page command/event
      // channels are loopback HTTP, so the search bar + add-to-sequence keep
      // working against the overlay page exactly as on mac/Windows.
      if (Platform.isLinux) {
        setState(() => _linuxOverlayUrl = url);
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
    // The page posts its Display-panel toggle state here on every change; persist it
    // so it's restored (via the load URL) next launch.
    if (event['type'] == 'displayPref') {
      final layers = event['layers'];
      if (layers is Map) {
        final prefs = <String, bool>{};
        layers.forEach((k, v) {
          if (k is String && v is bool) prefs[k] = v;
        });
        unawaited(_prefsService.save(prefs));
      }
      return;
    }
    if (event['type'] != 'addToSequence') return;
    final raDeg = (event['raDeg'] as num?)?.toDouble();
    final decDeg = (event['decDeg'] as num?)?.toDouble();
    if (raDeg == null || decDeg == null) return;
    final name = (event['name'] as String?)?.trim();
    final targetName = (name == null || name.isEmpty) ? 'Target' : name;
    // §36/§38 — a DIALED framing position angle rides into the run as a
    // Center and Rotate at that angle (the rotate half executes since #806).
    // The dial's untouched default (0°) keeps the plain blind slew: carrying
    // it would make every framing run require a configured plate solver, and
    // 0° almost always means "I didn't rotate", not "hold exactly 0°" — a
    // deliberate 0° framing can still add centring in the sequence editor.
    final rotationDeg = (event['rotationDeg'] as num?)?.toDouble();
    final positionAngleDeg =
        (rotationDeg != null && rotationDeg != 0) ? rotationDeg : null;

    final messenger = ScaffoldMessenger.of(context);
    ImagingRunResult? result;
    try {
      // A full imaging run (cool/unpark/track/slew/AF + exposure loop from the
      // user's Imaging Defaults), selected + brought up in the Run tab — or,
      // with a sequence already open, this target appended to it.
      result = await createImagingRun(
        ref,
        raDeg: raDeg,
        decDeg: decDeg,
        targetName: targetName,
        positionAngleDeg: positionAngleDeg,
      );
    } catch (e, st) {
      debugPrint('[planning] framing create-run failed: $e\n$st');
      if (mounted) {
        showImagingRunFeedback(messenger, targetName: targetName, failed: true);
      }
      return;
    }
    if (!mounted) return;
    showImagingRunFeedback(messenger, targetName: targetName, result: result);
  }

  // The webview can't receive keyboard text or a JS-bridge call, so the typed
  // search lives in Flutter and reaches the planetarium page through the loopback
  // server's one-shot command channel (the page polls it).
  void _pushCmd(Map<String, Object?> cmd) =>
      _server?.pushCommand(jsonEncode(cmd));

  void _submitSearch() {
    final q = _searchCtrl.text.trim();
    if (q.isEmpty) return;
    _pushCmd({'type': 'search', 'q': q});
  }

  // Show/hide the docked Tonight's Sky panel by flipping the shared mode. We do
  // NOT also fire the in-page `{'type':'tonight'}` command: that opens the page's
  // OWN Tonight drawer, which would duplicate the Flutter panel — the docked
  // panel is now the Tonight's Sky UI on every platform.
  void _toggleTonight() => ref.read(skyAtlasModeProvider.notifier).toggle();

  @override
  Widget build(BuildContext context) {
    if (_unavailable) return const _Unavailable();
    final linuxUrl = _linuxOverlayUrl;
    final c = _controller;
    // Still initialising (server + URL not ready on either path).
    if (linuxUrl == null && c == null) return const _Loading();

    // Forward panel→page commands (e.g. the recentre button's `goto`) over the
    // loopback server — the only Dart→page channel the native webview has.
    ref.listen<Map<String, Object?>?>(planetariumCommandProvider, (_, cmd) {
      if (cmd == null) return;
      _pushCmd(cmd);
      // Consume it: clear the bus so a later reader can't mistake this already-
      // forwarded command for a fresh one. (updateShouldNotify ignores the null,
      // so clear() doesn't re-wake this listener.)
      ref.read(planetariumCommandProvider.notifier).clear();
    });

    final tonightOpen =
        ref.watch(skyAtlasModeProvider) == SkyAtlasMode.tonightsSky;

    final planetarium = Expanded(
      child: linuxUrl != null
          ? LinuxPlanetariumOverlay(url: linuxUrl)
          : wva.WebViewWidget(controller: c!),
    );

    return ColoredBox(
      color: AraColors.bgPrimary,
      child: Column(
        children: [
          // The search bar drives the page over the loopback command channel, so
          // it works for the embedded webview AND the Linux native overlay page.
          _SearchBar(
            controller: _searchCtrl,
            onSubmit: _submitSearch,
            onTonight: _toggleTonight,
            tonightOpen: tonightOpen,
          ),
          // Side-by-side: the planetarium keeps its Expanded (so its bounds
          // shrink and the native overlay recomputes) and the panel docks at a
          // fixed width on the right — its own rect, occlusion-safe.
          Expanded(
            child: Row(
              children: [
                planetarium,
                if (tonightOpen)
                  const DecoratedBox(
                    decoration: BoxDecoration(
                      border: Border(left: BorderSide(color: AraColors.border)),
                    ),
                    child: TonightSkyPanel(),
                  ),
              ],
            ),
          ),
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
  // Highlights the toggle while the docked panel is open.
  final bool tonightOpen;

  const _SearchBar({
    required this.controller,
    required this.onSubmit,
    required this.onTonight,
    required this.tonightOpen,
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
                  fontSize: 13,
                  color: AraColors.textSecondary,
                ),
                filled: true,
                fillColor: AraColors.bgPrimary,
                contentPadding: const EdgeInsets.symmetric(
                  horizontal: 8,
                  vertical: 8,
                ),
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
          // A filled (vs outlined) button when the panel is open, so the toggle
          // reads its own state at a glance.
          tonightOpen
              ? FilledButton.icon(
                  onPressed: onTonight,
                  icon: const Icon(Icons.nights_stay, size: 16),
                  label: const Text("Tonight's Sky"),
                )
              : OutlinedButton.icon(
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
          Text(
            'Planetarium unavailable',
            style: Theme.of(context).textTheme.titleMedium,
          ),
          const SizedBox(height: 6),
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 24),
            child: Text(
              'The embedded planetarium renderer could not start on this host. '
              'Reopen the app; if it persists, check that a system WebView is available.',
              textAlign: TextAlign.center,
              style: Theme.of(
                context,
              ).textTheme.bodySmall?.copyWith(color: AraColors.textSecondary),
            ),
          ),
        ],
      ),
    ),
  );
}
