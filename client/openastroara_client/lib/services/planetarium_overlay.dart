import 'package:flutter/services.dart';

/// Linux-only bridge to the native GTK planetarium overlay
/// (`linux/runner/planetarium_overlay.cc`).
///
/// On Linux the §36 planetarium can't be embedded through Flutter's
/// platform-view / GL path without blanking the whole app
/// (flutter/flutter#88168), so a native `WebKitWebView` is composited over
/// `FlView` in its own GTK surface and positioned from Dart over this channel.
/// macOS (WKWebView) and Windows (WebView2) embed via `webview_all` instead and
/// never touch this class.
class PlanetariumOverlay {
  const PlanetariumOverlay();

  // Must match kChannelName in linux/runner/planetarium_overlay.cc.
  static const MethodChannel _channel =
      MethodChannel('org.openastro.openastroara/planetarium');

  /// Create the native webview (first call only) and load [url] — the same
  /// loopback page the embedded webviews load on other platforms.
  Future<void> setUrl(String url) =>
      _channel.invokeMethod<void>('setUrl', {'url': url});

  /// Position the overlay over the planetarium area. [rect] is the widget's
  /// global rect in logical pixels (left/top/width/height); GTK widget
  /// coordinates share Flutter's logical scale on a given display, so no DPI
  /// conversion is applied.
  Future<void> setBounds(Rect rect) => _channel.invokeMethod<void>('setBounds', {
        'x': rect.left,
        'y': rect.top,
        'width': rect.width,
        'height': rect.height,
      });

  /// Show/hide the overlay (hidden when the Planning tab isn't active or a route
  /// covers it — the webview keeps living so atlas state survives).
  Future<void> setVisible(bool visible) =>
      _channel.invokeMethod<void>('setVisible', {'visible': visible});
}
