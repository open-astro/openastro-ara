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
// tab rebuild (switch away + back) can't re-run native init.
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
    try {
      await _ensureManagerInitialized();
      final controller = WebviewManager().createWebView(loading: const _Loading());
      await controller.initialize(_aladinDataUrl);
      if (!mounted) {
        // The tab was disposed mid-init — release the browser we just created.
        await controller.dispose();
        return;
      }
      setState(() => _controller = controller);
    } catch (_) {
      // Native CEF isn't available — an unsupported host, a missing Chromium
      // runtime, or the headless `flutter test` environment (no plugin
      // registrant). Degrade to an informative panel instead of crashing the tab.
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
// CDS CDN; bundling the ~5 MB JS for offline use (per §36.1) is a later slice.
// The CDS logo + attribution Aladin renders bottom-right satisfies §36/§17.
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
</style>
<script src="https://aladin.cds.unistra.fr/AladinLite/api/v3/latest/aladin.js" charset="utf-8"></script>
</head>
<body>
<div id="aladin-lite-div"></div>
<script>
  A.init.then(function () {
    A.aladin('#aladin-lite-div', {
      survey: 'P/DSS2/color',
      fov: 60,
      target: 'M31',
      showCooGrid: true,
      showSimbadPointerControl: true,
    });
  });
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
