// Dev-only regression harness for the CEF 149 / WebGL offscreen pipeline.
// NOT shipped — it's a separate entry point, only built when explicitly targeted.
//
// Purpose: re-test the webview_cef OSR + software-WebGL path in isolation, with
// NONE of ARA's app shell, connect flow, daemon, or tab switching in the way — a
// single fullscreen webview loads a page directly. Run:
//
//   flutter run -t lib/main_atlas_test.dart -d macos
//
// It loads a minimal Aladin Lite v3 bootstrap (below); the DSS2 star field +
// coordinate grid render when the pipeline is healthy, blank/black when it isn't.
// To sanity-check raw WebGL instead, point _start() at https://get.webgl.org/
// (a spinning cube renders iff WebGL works).
//
// Uses the exact same webview_cef plugin + OSR config + clean-shutdown wiring as
// the §36 Sky Atlas, so a positive result here mirrors the in-app atlas.
import 'dart:io';
import 'dart:ui' show AppExitResponse;
import 'package:flutter/material.dart';
import 'package:webview_cef/webview_cef.dart';

// Minimal Aladin Lite v3 bootstrap (WebGL2 + WASM), loaded from CDN exactly the
// way the §36 Sky Atlas does. If the star field renders here, ARA's atlas will
// render once wired in.
const String _aladinHtml = '''
<!DOCTYPE html>
<html><head><meta charset="utf-8">
<style>html,body,#aladin-lite-div{margin:0;width:100%;height:100%;background:#000}</style>
<script src="https://aladin.cds.unistra.fr/AladinLite/api/v3/latest/aladin.js" charset="utf-8"></script>
</head><body>
<div id="aladin-lite-div"></div>
<script>
  A.init.then(() => {
    A.aladin('#aladin-lite-div', {survey:'P/DSS2/color', fov:60, target:'M31', showCooGrid:true});
    document.title = 'aladin-ready';
  }).catch(e => { document.title = 'aladin-error: ' + e; });
</script>
</body></html>''';

void main() => runApp(const _AtlasTestApp());

class _AtlasTestApp extends StatefulWidget {
  const _AtlasTestApp();
  @override
  State<_AtlasTestApp> createState() => _AtlasTestAppState();
}

class _AtlasTestAppState extends State<_AtlasTestApp> {
  WebViewController? _controller;
  String _status = 'starting…';
  AppLifecycleListener? _lifecycle;

  @override
  void initState() {
    super.initState();
    // Drive CefShutdown (CloseAllBrowsers + CefShutdown) BEFORE the process exits.
    // CEF crashes on teardown if exit() runs while its GPU/renderer threads are
    // still live; onExitRequested intercepts the macOS terminate request, lets us
    // quit CEF cleanly, then allows exit. Bounded by a timeout (as in production
    // main.dart) so a hung teardown can't wedge the harness on exit.
    _lifecycle = AppLifecycleListener(
      onExitRequested: () async {
        try {
          await WebviewManager().quit().timeout(const Duration(seconds: 3));
        } catch (_) {}
        return AppExitResponse.exit;
      },
    );
    _start();
  }

  @override
  void dispose() {
    _lifecycle?.dispose();
    super.dispose();
  }

  Future<void> _start() async {
    try {
      final f = File('${Directory.systemTemp.path}/atlas_test_aladin.html');
      await f.writeAsString(_aladinHtml);
      final url = Uri.file(f.path).toString();
      await WebviewManager().initialize();
      final c = WebviewManager().createWebView(loading: const Text('loading…'));
      await c.initialize(url);
      if (!mounted) return;
      setState(() {
        _controller = c;
        _status = 'loaded Aladin';
      });
    } catch (e, st) {
      // ignore: avoid_print
      print('atlas-test webview init failed: $e\n$st');
      if (!mounted) return;
      setState(() => _status = 'ERROR: $e');
    }
  }

  @override
  Widget build(BuildContext context) {
    final c = _controller;
    return MaterialApp(
      debugShowCheckedModeBanner: false,
      home: Scaffold(
        appBar: AppBar(title: Text('webview test — $_status'), toolbarHeight: 36),
        body: c == null
            ? Center(child: Text(_status))
            : SizedBox.expand(child: c.webviewWidget),
      ),
    );
  }
}
