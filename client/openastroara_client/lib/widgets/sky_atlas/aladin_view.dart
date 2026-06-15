import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:webview_cef/webview_cef.dart';

import '../../theme/ara_colors.dart';

/// §36 Sky Atlas — the embedded Aladin Lite (CDS) sky atlas.
///
/// Per PORT_DECISIONS.md the cross-desktop embed uses `webview_cef`: a
/// Chromium/CEF browser rendered to a Flutter texture, so the atlas composites
/// directly into the widget tree (under the search bar + mode toggle) and
/// renders identically in-tab on macOS, Windows, and Linux — one code path.
///
/// This widget owns the CEF browser lifecycle and surfaces loading / unavailable
/// states. The §36 universal-search "goto" and the Tonight's-Sky projection
/// wiring layer on top in later slices.
class AladinView extends StatefulWidget {
  const AladinView({super.key});

  @override
  State<AladinView> createState() => _AladinViewState();
}

// CEF's manager is a process-wide singleton; initialize it at most once so a
// tab rebuild (switch away + back) can't re-run native init. A FAILED init is
// not cached: _init() clears this on a manager-init failure so a later mount
// (e.g. after the user installs a missing runtime) can retry rather than being
// stuck "unavailable" for the whole process lifetime.
Future<void>? _managerInit;
Future<void> _ensureManagerInitialized() =>
    _managerInit ??= WebviewManager().initialize();

class _AladinViewState extends State<AladinView> {
  WebViewController? _controller;
  bool _unavailable = false;

  @override
  void initState() {
    super.initState();
    unawaited(_init());
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
  // A normal (non-async) external script blocks parsing until it loads or
  // errors, so by here `A` is defined on success and undefined on failure.
  if (typeof A !== 'undefined') {
    A.init.then(function () {
      A.aladin('#aladin-lite-div', {
        survey: 'P/DSS2/color',
        fov: 60,
        target: 'M31',
        showCooGrid: true,
        showSimbadPointerControl: true,
      });
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
                  'A Chromium runtime is required for the in-app sky atlas.',
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
