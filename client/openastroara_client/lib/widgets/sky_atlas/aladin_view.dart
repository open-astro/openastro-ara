import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart' show rootBundle;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:webview_cef/webview_cef.dart';

import '../../models/catalog_object.dart';
import '../../services/horizon_api.dart';
import '../../state/imaging/fov_box.dart';
import '../../state/sky_atlas/catalog_overlay_state.dart';
import '../../state/sky_atlas/horizon_overlay_state.dart';
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

/// Builds the JS that switches the atlas's base imagery survey to [surveyId] (a
/// CDS HiPS id). JSON-encoded like [gotoScript] so the id is always a single
/// string argument to the page's `araSetSurvey` helper (no injection surface).
/// Exposed for testing.
@visibleForTesting
String surveyScript(String surveyId) =>
    'window.araSetSurvey && window.araSetSurvey(${jsonEncode(surveyId)});';

/// Builds the JS that draws (or replaces) the §36 installed-catalog marker
/// overlay from [objects]. The whole list is JSON-encoded as a single argument
/// to the page's `araAddCatalog`, so a star/DSO name containing quotes or markup
/// can't break out of the literal or inject script — the same safety contract as
/// [gotoScript]. An empty list clears the overlay. Exposed for testing.
@visibleForTesting
String catalogScript(List<CatalogObject> objects) {
  final payload = objects
      .map((o) => <String, dynamic>{
            'name': o.name,
            'ra': o.raDeg,
            'dec': o.decDeg,
            if (o.magnitude != null) 'mag': o.magnitude,
          })
      .toList(growable: false);
  return 'window.araAddCatalog && window.araAddCatalog(${jsonEncode(payload)});';
}

/// Builds the JS that clears the installed-catalog marker overlay. Exposed for testing.
@visibleForTesting
String clearCatalogScript() => 'window.araClearCatalog && window.araClearCatalog();';

/// Builds the JS that draws (or replaces) the §36 local-horizon overlay from
/// [horizon]: the horizon curve (a closed RA/Dec polyline), the zenith marker,
/// and the N/E/S/W cardinal labels. The whole payload is JSON-encoded as a single
/// argument to the page's `araSetHorizon`, so a value can't break out of the
/// literal — the same safety contract as [catalogScript]. Exposed for testing.
@visibleForTesting
String horizonScript(Horizon horizon) {
  final payload = <String, dynamic>{
    'line': horizon.points
        .map((p) => <double>[p.raDeg, p.decDeg])
        .toList(growable: false),
    'zenith': <double>[horizon.zenith.raDeg, horizon.zenith.decDeg],
    'cardinals': horizon.cardinals
        .map((c) => <String, dynamic>{'label': c.label, 'ra': c.raDeg, 'dec': c.decDeg})
        .toList(growable: false),
  };
  return 'window.araSetHorizon && window.araSetHorizon(${jsonEncode(payload)});';
}

/// Builds the JS that clears the local-horizon overlay. Exposed for testing.
@visibleForTesting
String clearHorizonScript() => 'window.araClearHorizon && window.araClearHorizon();';

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

  // Switch the base imagery survey. Stashed until the browser is ready (the
  // bootstrap applies the latest pending id once Aladin finishes init).
  String? _pendingSurvey;
  void _setSurvey(String surveyId) {
    final controller = _controller;
    if (controller == null) {
      _pendingSurvey = surveyId;
      return;
    }
    unawaited(_runSurvey(controller, surveyId));
  }

  Future<void> _runSurvey(WebViewController controller, String surveyId) async {
    try {
      await controller.executeJavaScript(surveyScript(surveyId));
    } catch (e, st) {
      // Degrade-not-crash: a failed survey swap leaves the current imagery up.
      debugPrint('AladinView: survey switch failed: $e\n$st');
    }
  }

  // Draw (or replace/clear) the installed-catalog marker overlay. Stashed until
  // the browser is ready, then applied from _init.
  List<CatalogObject>? _pendingCatalog;
  void _setCatalog(List<CatalogObject> objects) {
    final controller = _controller;
    if (controller == null) {
      _pendingCatalog = objects;
      return;
    }
    unawaited(_runCatalog(controller, objects));
  }

  Future<void> _runCatalog(WebViewController controller, List<CatalogObject> objects) async {
    try {
      await controller.executeJavaScript(
          objects.isEmpty ? clearCatalogScript() : catalogScript(objects));
    } catch (e, st) {
      // Degrade-not-crash: a failed overlay update leaves the markers as-is.
      debugPrint('AladinView: catalog overlay update failed: $e\n$st');
    }
  }

  // Draw (or clear) the §36 local-horizon overlay. Stashed until the browser is
  // ready, then applied from _init. A null horizon clears the overlay.
  Horizon? _pendingHorizon;
  void _setHorizon(Horizon? horizon) {
    final controller = _controller;
    if (controller == null) {
      _pendingHorizon = horizon;
      return;
    }
    unawaited(_runHorizon(controller, horizon));
  }

  Future<void> _runHorizon(WebViewController controller, Horizon? horizon) async {
    try {
      await controller.executeJavaScript(
          horizon == null ? clearHorizonScript() : horizonScript(horizon));
    } catch (e, st) {
      // Degrade-not-crash: a failed horizon update leaves the overlay as-is.
      debugPrint('AladinView: horizon overlay update failed: $e\n$st');
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
    // Build the self-contained bootstrap page (the bundled Aladin Lite JS
    // inlined into a data: URL). A failure here means the JS asset is missing
    // from the bundle (a build misconfiguration) — degrade like a CEF failure.
    final String dataUrl;
    try {
      dataUrl = await _ensureAladinDataUrl();
    } catch (e, st) {
      debugPrint('AladinView: bundled Aladin Lite JS load failed: $e\n$st');
      if (mounted) setState(() => _unavailable = true);
      return;
    }
    if (!mounted) return;
    try {
      final controller = WebviewManager().createWebView(loading: const _Loading());
      await controller.initialize(dataUrl);
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
      // Restore the chosen survey: one stashed before the browser was ready, or
      // — since this tab unmounts on switch — the provider's current selection.
      // Only push when it differs from the bootstrap's hard-coded default, so a
      // fresh mount doesn't needlessly reload the same imagery.
      final String survey = _pendingSurvey ?? ref.read(skyAtlasSurveyProvider);
      _pendingSurvey = null;
      if (survey != kDefaultSkySurveyId) {
        unawaited(_runSurvey(controller, survey));
      }
      // Restore the installed-catalog overlay: markers stashed before the browser
      // was ready, or — since this tab unmounts on switch — the provider's current
      // value so returning to Planning re-draws them. Only push a non-empty set on
      // a fresh mount (an empty overlay is the default, nothing to draw).
      final catalog = _pendingCatalog ?? ref.read(skyAtlasCatalogProvider).asData?.value;
      _pendingCatalog = null;
      if (catalog != null && catalog.isNotEmpty) {
        unawaited(_runCatalog(controller, catalog));
      }
      // Restore the local-horizon overlay: a horizon stashed before the browser
      // was ready, or — since this tab unmounts on switch — the provider's current
      // value so returning to Planning re-draws it.
      final horizon = _pendingHorizon ?? ref.read(horizonProvider).asData?.value;
      _pendingHorizon = null;
      if (horizon != null) {
        unawaited(_runHorizon(controller, horizon));
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
    // The selected base imagery survey → swap the atlas's image layer.
    ref.listen<String>(skyAtlasSurveyProvider, (_, next) => _setSurvey(next));
    // The installed-catalog overlay (markers) → draw/replace/clear. Only the
    // data value drives a redraw; loading/error states leave the overlay as-is.
    ref.listen<AsyncValue<List<CatalogObject>>>(skyAtlasCatalogProvider, (_, next) {
      final objects = next.asData?.value;
      if (objects != null) _setCatalog(objects);
    });
    // The local-horizon overlay (curve + zenith + cardinals) → draw/clear. Only
    // the data value drives a redraw; loading/error states leave it as-is.
    ref.listen<AsyncValue<Horizon?>>(horizonProvider, (_, next) {
      if (next.hasValue) _setHorizon(next.value);
    });

    if (_unavailable) return const _Unavailable();
    final controller = _controller;
    if (controller == null) return const _Loading();
    return ColoredBox(color: AraColors.bgPrimary, child: controller.webviewWidget);
  }
}

// The bundled Aladin Lite JS (vendored under assets/aladin/aladin.js, ~1.9 MB,
// pinned to v3.6.1) is inlined into the bootstrap page, written once to a temp
// file, and loaded via a short file:// URL — memoized for the process lifetime so
// re-mounting the (unmount-on-switch) tab doesn't rewrite the file. A failure is
// NOT cached (clears the future) so a later mount — e.g. after a transient I/O
// hiccup — can retry.
//
// Why a file:// URL and not a self-contained `data:` URL: inlining the ~1.9 MB
// engine makes the assembled document ~2.5 MB, and a `data:` URL carries the whole
// document IN the URL string. That exceeds Chromium's max URL length
// (url::kMaxURLChars ≈ 2 MB), so CEF 149 silently rejects the navigation and the
// webview stays blank (no atlas — the symptom that replaced the old crash). The
// temp file keeps the URL short while the engine stays bundled/offline (no CDN, no
// local HTTP server) per §36.1; sky-survey tiles still need internet.
Future<String>? _aladinDataUrl;
// The unique temp dir created for this process's bootstrap file, retained so the
// graceful-exit path can delete it (see disposeAladinTempDir).
Directory? _aladinTempDir;
Future<String> _ensureAladinDataUrl() => _aladinDataUrl ??= _buildAladinDataUrl();

/// Best-effort removal of the bootstrap temp dir. **Exit-path only** — call this
/// solely from the app's onExitRequested handler (after CEF has been quit), never
/// mid-session: it deletes the file a live CEF browser may still be reading and
/// clears the memoized URL, so a concurrent atlas mount could hit a use-after-delete.
/// Only ever touches *this* process's own dir (a uniquely-named createTemp result),
/// so it can't race a second instance's live dir. A crash/kill skips it; the unique
/// name means orphans never collide and the OS reaps the temp root.
Future<void> disposeAladinTempDir() async {
  final dir = _aladinTempDir;
  _aladinTempDir = null;
  _aladinDataUrl = null;
  if (dir == null) return;
  try {
    if (dir.existsSync()) await dir.delete(recursive: true);
  } catch (_) {/* best-effort — never let cleanup throw on the exit path */}
}

Future<String> _buildAladinDataUrl() async {
  try {
    final js = await rootBundle.loadString('assets/aladin/aladin.js');
    final html = inlineAladinJs(js);
    // Write into a UNIQUE per-process temp dir rather than a fixed name in the
    // world-writable temp root: the random directory name can't be pre-placed as a
    // hostile symlink before first launch, and it can't collide with a second app
    // instance (e.g. a debug + a release build) racing a shared path. Memoized, so
    // this runs once per process; disposeAladinTempDir deletes it on a clean exit.
    final dir = await Directory.systemTemp.createTemp('openastroara_sky_atlas_');
    _aladinTempDir = dir;
    final file = File('${dir.path}/atlas.html');
    await file.writeAsString(html, flush: true);
    return Uri.file(file.path).toString();
  } catch (_) {
    // Capture our dir reference BEFORE clearing _aladinDataUrl: nulling the memo
    // first would let a concurrent _ensureAladinDataUrl() start a second build that
    // overwrites _aladinTempDir before we read it here (only reachable across an
    // await, but cheap to make order-independent).
    final partial = _aladinTempDir;
    _aladinTempDir = null;
    _aladinDataUrl = null; // don't cache the failure — allow a later retry
    // If the dir was created before the write threw, delete it now so a retry
    // doesn't orphan it (the retry would overwrite _aladinTempDir with a new dir).
    if (partial != null) {
      try {
        if (await partial.exists()) await partial.delete(recursive: true);
      } catch (_) {/* best-effort */}
    }
    rethrow;
  }
}

/// Inlines the bundled Aladin Lite engine [js] into the bootstrap page at the
/// `__ALADIN_LITE_JS__` placeholder. `replaceFirst` inserts the replacement
/// literally (no `$`-substitution), so the minified engine — dense with `$`
/// identifiers — passes through verbatim where Dart string interpolation would
/// have mangled it. Split out so the splice is unit-testable without loading the
/// ~1.9 MB asset. Exposed for testing.
@visibleForTesting
String inlineAladinJs(String js) =>
    _aladinBootstrapHtml.replaceFirst('__ALADIN_LITE_JS__', js);

// The Aladin Lite bootstrap. The assembled document is written to a temp file and
// loaded via a short file:// URL (see _buildAladinDataUrl — a `data:` URL would
// exceed Chromium's ~2 MB max-URL length once the engine is inlined, so CEF 149
// silently rejects it). The Aladin Lite v3 engine is bundled
// (inlined at `__ALADIN_LITE_JS__` from assets/aladin/aladin.js, pinned to a
// specific version — NOT `/latest/` — so a remote breaking change can't silently
// alter the production atlas), removing the runtime CDN trust + reachability
// dependency for the engine entirely (per §36.1). Sky-survey TILES are still
// fetched from the CDS HiPS servers at runtime, so imagery needs internet; the
// engine, search, and overlays do not. A raw string (r'''): the page's helper JS
// must pass through to CEF verbatim, un-interpolated. The CDS logo + attribution
// Aladin renders bottom-right satisfies §36/§17/§36.11.
const String _aladinBootstrapHtml = r'''
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
  // Shown if the bundled Aladin Lite engine fails to define `A` (a corrupt or
  // truncated bundle). Without this the data: page loads fine but the atlas is a
  // blank black rectangle with no feedback. (The engine is bundled, so this is
  // no longer a network condition; sky-survey tiles still need internet, but a
  // tile failure is Aladin's own blank-with-grid state, not this overlay.)
  function aladinLoadFailed() {
    var el = document.getElementById('atlas-error');
    if (el) el.style.display = 'flex';
  }
</script>
<script>__ALADIN_LITE_JS__</script>
</head>
<body>
<div id="aladin-lite-div"></div>
<div id="atlas-error">Could not start the sky atlas renderer.<br>Try closing and reopening the Sky Atlas.</div>
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

  // §36 base-survey picker. araSetSurvey swaps the atlas's background imagery to
  // a CDS HiPS id (e.g. 'CDS/P/DESI-Legacy-Surveys/DR10/color'); before Aladin
  // finishes init the latest id is stashed and applied from A.init.then. Driven
  // from Dart via AladinView.surveyScript.
  var araPendingSurvey = null;
  function araSetSurvey(id) {
    if (!id) return;
    if (araAladin) {
      araAladin.setBaseImageLayer(id);
    } else {
      araPendingSurvey = id;
    }
  }

  // §36 installed-catalog marker overlay. araAddCatalog(objs) draws an Aladin
  // catalog of {name, ra, dec, mag} point sources (replacing any prior set);
  // araClearCatalog removes them. Before Aladin finishes init the latest set is
  // stashed and applied from A.init.then. Driven from Dart via
  // AladinView.catalogScript / clearCatalogScript.
  var araCatalog = null;
  var araPendingCatalog = null;
  function araEnsureCatalog() {
    if (!araAladin) return null;
    if (!araCatalog) {
      araCatalog = A.catalog({ name: 'Catalog', shape: 'circle', sourceSize: 8, color: '#ffd54f' });
      araAladin.addCatalog(araCatalog);
    }
    return araCatalog;
  }
  function araAddCatalog(objs) {
    // Before Aladin is ready we keep only the LATEST set (replace, not queue) —
    // the overlay is a full snapshot, so an earlier pending set is always stale.
    if (!araAladin) { araPendingCatalog = objs; return; }
    var cat = araEnsureCatalog(); // non-null here: the !araAladin guard above already returned
    cat.removeAll();
    if (!objs || !objs.length) return;
    var sources = [];
    for (var i = 0; i < objs.length; i++) {
      var o = objs[i];
      // Guard against a malformed entry — only place a source with numeric coords.
      if (!o || typeof o.ra !== 'number' || typeof o.dec !== 'number') continue;
      sources.push(A.source(o.ra, o.dec, { name: o.name || '', mag: o.mag }));
    }
    cat.addSources(sources);
  }
  function araClearCatalog() {
    // Also drop any add stashed before init, so a clear that arrives during the
    // init window cancels it rather than letting a stale set draw on init.
    araPendingCatalog = null;
    if (araCatalog) araCatalog.removeAll();
  }

  // §36 local-horizon overlay. araSetHorizon(data) draws the horizon curve (a
  // closed RA/Dec polyline) on its own graphic overlay plus a labelled catalog
  // for the zenith + N/E/S/W cardinals; araClearHorizon removes them. Before
  // Aladin finishes init the latest payload is stashed and applied from
  // A.init.then. Driven from Dart via AladinView.horizonScript / clearHorizonScript.
  var araHorizonOverlay = null; // the curve (graphicOverlay)
  var araHorizonCat = null;     // zenith + cardinal labels (catalog)
  var araPendingHorizon = null;
  function araEnsureHorizonOverlay() {
    if (!araAladin) return null;
    if (!araHorizonOverlay) {
      // Earthy brown, the conventional horizon colour, distinct from the cyan FOV box.
      araHorizonOverlay = A.graphicOverlay({ color: '#a1887f', lineWidth: 2 });
      araAladin.addOverlay(araHorizonOverlay);
    }
    return araHorizonOverlay;
  }
  function araEnsureHorizonCat() {
    if (!araAladin) return null;
    if (!araHorizonCat) {
      araHorizonCat = A.catalog({ name: 'Horizon', shape: 'cross', sourceSize: 12, color: '#a1887f' });
      araAladin.addCatalog(araHorizonCat);
    }
    return araHorizonCat;
  }
  function araSetHorizon(data) {
    // Before Aladin is ready keep only the LATEST payload (replace, not queue) —
    // the overlay is a full snapshot, so an earlier pending one is always stale.
    if (!araAladin) { araPendingHorizon = data; return; }
    var ov = araEnsureHorizonOverlay(); // non-null here: the !araAladin guard above already returned
    var cat = araEnsureHorizonCat();
    ov.removeAll();
    cat.removeAll();
    if (!data || !data.line || !data.line.length) return;
    // The horizon is a closed curve, so a polygon outline traces it back onto itself.
    ov.add(A.polygon(data.line));
    var srcs = [];
    if (data.zenith && data.zenith.length === 2) {
      srcs.push(A.source(data.zenith[0], data.zenith[1], { name: 'Zenith' }));
    }
    if (data.cardinals) {
      for (var i = 0; i < data.cardinals.length; i++) {
        var c = data.cardinals[i];
        if (c && typeof c.ra === 'number' && typeof c.dec === 'number') {
          srcs.push(A.source(c.ra, c.dec, { name: c.label || '' }));
        }
      }
    }
    if (srcs.length) cat.addSources(srcs);
  }
  function araClearHorizon() {
    // Also drop any payload stashed before init, so a clear arriving during the
    // init window cancels it rather than letting a stale horizon draw on init.
    araPendingHorizon = null;
    if (araHorizonOverlay) araHorizonOverlay.removeAll();
    if (araHorizonCat) araHorizonCat.removeAll();
  }

  // The inline engine script above runs to completion before this one starts,
  // so by here `A` is defined on success and undefined if the bundle was bad.
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
      // Apply a survey chosen during Aladin's init window.
      if (araPendingSurvey) {
        var s = araPendingSurvey;
        araPendingSurvey = null;
        araAladin.setBaseImageLayer(s);
      }
      // Draw a catalog overlay pushed during Aladin's init window.
      if (araPendingCatalog) {
        var pc = araPendingCatalog;
        araPendingCatalog = null;
        araAddCatalog(pc);
      }
      // Draw a horizon overlay pushed during Aladin's init window.
      if (araPendingHorizon) {
        var ph = araPendingHorizon;
        araPendingHorizon = null;
        araSetHorizon(ph);
      }
      // Keep the FOV box centred as the user pans/zooms, and draw any box that
      // Dart pushed before Aladin finished initializing.
      araAladin.on('positionChanged', function () { araRedrawFovBox(); });
      araAladin.on('zoomChanged', function () { araRedrawFovBox(); });
      araRedrawFovBox();
    }).catch(function (e) { aladinLoadFailed(); });
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
