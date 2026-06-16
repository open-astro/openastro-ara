import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:webview_cef/webview_cef.dart';

import '../../state/imaging/fov_box.dart';
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

/// Builds the JS that draws (or clears) the §36 Frame-mode framing overlay. A
/// null [box] clears it; otherwise the page's `araSetFovBox` draws a
/// [FovBox.cols]×[FovBox.rows] mosaic of [FovBox.widthDeg] × [FovBox.heightDeg]
/// degree panels at [FovBox.rotationDeg]° with [FovBox.overlapPct] overlap,
/// centred on (and tracking) the current view centre. All args are plain numbers,
/// so there's no injection surface (unlike a name string). Exposed for testing.
@visibleForTesting
String fovBoxScript(FovBox? box) {
  if (box == null) return 'window.araClearFovBox && window.araClearFovBox();';
  String n(double v) => v.toStringAsFixed(6);
  return 'window.araSetFovBox && window.araSetFovBox('
      '${n(box.widthDeg)}, ${n(box.heightDeg)}, ${n(box.rotationDeg)}, '
      '${box.cols}, ${box.rows}, ${n(box.overlapPct)});';
}

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

  // Same idea for the Frame-mode FOV box: a box set before the browser is ready
  // is stashed and drawn once the controller exists. `false` means "no box set
  // yet" (distinct from a deliberate null = clear), so we only push an initial
  // clear if Frame mode was toggled before init.
  FovBox? _pendingFovBox;
  bool _hasPendingFovBox = false;

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

  // The provider's current target, or null when no search has been made.
  String? _currentSearch() {
    final q = ref.read(skyAtlasSearchProvider);
    return q.isEmpty ? null : q;
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

  // Draw or clear the Frame-mode FOV box. Stashed until the browser is ready.
  void _setFovBox(FovBox? box) {
    final controller = _controller;
    if (controller == null) {
      _pendingFovBox = box;
      _hasPendingFovBox = true;
      return;
    }
    unawaited(_runFovBox(controller, box));
  }

  Future<void> _runFovBox(WebViewController controller, FovBox? box) async {
    try {
      await controller.executeJavaScript(fovBoxScript(box));
    } catch (e, st) {
      // Same degrade-not-crash contract as _runGoto: a JS-bridge hiccup leaves
      // the overlay as-is rather than tearing down the tab.
      debugPrint('AladinView: FOV box update failed: $e\n$st');
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
      // Apply a target the user searched for before the browser was ready —
      // or, since this tab unmounts on switch, the last search the provider
      // still holds, so returning to the atlas restores it instead of resetting
      // to the default target.
      final pending = _pendingGoto ?? _currentSearch();
      if (pending != null) {
        _pendingGoto = null;
        unawaited(_runGoto(controller, pending));
      }
      // Restore the Frame-mode overlay: a box toggled before the browser was
      // ready, or — since this tab unmounts on switch — the box the provider
      // still holds, so returning to Planning re-draws it.
      final box = _hasPendingFovBox ? _pendingFovBox : ref.read(frameFovBoxProvider);
      _hasPendingFovBox = false;
      _pendingFovBox = null;
      if (box != null) {
        unawaited(_runFovBox(controller, box));
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
    // The Frame-mode FOV box (null when Frame is off or optics are unset) → draw
    // or clear the overlay.
    ref.listen<FovBox?>(frameFovBoxProvider, (_, next) => _setFovBox(next));

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

  // §36/§25.5 Frame-mode FOV overlay. araSetFovBox stores the rectangle (W/H in
  // degrees + position angle) and draws it centred on the current view centre;
  // araClearFovBox removes it. A 'positionChanged' handler redraws on pan/zoom so
  // the box tracks the centre. Driven from Dart via AladinView.fovBoxScript.
  var araFovOverlay = null;
  var araFovBox = null; // { w, h, rot (deg), cols, rows, overlap (%) }, or null
  function araEnsureFovOverlay() {
    if (!araAladin) return null;
    if (!araFovOverlay) {
      araFovOverlay = A.graphicOverlay({ color: '#4fc3f7', lineWidth: 2 });
      araAladin.addOverlay(araFovOverlay);
    }
    return araFovOverlay;
  }
  function araRedrawFovBox() {
    var ov = araEnsureFovOverlay();
    if (!ov) return;
    ov.removeAll();
    if (!araFovBox || !araAladin) return;
    var c = araAladin.getRaDec(); // [raDeg, decDeg] of the current centre
    var ra0 = c[0], dec0 = c[1];
    var w = araFovBox.w, h = araFovBox.h;
    var cols = araFovBox.cols > 0 ? araFovBox.cols : 1;
    var rows = araFovBox.rows > 0 ? araFovBox.rows : 1;
    // Centre-to-centre spacing fraction. Overlap is clamped to [0, 0.95] so a
    // pathological value (the UI slider caps at 50%, but be defensive) can't
    // collapse every panel onto one point (overlap=100) or invert the grid.
    var ovFrac = Math.min(0.95, Math.max(0, (araFovBox.overlap || 0) / 100));
    var stepW = w * (1 - ovFrac), stepH = h * (1 - ovFrac);
    var th = araFovBox.rot * Math.PI / 180;
    var cosT = Math.cos(th), sinT = Math.sin(th);
    // RA degrees compress by cos(dec) near the poles; guard the singularity.
    // The correction is taken at the view centre and reused for every panel — a
    // flat-sky approximation that drifts at the edges of a large mosaic /
    // near the poles, which is acceptable for a planning overlay.
    var cosd = Math.cos(dec0 * Math.PI / 180);
    if (Math.abs(cosd) < 1e-6) cosd = (cosd < 0 ? -1e-6 : 1e-6);
    var hw = w / 2, hh = h / 2;
    var corners = [[-hw, -hh], [hw, -hh], [hw, hh], [-hw, hh]];
    // Draw cols×rows panels, the whole grid rotated by the position angle about
    // the view centre. cols=rows=1 → a single FOV box (gx=gy=0).
    for (var iy = 0; iy < rows; iy++) {
      for (var ix = 0; ix < cols; ix++) {
        var gx = (ix - (cols - 1) / 2) * stepW; // panel-centre offset in the grid frame
        var gy = (iy - (rows - 1) / 2) * stepH;
        var poly = corners.map(function (p) {
          var lx = p[0] + gx, ly = p[1] + gy;
          var dx = lx * cosT - ly * sinT;
          var dy = lx * sinT + ly * cosT;
          return [ra0 + dx / cosd, dec0 + dy];
        });
        ov.add(A.polygon(poly));
      }
    }
  }
  function araSetFovBox(widthDeg, heightDeg, rotationDeg, cols, rows, overlapPct) {
    araFovBox = { w: widthDeg, h: heightDeg, rot: rotationDeg, cols: cols, rows: rows, overlap: overlapPct };
    araRedrawFovBox();
  }
  function araClearFovBox() {
    araFovBox = null;
    if (araFovOverlay) araFovOverlay.removeAll();
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
      // Keep the FOV box centred as the user pans/zooms, and draw any box that
      // Dart pushed before Aladin finished initializing.
      araAladin.on('positionChanged', function () { araRedrawFovBox(); });
      araAladin.on('zoomChanged', function () { araRedrawFovBox(); });
      araRedrawFovBox();
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
