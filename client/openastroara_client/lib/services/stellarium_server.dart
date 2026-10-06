import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math';
import 'dart:typed_data';

import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart' show rootBundle;
import 'package:path_provider/path_provider.dart';

/// §36 Planetarium — serves the bundled Stellarium Web Engine (the `index.html`
/// bridge page, the WASM engine, and the ~4.5 MB offline sky data) over a
/// loopback HTTP server so the CEF webview can load it.
///
/// Why a server and not a `file://` URL: the engine fetches its WASM module and
/// every sky-data file over XHR, and CEF blocks cross-origin `file://` reads — so
/// the page must be served over http. Everything is read straight from the Flutter
/// asset bundle (declared under `assets/stellarium/`), so vector sky data stays
/// offline. DSS2 photographic tiles use a per-user disk cache; each fetched
/// resource is capped at 64 MiB.
///
/// One server is started per app run (lazily, on first [start]) and bound to an
/// ephemeral loopback port. Call [dispose] to stop it.
class StellariumServer {
  StellariumServer._(
    this._server,
    this.baseUrl,
    this.token,
    this._dssCacheDir,
    this._dssCacheIsTemp,
  );

  final HttpServer _server;

  /// `http://127.0.0.1:<port>` — load `'$baseUrl/index.html'` in the webview.
  final String baseUrl;

  /// Per-run secret baked into the served page URL (`?token=…`) and required as
  /// the `X-Ara-Token` header on the [POST /araevent] / [GET /aracmd] control
  /// channels. See [_isAuthorized]: only the page we served — which reads the
  /// token from its own `location.search` — knows it, so a DNS-rebinding page
  /// (which can hit our loopback origin but can't read our page's URL or bypass
  /// same-origin to see it) can't forge these mount-slewing requests.
  final String token;

  /// Local lazy cache for DSS2 HiPS tiles. The page talks to `/dss-<token>/` on this
  /// loopback server instead of CDS directly, so tiles downloaded while online
  /// remain available when the computer later joins the SBC-only hotspot.
  final Directory _dssCacheDir;

  /// True when [_dssCacheDir] is the private mkdtemp fallback rather than the
  /// app-support cache; [dispose] removes it so headless hosts and test runs
  /// do not leave one directory per server behind in the system temp.
  final bool _dssCacheIsTemp;

  /// Where DSS2 tiles are cached (exposed so a test can seed a hit).
  @visibleForTesting
  Directory get dssCacheDir => _dssCacheDir;
  // connectionTimeout, not `.getUrl().timeout()`: the latter abandons only the
  // wait, so a connect+TLS that finishes a moment later leaves an unclosed
  // HttpClientRequest in the client's active set for the app's life.
  final HttpClient _dssClient = HttpClient()
    ..connectionTimeout = const Duration(seconds: 5)
    ..maxConnectionsPerHost = 6;
  final Map<String, Future<Uint8List?>> _dssFetches = {};
  DateTime? _dssRetryAfter;
  DateTime? _dssLastFailure;
  DateTime? _dssLastSuccess;

  /// True once an upstream fetch has failed more recently than one succeeded:
  /// the page's Frame panel asks `/dss-<token>/status` so it can say why the photo
  /// backdrop is blank at a dark site even when the manifest is cached.
  bool get _dssOffline =>
      _dssLastFailure != null &&
      (_dssLastSuccess == null || _dssLastFailure!.isAfter(_dssLastSuccess!));

  /// Forget the upstream outcome history (backoff, offline flag). Test-only:
  /// the fetch tests share one server, and a refusal in one test must not
  /// decide what the next one observes.
  @visibleForTesting
  void resetDssState() {
    _dssRetryAfter = null;
    _dssLastFailure = null;
    _dssLastSuccess = null;
  }

  /// The DSS route carries the per-run [token] in its PATH (`/dss-<token>/`)
  /// because the engine's HiPS loader cannot set a header: a DNS-rebinding
  /// page that can reach the port but not read our URL can neither drive the
  /// cache nor read what is in it (#1143). The page builds the same prefix
  /// from its own `?token=`.
  late final String _dssPathPrefix = '/dss-$token/';
  /// The bare `/dss/` of older pages and a wrong token are refused here,
  /// never handed to the asset handler.
  static bool _isDssPath(String path) =>
      path == '/dss' || path.startsWith('/dss/') || path.startsWith('/dss-');

  /// Same comparison the header token gets: the prefix is compared in
  /// constant time, so a wrong token in the path leaks nothing by timing.
  bool _hasDssPrefix(String path) =>
      path.length >= _dssPathPrefix.length &&
      _constantTimeEquals(
          path.substring(0, _dssPathPrefix.length), _dssPathPrefix);

  /// Route prefix the page must use for tiles, e.g. `/dss-<token>/`.
  @visibleForTesting
  String get dssPathPrefix => _dssPathPrefix;

  /// Upstream fetches in flight at once. A miss beyond this waits for a slot
  /// (bounded by [dssHeadersTimeout]) rather than opening another upstream
  /// connection: the page's sockets are still parked, but behind one
  /// connect/first-byte deadline, not one per miss (#1143). Cached tiles never
  /// queue. Not a 404 shed: the engine's HiPS loader may remember a 404 for
  /// the session, which would leave a tile blank while online.
  @visibleForTesting
  static int maxDssConcurrentFetches = 6;

  /// Whole-cache size cap. A write that takes the cache past it evicts the
  /// oldest-fetched files until it is back under 90 % of the cap (see
  /// [pruneDssCache]). ~16 MiB of writes between checks keeps the scan rare.
  @visibleForTesting
  static int maxDssCacheBytes = 512 * 1000 * 1000;
  @visibleForTesting
  static int dssPruneCheckEvery = 16 * 1024 * 1024;
  int _dssBytesSincePrune = 0;
  bool _dssPruning = false;

  /// First-byte deadline for an upstream fetch (connect is bounded separately).
  @visibleForTesting
  static Duration dssHeadersTimeout = const Duration(seconds: 10);
  /// Upstream HiPS root (trailing slash: tile paths resolve beneath it).
  /// Static + overridable so a test can point the cache at a local stub.
  @visibleForTesting
  static Uri dssOrigin = Uri.parse('https://alasky.u-strasbg.fr/DSS/DSSColor/');

  /// Per-resource size cap (a HiPS tile is ~10-60 KiB; Allsky under 2 MiB).
  @visibleForTesting
  static int maxDssResourceBytes = 64 * 1024 * 1024;

  /// Deadline for the whole upstream body once headers are in: a stalled TCP
  /// body (captive-portal hotspot) must not pin the page's fetch, the
  /// coalescing map entry and the socket until the OS gives up.
  @visibleForTesting
  static Duration dssBodyTimeout = const Duration(seconds: 30);
  static final RegExp _dssSegment = RegExp(r'^[A-Za-z0-9._-]+$');

  static const String _tokenHeader = 'x-ara-token';

  /// A 256-bit URL-safe random token, minted once per server run.
  static String _mintToken() {
    final rng = Random.secure();
    final bytes = List<int>.generate(32, (_) => rng.nextInt(256));
    return base64Url.encode(bytes).replaceAll('=', '');
  }

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

  // ── catalogs channel ────────────────────────────────────────────────────
  // The page's Catalogs overlays used to GET the daemon's /api/v1/catalogs;
  // the catalogs are bundled in the client now, so the page asks THIS server
  // (`/aracat`, `/aracat/{id}?limit=`) and the view answers from the bundled
  // set — no daemon, no network. Same wire shapes as the daemon endpoints.
  static Future<List<Map<String, Object?>>> Function()? catalogListResolver;
  static Future<List<Map<String, Object?>>?> Function(String id, int limit)?
      catalogObjectsResolver;

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
    final (dssCacheDir, dssCacheIsTemp) = await _createDssCacheDir();
    final instance = StellariumServer._(
      server,
      'http://127.0.0.1:${server.port}',
      _mintToken(),
      dssCacheDir,
      dssCacheIsTemp,
    );
    unawaited(instance._serve());
    // Housekeeping off the start path: orphaned `.part-*` files from a kill
    // mid-write, and a cache that grew past the cap in an earlier run.
    unawaited(instance._houseKeep());
    return instance;
  }

  Future<void> _houseKeep() async {
    try {
      await sweepPartFiles(_dssCacheDir);
      await _pruneIfNeeded(force: true);
    } on FileSystemException {
      // Disposed (temp cache removed) while the scan ran: nothing to keep.
    } catch (e) {
      debugPrint('StellariumServer: DSS cache housekeeping failed: $e');
    }
  }

  /// Delete the `*.part-<micros>` temp files a hard kill left behind (the
  /// persist step writes to one and renames it over the tile). Returns the
  /// number removed. Static so a test can run it on any directory.
  @visibleForTesting
  static Future<int> sweepPartFiles(Directory dir) async {
    if (!await dir.exists()) return 0;
    var removed = 0;
    await for (final e in dir.list(recursive: true, followLinks: false)) {
      if (e is File && _partName.hasMatch(e.uri.pathSegments.last)) {
        try {
          await e.delete();
          removed++;
        } catch (_) {/* a write in progress, or gone: leave it */}
      }
    }
    return removed;
  }

  static final RegExp _partName = RegExp(r'\.part-\d+$');

  /// `properties`, any `Allsky.*` preview, and tiles of order 3 or lower:
  /// what every session reads before any deep tile, and what an offline
  /// launch cannot do without. Pinned by [pruneDssCache]. Keys may carry a
  /// `@<buster>` suffix.
  @visibleForTesting
  static bool isDssRootResource(String rel) {
    if (rel == 'properties' || rel.startsWith('properties@')) return true;
    final parts = rel.split('/');
    final order = RegExp(r'^Norder(\d+)$').firstMatch(parts.first);
    if (order == null) return false;
    if (parts.length == 2 && parts[1].startsWith('Allsky.')) return true;
    return int.parse(order[1]!) <= 3;
  }

  /// Evict the oldest-fetched files until the cache is under 90 % of
  /// [maxBytes]; `.part-*` files are never counted or removed here. Returns
  /// the bytes freed. Modification time stands in for "last fetched" (a hit
  /// does not touch the file: one stat per tile served would cost more than
  /// the odd re-download). The survey root is pinned ([isDssRootResource]):
  /// hits never touch mtime, so the first files a session fetches —
  /// `properties`, the Allsky previews and the low-order tiles every view
  /// goes through — would otherwise be the first evicted, and an offline
  /// launch would 404 on `properties` with hundreds of MB of tiles still on
  /// disk (review on #1296). Static so a test can run it on any directory.
  @visibleForTesting
  static Future<int> pruneDssCache(Directory dir, {required int maxBytes}) async {
    if (!await dir.exists()) return 0;
    final files = <({File file, int size, DateTime modified})>[];
    var total = 0;
    final root = dir.path.length + 1;
    await for (final e in dir.list(recursive: true, followLinks: false)) {
      if (e is! File || _partName.hasMatch(e.uri.pathSegments.last)) continue;
      try {
        final st = await e.stat();
        total += st.size;
        final rel = e.path.substring(root).replaceAll(r'\', '/');
        if (isDssRootResource(rel)) continue; // counted, never evicted
        files.add((file: e, size: st.size, modified: st.modified));
      } catch (_) {/* vanished mid-scan */}
    }
    if (total <= maxBytes) return 0;
    files.sort((a, b) => a.modified.compareTo(b.modified));
    final target = (maxBytes * 0.9).floor();
    var freed = 0;
    for (final f in files) {
      if (total - freed <= target) break;
      try {
        await f.file.delete();
        freed += f.size;
      } catch (_) {/* in use or gone */}
    }
    return freed;
  }

  Future<void> _pruneIfNeeded({bool force = false}) async {
    if (_dssPruning) return;
    if (!force && _dssBytesSincePrune < dssPruneCheckEvery) return;
    _dssPruning = true;
    _dssBytesSincePrune = 0;
    try {
      final freed = await pruneDssCache(_dssCacheDir, maxBytes: maxDssCacheBytes);
      if (freed > 0) {
        debugPrint('StellariumServer: DSS cache over cap, evicted $freed bytes');
      }
    } on FileSystemException {
      // Disposed (temp cache removed) mid-scan: nothing left to prune.
    } catch (e) {
      debugPrint('StellariumServer: DSS cache prune failed: $e');
    } finally {
      _dssPruning = false;
    }
  }

  /// Wait until fewer than [maxDssConcurrentFetches] fetches are in flight,
  /// or [dssHeadersTimeout] has passed (then the caller fetches anyway: the
  /// slot count is a brake, never a refusal).
  Future<void> _awaitFetchSlot() async {
    final deadline = DateTime.now().add(dssHeadersTimeout);
    while (_dssFetches.length >= maxDssConcurrentFetches &&
        DateTime.now().isBefore(deadline)) {
      // Any in-flight fetch finishing frees a slot; a failed one is swallowed
      // here (its own caller reports it) and only wakes this waiter.
      await Future.any(_dssFetches.values.map((f) => f.catchError((_) => null)))
          .timeout(const Duration(milliseconds: 500), onTimeout: () => null);
    }
  }

  /// Size and file count of the tile cache, for the Storage panel.
  static Future<({int files, int bytes})> measureDssCache() async {
    final dir = await _dssCacheDirectory();
    var files = 0, bytes = 0;
    if (await dir.exists()) {
      await for (final e in dir.list(recursive: true, followLinks: false)) {
        if (e is! File) continue;
        try {
          bytes += (await e.stat()).size;
          files++;
        } catch (_) {/* vanished mid-scan */}
      }
    }
    return (files: files, bytes: bytes);
  }

  /// "Clean sky photo cache": delete every cached tile. A running server
  /// re-fetches on the next miss; an in-flight persist whose rename now
  /// fails is logged and the tile served from memory. Returns what was
  /// removed.
  static Future<({int files, int bytes})> clearDssCache() async {
    final dir = await _dssCacheDirectory();
    final before = await measureDssCache();
    final running = _instance;
    if (running != null) {
      try {
        (await running)._dssBytesSincePrune = 0;
      } catch (_) {/* a failed start */}
    }
    if (await dir.exists()) {
      await for (final e in dir.list(followLinks: false)) {
        try {
          await e.delete(recursive: true);
        } catch (_) {/* a write in progress: the next clean gets it */}
      }
    }
    return before;
  }

  /// The running server's cache directory, or the app-support one it would
  /// use (so the Storage panel works before the planetarium was ever opened).
  static Future<Directory> _dssCacheDirectory() async {
    final running = _instance;
    if (running != null) {
      try {
        return (await running)._dssCacheDir;
      } catch (_) {/* a failed start: fall through */}
    }
    final support = await getApplicationSupportDirectory();
    return Directory('${support.path}/$dssCacheDirName');
  }

  /// Folder name under the application-support directory (docs/RUNNING.md).
  static const String dssCacheDirName = 'stellarium-dss2';

  /// The cache directory, and whether it is the temp fallback (see
  /// [_dssCacheIsTemp]).
  static Future<(Directory, bool)> _createDssCacheDir() async {
    try {
      final support = await getApplicationSupportDirectory();
      final dir = Directory('${support.path}/$dssCacheDirName');
      await dir.create(recursive: true);
      return (dir, false);
    } catch (_) {
      // Keep the planetarium usable in a test/headless host where the path
      // provider plugin is unavailable. A fresh private (mkdtemp, 0700) dir per
      // run — never a fixed shared path another user could pre-create or
      // symlink on a multi-user host. Nothing here touches the SBC.
      return (await Directory.systemTemp.createTemp('openastroara-dss2-'), true);
    }
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

  /// Gate for the two control channels (`/araevent`, `/aracmd`). On top of the
  /// [_isLoopbackHost] DNS-rebind guard, require the per-run [token] in the
  /// `X-Ara-Token` header. A direct-loopback POST from another local process or
  /// a rebinding page still carries our loopback `Host`, so the Host check alone
  /// lets it through; only the page WE served knows the token (it reads it from
  /// its own `location.search`), so this closes the forged-mount-slew hole.
  /// Compared length-independently to avoid a trivial timing side-channel.
  bool _isAuthorized(HttpRequest request) {
    if (!_isLoopbackHost(request)) return false;
    final provided = request.headers.value(_tokenHeader);
    return provided != null && _constantTimeEquals(provided, token);
  }

  static bool _constantTimeEquals(String a, String b) {
    if (a.length != b.length) return false;
    var diff = 0;
    for (var i = 0; i < a.length; i++) {
      diff |= a.codeUnitAt(i) ^ b.codeUnitAt(i);
    }
    return diff == 0;
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
        // Same guard as /araevent (Host + per-run token): a rebinding or
        // other local page could otherwise poll this queue, draining goto
        // commands the real page never sees and reading any queued target.
        if (!_isAuthorized(request)) {
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
        response.headers.contentType = ContentType(
          'application',
          'json',
          charset: 'utf-8',
        );
        response.headers.set(HttpHeaders.cacheControlHeader, 'no-store');
        response.write(cmd);
        await response.close();
        return;
      }
      // Page → Flutter reverse channel: the page POSTs a JSON event here; we
      // decode it and surface it on [events]. Always answer 200 so the page's
      // fetch resolves; a malformed body is just dropped.
      if (path == '/araevent') {
        // This channel can carry mount-slewing events (addToSequence), so
        // require both our loopback Host AND the per-run token header. The Host
        // check defeats DNS-rebinding; the token defeats a forged same-origin
        // POST from any other local page/process that can reach the loopback
        // port but can't read the token we baked into the served page URL.
        if (!_isAuthorized(request)) {
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
          if (request.contentLength != -1 &&
              request.contentLength > maxEventBytes) {
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
        } catch (_) {
          /* ignore malformed or oversized event bodies */
        }
        response.headers.set(HttpHeaders.cacheControlHeader, 'no-store');
        response.statusCode = HttpStatus.ok;
        await response.close();
        return;
      }
      // DSS2 photographic HiPS tiles are cached on this computer. The page's
      // data source points at this fixed prefix; a cache miss fetches only the
      // requested path from the fixed CDS origin, then stores it for offline
      // use. No arbitrary proxying is allowed.
      if (_isDssPath(path)) {
        if (!_hasDssPrefix(path)) {
          // Wrong or missing token in the path: not our page.
          response.statusCode = HttpStatus.forbidden;
          await response.close();
          return;
        }
        // Cache misses cost an upstream fetch and a disk write, so at least
        // hold this to our own loopback Host like the control channels (a
        // DNS-rebinding page must not be able to drive the cache).
        if (!_isLoopbackHost(request)) {
          response.statusCode = HttpStatus.forbidden;
          await response.close();
          return;
        }
        await _serveDss(request, path);
        return;
      }
      if (path == '/aracat' || path.startsWith('/aracat/')) {
        if (!_isAuthorized(request)) {
          response.statusCode = HttpStatus.forbidden;
          await response.close();
          return;
        }
        Object? payload;
        if (path == '/aracat') {
          payload = await catalogListResolver?.call();
        } else {
          final id = Uri.decodeComponent(path.substring('/aracat/'.length));
          final limit = int.tryParse(request.uri.queryParameters['limit'] ?? '') ?? 500;
          payload = await catalogObjectsResolver?.call(id, limit);
        }
        if (payload == null) {
          response.statusCode = HttpStatus.notFound;
          await response.close();
          return;
        }
        response.headers.contentType =
            ContentType('application', 'json', charset: 'utf-8');
        response.headers.set(HttpHeaders.cacheControlHeader, 'no-store');
        response.write(jsonEncode(payload));
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
      final bytes = data.buffer.asUint8List(
        data.offsetInBytes,
        data.lengthInBytes,
      );
      response.headers.contentType = _contentTypeFor(path);
      // Everything here is read from the local asset bundle over loopback, so a
      // webview cache buys nothing — and WKWebView/WebKitGTK caching a stale
      // index.html/JS across app updates breaks the page. Forbid caching.
      response.headers.set(HttpHeaders.cacheControlHeader, 'no-store');
      // The engine fetches gzipped data (e.g. the satellite TLEs) and inflates it
      // itself, so never let the HTTP layer claim/translate the encoding.
      response.headers.set(HttpHeaders.acceptRangesHeader, 'bytes');
      // The star/DSO tile loader uses HTTP Range requests; honour them with a 206
      // partial response (a plain 200-with-full-body confuses the loader → no stars).
      final r = _parseRange(range, bytes.length);
      if (r != null) {
        response.statusCode = HttpStatus.partialContent;
        response.headers.set(
          HttpHeaders.contentRangeHeader,
          'bytes ${r.$1}-${r.$2}/${bytes.length}',
        );
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
      } catch (_) {
        /* response already closed/detached */
      }
    }
  }

  Future<void> _serveDss(HttpRequest request, String path) async {
    final response = request.response;
    if (request.method != 'GET' && request.method != 'HEAD') {
      response.statusCode = HttpStatus.methodNotAllowed;
      response.headers.set(HttpHeaders.allowHeader, 'GET, HEAD');
      await response.close();
      return;
    }
    // Page → "is the photo backdrop expected to be blank?" (see [_dssOffline]).
    // (Shadows an upstream resource literally named `status`; HiPS has none.)
    if (path == '${_dssPathPrefix}status') {
      response.headers.contentType =
          ContentType('application', 'json', charset: 'utf-8');
      response.headers.set(HttpHeaders.cacheControlHeader, 'no-store');
      response.write(jsonEncode({'offline': _dssOffline}));
      await response.close();
      return;
    }
    final rel = dssRelativePathFrom(path, _dssPathPrefix);
    if (rel == null) {
      response.statusCode = HttpStatus.forbidden;
      await response.close();
      return;
    }
    // The engine's cache-buster (`Allsky.jpg?v=<release_date>`) is part of the
    // cache key, so a survey re-release is fetched again instead of served
    // from the old tile for ever (#1143). Anything else in the query is
    // ignored, as before.
    final buster = request.uri.queryParameters['v'];
    final key = buster != null && _dssSegment.hasMatch(buster)
        ? '$rel@$buster'
        : rel;
    final file = File('${_dssCacheDir.path}/$key');
    Uint8List? bytes;
    var shed = false;
    try {
      if (key != rel && !await file.exists()) {
        // A tile cached before the key carried the buster: adopt it under
        // the new name rather than re-download it (and leave the old file to
        // sit until eviction). Lazy, one rename, only on a miss.
        final legacy = File('${_dssCacheDir.path}/$rel');
        if (await legacy.exists()) {
          try {
            await legacy.rename(file.path);
          } catch (_) {/* raced with a write: fall through to a fetch */}
        }
      }
      if (await file.exists()) {
        bytes = await file.readAsBytes();
      } else if (_dssRetryAfter == null ||
          DateTime.now().isAfter(_dssRetryAfter!)) {
        // Coalesce duplicate tile requests from the engine's render workers.
        var inFlight = _dssFetches[key];
        if (inFlight == null) {
          await _awaitFetchSlot();
          inFlight = _dssFetches[key]; // another worker may have started it
          // Offline may have been established while waiting: a fetch now
          // would only arm the backoff again.
          if (inFlight == null &&
              _dssRetryAfter != null &&
              DateTime.now().isBefore(_dssRetryAfter!)) {
            shed = true;
          }
        }
        if (!shed) {
          final pending =
              inFlight ?? (_dssFetches[key] = _fetchDss(key, buster, file));
          try {
            bytes = await pending;
          } finally {
            if (identical(_dssFetches[key], pending)) _dssFetches.remove(key);
          }
        }
      }
    } catch (e, st) {
      debugPrint('StellariumServer: DSS cache failed for $key: $e\n$st');
    }
    if (bytes == null) {
      if (shed) response.headers.set('x-ara-dss-backoff', '1');
      // A missing offline tile is a normal cache miss. The vector sky and frame
      // overlay remain usable; the page simply has no photographic backdrop.
      response.statusCode = HttpStatus.notFound;
      response.headers.set(HttpHeaders.cacheControlHeader, 'no-store');
      await response.close();
      return;
    }
    response.headers.contentType = _contentTypeFor(path);
    response.headers.set(
      HttpHeaders.cacheControlHeader,
      'public, max-age=31536000, immutable',
    );
    response.headers.set(HttpHeaders.contentLengthHeader, bytes.length);
    if (request.method == 'GET') response.add(bytes);
    await response.close();
  }

  /// Map a request path under `/dss/` onto the cache-relative tile path, or
  /// null when it must be refused: anything outside the prefix, an empty or
  /// dot segment (`/dss//properties`, `..`), or a character outside the HiPS
  /// name alphabet. Only such a validated path ever reaches the disk or CDS.
  @visibleForTesting
  static String? dssRelativePath(String path) =>
      dssRelativePathFrom(path, '/dss/');

  /// [dssRelativePath] under an explicit route prefix (the per-run one).
  @visibleForTesting
  static String? dssRelativePathFrom(String path, String prefix) {
    if (!path.startsWith(prefix)) return null;
    final relative = path.substring(prefix.length);
    if (relative.isEmpty) return null;
    for (final part in relative.split('/')) {
      if (part == '.' || part == '..' || !_dssSegment.hasMatch(part)) {
        return null;
      }
    }
    return relative;
  }

  Future<Uint8List?> _fetchDss(String key, String? buster, File file) async {
    // The cache key carries the buster suffix; upstream gets it as the query
    // the engine sent (CDS serves the same bytes either way).
    final relative = buster == null ? key : key.substring(0, key.length - buster.length - 1);
    final uri = dssOrigin
        .resolve(relative)
        .replace(queryParameters: buster == null ? null : {'v': buster});
    HttpClientRequest? req;
    final Uint8List bytes;
    try {
      req = await _dssClient.getUrl(uri);
      req.headers.set(HttpHeaders.userAgentHeader, 'OpenAstroAra DSS cache');
      // A captive portal answers every URL with a 302 to its login page, and a
      // followed redirect would land here as a 200. The survey never redirects
      // a tile, so a 3xx is never a tile: see it, refuse it below.
      req.followRedirects = false;
      final upstream = await req.close().timeout(dssHeadersTimeout);
      if (upstream.statusCode == HttpStatus.notFound) {
        // A 404 is the survey's own answer (a tile outside its coverage), not
        // a connectivity failure — no backoff, and it counts as "online".
        // Bounded like every other read: a 404 whose body stalls (half-open
        // TCP after a hotspot switch) must not pin the coalesced fetch and the
        // page's socket for the app's life.
        await upstream.drain<void>().timeout(dssBodyTimeout);
        _dssLastSuccess = DateTime.now();
        return null;
      }
      // Only a 200 carrying the resource's own media type is a tile. A captive
      // portal (hotel / campground Wi-Fi) answers with a 302 to its login page
      // or a 200 of HTML; persisting either would serve HTML as image/jpeg,
      // immutable, until the cache is deleted by hand. Refuse and treat it as
      // offline, which for the user it is — the Frame hint then says so.
      if (upstream.statusCode != HttpStatus.ok ||
          !_isDssMediaType(relative, upstream.headers.contentType)) {
        // Drain first: the response is complete, so abort() below is a no-op
        // and an unread body would pin the socket until the client closes.
        await upstream.drain<void>().timeout(dssBodyTimeout);
        throw HttpException(
          'upstream answered ${upstream.statusCode} '
          '${upstream.headers.contentType?.mimeType ?? "(no content type)"} '
          'for $relative',
          uri: uri,
        );
      }
      final body = await _readCapped(upstream).timeout(dssBodyTimeout);
      if (body == null) {
        // Over the cap: refuse, don't persist, and don't leave the rest of the
        // body streaming into nowhere. Neither a success nor an outage.
        try {
          req.abort();
        } catch (_) {/* already finished */}
        return null;
      }
      _dssLastSuccess = DateTime.now();
      bytes = body;
    } on Object catch (e) {
      // Joining the SBC hotspot removes the Internet route. Avoid making every
      // visible tile wait through another socket timeout while offline; cached
      // tiles still serve immediately during the backoff window.
      _dssLastFailure = DateTime.now();
      _dssRetryAfter = DateTime.now().add(const Duration(seconds: 30));
      // A timed-out request/response must not linger on the socket.
      try {
        req?.abort();
      } catch (_) {/* already finished */}
      debugPrint('StellariumServer: DSS fetch failed for $relative: $e');
      return null;
    }
    // Persist separately: the bytes arrived fine, so a disk problem (full,
    // read-only) must not arm the "offline" backoff — serve them and move on.
    try {
      await file.parent.create(recursive: true);
      final part = File(
        '${file.path}.part-${DateTime.now().microsecondsSinceEpoch}',
      );
      await part.writeAsBytes(bytes, flush: true);
      await part.rename(file.path);
      _dssBytesSincePrune += bytes.length;
      unawaited(_pruneIfNeeded());
    } catch (e) {
      debugPrint('StellariumServer: DSS cache write failed for $relative: $e');
    }
    return bytes;
  }

  /// True when [type] is what the survey serves for this resource: the
  /// `properties` manifest is text (`text/plain` at CDS), every other HiPS
  /// resource is an image. Anything else — HTML, JSON, nothing — is not the
  /// survey talking and must never be persisted as a tile.
  @visibleForTesting
  static bool isDssMediaType(String relative, ContentType? type) =>
      _isDssMediaType(relative, type);

  static bool _isDssMediaType(String relative, ContentType? type) {
    if (type == null) return false;
    if (relative == 'properties' || relative.endsWith('/properties')) {
      return type.primaryType == 'text' && type.subType != 'html';
    }
    return type.primaryType == 'image';
  }

  /// Read the whole upstream body, or null once it exceeds [maxDssResourceBytes].
  static Future<Uint8List?> _readCapped(HttpClientResponse upstream) async {
    final builder = BytesBuilder(copy: false);
    await for (final chunk in upstream) {
      if (builder.length + chunk.length > maxDssResourceBytes) return null;
      builder.add(chunk);
    }
    return builder.takeBytes();
  }

  /// Parse a single `bytes=start-end` Range header into inclusive byte offsets,
  /// clamped to the resource length. Returns null for absent/unsatisfiable/multi
  /// ranges (the caller then serves the whole body).
  @visibleForTesting
  static (int, int)? parseRange(String? header, int length) =>
      _parseRange(header, length);

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
    // A HiPS `properties` manifest has no extension.
    if (path.endsWith('/properties')) {
      return ContentType('text', 'plain', charset: 'utf-8');
    }
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
      case 'jpg':
      case 'jpeg':
        return ContentType('image', 'jpeg');
      case 'gz':
        return ContentType('application', 'gzip');
      default:
        return ContentType('application', 'octet-stream');
    }
  }

  Future<void> dispose() async {
    await _events.close();
    _dssClient.close(force: true);
    await _server.close(force: true);
    if (identical(await _instance, this)) _instance = null;
    if (_dssCacheIsTemp) {
      try {
        await _dssCacheDir.delete(recursive: true);
      } catch (_) {/* already gone, or a straggling write: leave it */}
    }
  }
}
