import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:webview_cef/webview_cef.dart';

import '../../state/sky_atlas/sky_atlas_state.dart';
import '../../theme/ara_colors.dart';

/// §36 Sky Atlas — the embedded Aladin Lite (CDS) sky atlas.
///
/// Per PORT_DECISIONS.md the cross-desktop embed uses `webview_cef`: a
/// Chromium/CEF browser rendered to a Flutter texture, so the atlas composites
/// directly into the widget tree (under the search bar + mode toggle) and
/// renders identically in-tab on macOS, Windows, and Linux — one code path.
///
/// This widget owns the CEF browser lifecycle and surfaces loading / unavailable
/// states, and drives the §36 universal-search "goto" (a submitted target name
/// or coordinates moves the atlas via Aladin's `gotoObject`). The Tonight's-Sky
/// projection wiring layers on in a later slice.
class AladinView extends ConsumerStatefulWidget {
  const AladinView({super.key});

  @override
  ConsumerState<AladinView> createState() => _AladinViewState();
}

/// Builds the JS that moves the atlas to [target]. The target is JSON-encoded so
/// a name containing quotes/backslashes (or anything else) can't break out of
/// the string literal or inject script — it's always a single string argument
/// to the page's `araGoto` helper. Exposed for testing.
@visibleForTesting
String gotoScript(String target) => 'window.araGoto && window.araGoto(${jsonEncode(target)});';

// CEF's manager is a process-wide singleton; initialize it at most once so a
// tab rebuild (switch away + back) can't re-run native init. A FAILED init is
// not cached: _init() clears this on a manager-init failure so a later mount
// (e.g. after the user installs a missing runtime) can retry rather than being
// stuck "unavailable" for the whole process lifetime.
Future<void>? _managerInit;
Future<void> _ensureManagerInitialized() =>
    _managerInit ??= WebviewManager().initialize();

class _AladinViewState extends ConsumerState<AladinView> {
  WebViewController? _controller;
  bool _unavailable = false;

  // A target submitted before the browser finished initializing is held here
  // and applied once the controller is ready, so an early search isn't dropped.
  String? _pendingGoto;

  @override
  void initState() {
    super.initState();
    unawaited(_init());
  }

  // Move the atlas to a submitted target. If the browser isn't ready yet, stash
  // the target and apply it when init completes.
  void _goto(String target) {
    if (target.isEmpty) return;
    final controller = _controller;
    if (controller == null) {
      _pendingGoto = target;
      return;
    }
    unawaited(_runGoto(controller, target));
  }

  Future<void> _runGoto(WebViewController controller, String target) async {
    try {
      await controller.executeJavaScript(gotoScript(target));
    } catch (e, st) {
      // A goto failure (browser torn down mid-call, JS bridge hiccup) must not
      // crash the tab — the atlas simply stays where it was.
      debugPrint('AladinView: goto "$target" failed: $e\n$st');
    }
  }

  Future<void> _init() async {
    // Manager init is separated from browser init so only a manager-init failure
    // resets the memoized future (allowing a retry); a browser-init failure
    // leaves the successfully-initialized manager in place.
    try {
      await _ensureManagerInitialized();
    } catch (e, st) {
      // Native CEF couldn't start — an unsupported host, a missing Chromium
      // runtime, or the headless `flutter test` environment (no plugin
      // registrant). Catch ALL throwables (not just Exception): a native
      // plugin layer can surface Error subtypes (StateError/UnsupportedError),
      // and the guarantee here is "never crash the tab" — but it's logged, not
      // silently swallowed. Clear the cache so a later mount retries, degrade.
      _managerInit = null;
      debugPrint('AladinView: CEF manager init failed: $e\n$st');
      if (mounted) setState(() => _unavailable = true);
      return;
    }
    // Bail before allocating a native browser if the tab was disposed during
    // manager init (the dispose guard after controller init still cleans up,
    // but this avoids the wasted allocation entirely).
    if (!mounted) return;
    try {
      final controller = WebviewManager().createWebView(loading: const _Loading());
      await controller.initialize(_aladinDataUrl);
      if (!mounted) {
        // The tab was disposed mid-init — release the browser we just created.
        await controller.dispose();
        return;
      }
      setState(() => _controller = controller);
      // Apply a target the user searched for before the browser was ready.
      final pending = _pendingGoto;
      if (pending != null) {
        _pendingGoto = null;
        unawaited(_runGoto(controller, pending));
      }
    } catch (e, st) {
      // Same rationale as above: degrade-not-crash, but logged.
      debugPrint('AladinView: Aladin browser init failed: $e\n$st');
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
    // A newly-submitted search target (the provider holds the last one) moves
    // the atlas. Empty (initial) state is ignored by _goto.
    ref.listen<String>(skyAtlasSearchProvider, (_, next) => _goto(next));

    if (_unavailable) return const _Unavailable();
    final controller = _controller;
    if (controller == null) return const _Loading();
    return ColoredBox(color: AraColors.bgPrimary, child: controller.webviewWidget);
  }
}

// The Aladin Lite bootstrap, handed to CEF as a base64 `data:` URL so no temp
// file or local HTTP server is needed. The page pulls Aladin Lite v3 from the
// CDS CDN, pinned to a specific version (NOT `/latest/`) so a remote breaking
// change can't silently alter the production atlas. Bundling the ~5 MB JS for
// offline use — which also removes the runtime CDN trust + reachability
// dependency entirely — is the next §36 slice (per §36.1). The CDS logo +
// attribution Aladin renders bottom-right satisfies §36/§17.
final String _aladinDataUrl =
    'data:text/html;base64,${base64Encode(utf8.encode(_aladinBootstrapHtml))}';

const String _aladinBootstrapHtml = '''
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<style>
  html, body { margin: 0; padding: 0; width: 100%; height: 100%; background: #000; overflow: hidden; }
  #aladin-lite-div { width: 100%; height: 100%; }
  #atlas-error {
    display: none; position: absolute; top: 0; left: 0; width: 100%; height: 100%;
    box-sizing: border-box; padding: 0 24px; color: #9aa0a6; background: #000;
    font: 14px/1.5 sans-serif; text-align: center;
    align-items: center; justify-content: center; flex-direction: column;
  }
</style>
<script>
  // Shown if the Aladin Lite script can't be fetched (no internet, DNS, CDN
  // outage). Without this the data: page loads fine but the atlas is a blank
  // black rectangle with no feedback.
  function aladinLoadFailed() {
    var el = document.getElementById('atlas-error');
    if (el) el.style.display = 'flex';
  }
</script>
<script src="https://aladin.cds.unistra.fr/AladinLite/api/v3/3.6.1/aladin.js" charset="utf-8" onerror="aladinLoadFailed()"></script>
</head>
<body>
<div id="aladin-lite-div"></div>
<div id="atlas-error">Could not load the sky atlas.<br>Check your internet connection, then reopen the Sky Atlas.</div>
<script>
  // The Aladin instance + a goto helper, exposed on window so the Dart side can
  // drive it via executeJavaScript (see AladinView.gotoScript). araGoto resolves
  // a target name (Simbad/Sesame) or coordinates and recenters the atlas.
  var araAladin = null;
  var araPendingTarget = null;
  function araGoto(target) {
    if (!target) return;
    if (araAladin) {
      araAladin.gotoObject(target, { error: function () { /* name not resolved — leave the view put */ } });
    } else {
      // Aladin's async init (A.init + WASM load) hasn't resolved yet — a goto
      // fired now would be dropped, so remember the latest target and apply it
      // from the A.init.then callback once araAladin exists.
      araPendingTarget = target;
    }
  }
  // A normal (non-async) external script blocks parsing until it loads or
  // errors, so by here `A` is defined on success and undefined on failure.
  if (typeof A !== 'undefined') {
    A.init.then(function () {
      araAladin = A.aladin('#aladin-lite-div', {
        survey: 'P/DSS2/color',
        fov: 60,
        target: 'M31',
        showCooGrid: true,
        showSimbadPointerControl: true,
      });
      // Apply a target searched for during Aladin's init window.
      if (araPendingTarget) {
        var t = araPendingTarget;
        araPendingTarget = null;
        araGoto(t);
      }
    });
  } else {
    aladinLoadFailed();
  }
</script>
</body>
</html>
''';

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
                'Sky atlas unavailable',
                style: Theme.of(context).textTheme.titleMedium,
              ),
              const SizedBox(height: 6),
              Padding(
                padding: const EdgeInsets.symmetric(horizontal: 24),
                child: Text(
                  'The embedded Aladin Lite renderer could not start on this host. '
                  'A Chromium runtime is required for the in-app sky atlas — '
                  'install it, then close and reopen the app.',
                  textAlign: TextAlign.center,
                  style: Theme.of(context).textTheme.bodySmall?.copyWith(
                        color: AraColors.textSecondary,
                      ),
                ),
              ),
            ],
          ),
        ),
      );
}
