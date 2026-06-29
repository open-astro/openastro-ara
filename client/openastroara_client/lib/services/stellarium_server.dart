import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart' show rootBundle;

/// §36 Planetarium — serves the bundled Stellarium Web Engine (the `index.html`
/// bridge page, the WASM engine, and the ~4.5 MB offline sky data) over a
/// loopback HTTP server so the CEF webview can load it.
///
/// Why a server and not a `file://` URL: the engine fetches its WASM module and
/// every sky-data file over XHR, and CEF blocks cross-origin `file://` reads — so
/// the page must be served over http. Everything is read straight from the Flutter
/// asset bundle (declared under `assets/stellarium/`), so it stays fully offline;
/// nothing is written to disk.
///
/// One server is started per app run (lazily, on first [start]) and bound to an
/// ephemeral loopback port. Call [dispose] to stop it.
class StellariumServer {
  StellariumServer._(this._server, this.baseUrl);

  final HttpServer _server;

  /// `http://127.0.0.1:<port>` — load `'$baseUrl/index.html'` in the webview.
  final String baseUrl;

  static const String _assetRoot = 'assets/stellarium';

  // ── Flutter → page command channel ──────────────────────────────────────
  // The multi-process CEF webview has no working Dart→page JS bridge
  // (executeJavaScript / evaluateJavascript don't reach the renderer), so any
  // command that originates in Flutter (e.g. a typed search — CEF can't receive
  // keyboard text either) is dropped here as a one-shot JSON command and the
  // page polls [GET /aracmd] to pick it up. Loopback + same-origin, so the page
  // can always reach it.
  final List<String> _commands = [];

  /// Hard cap on the unread command backlog. The page drains one per ~350 ms
  /// poll; commands are normally enqueued only on user interaction, so this stays
  /// tiny — but bound it so a future loop calling [pushCommand] can't grow it
  /// without limit. When full, the oldest (most stale) commands are dropped.
  static const int _maxQueuedCommands = 64;

  /// Queue a command (a JSON string) for the page to pick up on its next poll.
  void pushCommand(String json) {
    _commands.add(json);
    if (_commands.length > _maxQueuedCommands) {
      _commands.removeRange(0, _commands.length - _maxQueuedCommands);
    }
  }

  // ── page → Flutter reverse channel ──────────────────────────────────────
  // Some actions originate in the page but must be handled by Flutter (e.g.
  // "add this framed target to the sequence" — the daemon's NINA sequence DOM is
  // built by Dart code, not the page). The page POSTs a JSON event to
  // [POST /araevent]; we surface it on [events] for the widget to act on.
  final _events = StreamController<Map<String, Object?>>.broadcast();

  /// Events the planetarium page posts back to Flutter (e.g. `addToSequence`).
  Stream<Map<String, Object?>> get events => _events.stream;

  static Future<StellariumServer>? _instance;

  /// Start (or return the already-running) loopback asset server.
  ///
  /// A *failed* start (e.g. `HttpServer.bind` losing the port race under
  /// resource pressure) clears the cached future so the next mount can retry —
  /// otherwise every later `start()` would replay the same rejected future and
  /// the planetarium could never recover without an app restart.
  static Future<StellariumServer> start() =>
      _instance ??= _start().onError((Object e, StackTrace s) {
        _instance = null;
        Error.throwWithStackTrace(e, s);
      });

  static Future<StellariumServer> _start() async {
    // Port 0 → the OS picks a free ephemeral port; loopback-only so nothing off
    // this machine can reach the engine/data.
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    final instance = StellariumServer._(server, 'http://127.0.0.1:${server.port}');
    unawaited(instance._serve());
    return instance;
  }

  Future<void> _serve() async {
    await for (final request in _server) {
      unawaited(_handle(request));
    }
  }

  /// True when the request's `Host` header names our own loopback origin.
  /// Defeats DNS-rebinding: a rebinding page can resolve a hostname to 127.0.0.1
  /// and POST here, but its browser still sends that attacker hostname in `Host`.
  /// The server binds `InternetAddress.loopbackIPv4` and the page URL is always
  /// `http://127.0.0.1:<port>/…`, so every legitimate post carries exactly
  /// `Host: 127.0.0.1:<port>`. Anything else (a rebinding hostname, a missing or
  /// garbage Host with no port) is rejected.
  bool _isLoopbackHost(HttpRequest request) {
    return request.headers.host == '127.0.0.1' &&
        request.headers.port == _server.port;
  }

  Future<void> _handle(HttpRequest request) async {
    final response = request.response;
    try {
      // Map the URL path onto an asset key. A bare "/" serves the bridge page.
      var path = request.uri.path;
      if (path == '/' || path.isEmpty) path = '/index.html';
      // Flutter → page command channel: the page long-polls this; we hand back the
      // oldest queued command (a JSON object) and drop it, or `{}` when idle.
      if (path == '/aracmd') {
        // Same DNS-rebinding guard as /araevent: a rebinding page could otherwise
        // poll this queue, draining goto commands the real page never sees and
        // reading any queued target. Lower stakes than the POST path (read-only,
        // no mount slew) but it's the same one-liner.
        if (!_isLoopbackHost(request)) {
          response.statusCode = HttpStatus.forbidden;
          await response.close();
          return;
        }
        if (request.method != 'GET') {
          response.statusCode = HttpStatus.methodNotAllowed;
          response.headers.set(HttpHeaders.allowHeader, 'GET');
          await response.close();
          return;
        }
        final cmd = _commands.isNotEmpty ? _commands.removeAt(0) : '{}';
        response.headers.contentType =
            ContentType('application', 'json', charset: 'utf-8');
        response.headers.set(HttpHeaders.cacheControlHeader, 'no-store');
        response.write(cmd);
        await response.close();
        return;
      }
      // Page → Flutter reverse channel: the page POSTs a JSON event here; we
      // decode it and surface it on [events]. Always answer 200 so the page's
      // fetch resolves; a malformed body is just dropped.
      if (path == '/araevent') {
        // DNS-rebinding guard: this channel can carry mount-slewing events
        // (addToSequence), so only accept POSTs whose Host header is our own
        // loopback origin. A rebinding attack reaches us with the attacker's
        // hostname in Host; our own page always sends 127.0.0.1:<port>.
        if (!_isLoopbackHost(request)) {
          response.statusCode = HttpStatus.forbidden;
          await response.close();
          return;
        }
        if (request.method != 'POST') {
          response.statusCode = HttpStatus.methodNotAllowed;
          response.headers.set(HttpHeaders.allowHeader, 'POST');
          await response.close();
          return;
        }
        try {
          // The page posts tiny JSON events; cap the body so a buggy or compromised
          // page can't make us buffer an arbitrarily large payload (loopback-only,
          // but bound the read regardless). A declared Content-Length lets us reject
          // up front; chunked bodies (contentLength == -1) are bounded by the
          // per-chunk accumulation check below.
          const maxEventBytes = 64 * 1024;
          if (request.contentLength != -1 && request.contentLength > maxEventBytes) {
            throw const FormatException('event body too large');
          }
          // BytesBuilder(copy: false) keeps each chunk by reference instead of the
          // O(chunks²) re-copy a growing List<int>.addAll would do. Check the size
          // BEFORE adding so peak buffering stays at the cap, not cap + one chunk.
          final builder = BytesBuilder(copy: false);
          await for (final chunk in request) {
            if (builder.length + chunk.length > maxEventBytes) {
              throw const FormatException('event body too large');
            }
            builder.add(chunk);
          }
          final decoded = jsonDecode(utf8.decode(builder.takeBytes()));
          // Guard against a dispose() that closed the controller while this
          // request was mid-flight — add to a closed StreamController throws.
          if (decoded is Map && !_events.isClosed) {
            _events.add(Map<String, Object?>.from(decoded));
          }
        } catch (_) {/* ignore malformed or oversized event bodies */}
        response.headers.set(HttpHeaders.cacheControlHeader, 'no-store');
        response.statusCode = HttpStatus.ok;
        await response.close();
        return;
      }
      // Reject any traversal attempt before touching the bundle.
      if (path.contains('..')) {
        response.statusCode = HttpStatus.forbidden;
        await response.close();
        return;
      }
      final key = '$_assetRoot$path';
      final range = request.headers.value(HttpHeaders.rangeHeader);
      final ByteData data;
      try {
        data = await rootBundle.load(key);
      } catch (_) {
        response.statusCode = HttpStatus.notFound;
        await response.close();
        return;
      }
      final bytes =
          data.buffer.asUint8List(data.offsetInBytes, data.lengthInBytes);
      response.headers.contentType = _contentTypeFor(path);
      // The engine fetches gzipped data (e.g. the satellite TLEs) and inflates it
      // itself, so never let the HTTP layer claim/translate the encoding.
      response.headers.set(HttpHeaders.acceptRangesHeader, 'bytes');
      // The star/DSO tile loader uses HTTP Range requests; honour them with a 206
      // partial response (a plain 200-with-full-body confuses the loader → no stars).
      final r = _parseRange(range, bytes.length);
      if (r != null) {
        response.statusCode = HttpStatus.partialContent;
        response.headers.set(HttpHeaders.contentRangeHeader,
            'bytes ${r.$1}-${r.$2}/${bytes.length}');
        response.headers.set(HttpHeaders.contentLengthHeader, r.$2 - r.$1 + 1);
        response.add(bytes.sublist(r.$1, r.$2 + 1));
      } else {
        response.headers.set(HttpHeaders.contentLengthHeader, bytes.length);
        response.add(bytes);
      }
      await response.close();
    } catch (e, st) {
      debugPrint('StellariumServer: failed to serve ${request.uri}: $e\n$st');
      try {
        response.statusCode = HttpStatus.internalServerError;
        await response.close();
      } catch (_) {/* response already closed/detached */}
    }
  }

  /// Parse a single `bytes=start-end` Range header into inclusive byte offsets,
  /// clamped to the resource length. Returns null for absent/unsatisfiable/multi
  /// ranges (the caller then serves the whole body).
  @visibleForTesting
  static (int, int)? parseRange(String? header, int length) => _parseRange(header, length);

  static (int, int)? _parseRange(String? header, int length) {
    if (header == null || length <= 0) return null;
    const prefix = 'bytes=';
    if (!header.startsWith(prefix)) return null;
    final spec = header.substring(prefix.length);
    if (spec.contains(',')) return null; // multi-range not supported
    final dash = spec.indexOf('-');
    if (dash < 0) return null;
    final startStr = spec.substring(0, dash);
    final endStr = spec.substring(dash + 1);
    int start, end;
    if (startStr.isEmpty) {
      // suffix range: bytes=-N → the last N bytes
      final n = int.tryParse(endStr);
      if (n == null || n <= 0) return null;
      start = (length - n).clamp(0, length - 1);
      end = length - 1;
    } else {
      final s = int.tryParse(startStr);
      // `s < 0` is defensive: today startStr is the slice *before the first dash*
      // so it can't hold a '-' (a leading dash makes it empty → the suffix branch),
      // but guard anyway so a future change to the dash-splitting can't let a
      // negative start slip past `>= length` and throw in bytes.sublist(s, …).
      if (s == null || s < 0 || s >= length) return null;
      start = s;
      end = endStr.isEmpty ? length - 1 : (int.tryParse(endStr) ?? length - 1);
    }
    // Unsatisfiable range (last-byte-pos < first-byte-pos, e.g. "bytes=100-5"):
    // return null so the caller serves the full body (200) rather than the clamp
    // silently collapsing it to a wrong 1-byte 206. A 200-with-full-body is an
    // RFC-allowed response to a Range the server chooses not to honour.
    if (end < start) return null;
    end = end.clamp(start, length - 1);
    return (start, end);
  }

  /// Content type by extension. The WASM type matters (streaming instantiation);
  /// the binary sky-data blobs are fetched as array buffers, so octet-stream is fine.
  @visibleForTesting
  static ContentType contentTypeFor(String path) => _contentTypeFor(path);

  static ContentType _contentTypeFor(String path) {
    final dot = path.lastIndexOf('.');
    final ext = dot < 0 ? '' : path.substring(dot + 1).toLowerCase();
    switch (ext) {
      case 'html':
        return ContentType.html;
      case 'js':
        return ContentType('text', 'javascript', charset: 'utf-8');
      case 'wasm':
        return ContentType('application', 'wasm');
      case 'json':
        return ContentType('application', 'json', charset: 'utf-8');
      case 'ttf':
        return ContentType('font', 'ttf');
      case 'svg':
        return ContentType('image', 'svg+xml');
      case 'png':
        return ContentType('image', 'png');
      case 'webp':
        return ContentType('image', 'webp');
      case 'gz':
        return ContentType('application', 'gzip');
      default:
        return ContentType('application', 'octet-stream');
    }
  }

  Future<void> dispose() async {
    await _events.close();
    await _server.close(force: true);
    if (identical(await _instance, this)) _instance = null;
  }
}
