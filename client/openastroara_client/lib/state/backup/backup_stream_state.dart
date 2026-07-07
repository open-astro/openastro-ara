import 'dart:async';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:path_provider/path_provider.dart';

import '../../models/backup_stream.dart';
import '../../models/server.dart';
import '../../services/backup_stream_api.dart';
import '../saved_server_state.dart';
import '../ws/ws_providers.dart';

/// §44 backup-stream puller state, surfaced in Settings → Storage and the
/// footer indicator.
@immutable
class BackupStreamState {
  final bool enabled;

  /// True while the claim is held and the poll loop runs.
  final bool active;
  final String localRoot;
  final int pendingCount;
  final int syncedThisSession;
  final int syncedBytesThisSession;

  /// Human-readable why-not when [enabled] but not [active] (slot held
  /// elsewhere, disk trouble, transport error) — null when healthy.
  final String? problem;

  const BackupStreamState({
    this.enabled = false,
    this.active = false,
    this.localRoot = '',
    this.pendingCount = 0,
    this.syncedThisSession = 0,
    this.syncedBytesThisSession = 0,
    this.problem,
  });

  BackupStreamState copyWith({
    bool? enabled,
    bool? active,
    String? localRoot,
    int? pendingCount,
    int? syncedThisSession,
    int? syncedBytesThisSession,
    String? problem,
    bool clearProblem = false,
  }) =>
      BackupStreamState(
        enabled: enabled ?? this.enabled,
        active: active ?? this.active,
        localRoot: localRoot ?? this.localRoot,
        pendingCount: pendingCount ?? this.pendingCount,
        syncedThisSession: syncedThisSession ?? this.syncedThisSession,
        syncedBytesThisSession: syncedBytesThisSession ?? this.syncedBytesThisSession,
        problem: clearProblem ? null : (problem ?? this.problem),
      );
}

/// The §44 desktop puller: claim the daemon's stream slot, poll the pending
/// queue (nudged by `frame.complete`), download each frame, verify its
/// SHA-256, write it under `<localRoot>/<server-host>/<sessionId>/`, and ack.
/// Pulls pause while an exposure is in flight when the daemon emits the
/// `camera.exposure_*` events (§44.4 capture-aware backoff-lite); otherwise
/// the fixed poll interval stands. A lost slot re-claims once (our own
/// hostname re-claims idempotently server-side); a second loss disables the
/// stream and surfaces the holder.
class BackupStreamController extends Notifier<BackupStreamState> {
  Timer? _pollTimer;
  Timer? _wsDebounce;
  bool _polling = false;
  bool _reclaimedOnce = false;
  bool _exposureInFlight = false;

  // ─── test seams ───
  BackupStreamClient Function(AraServer server)? clientFactory;
  String Function()? hostnameResolver;
  Future<String> Function()? defaultRootResolver;
  Duration pollInterval = const Duration(seconds: 15);

  BackupStreamClient? _client;
  String? _clientServerId;

  @override
  BackupStreamState build() {
    ref.onDispose(_teardown);
    ref.listen(wsEventsProvider, (prev, next) {
      final event = next.asData?.value;
      if (event == null) return;
      switch (event.type) {
        case 'frame.complete':
          if (!state.active) return;
          _wsDebounce?.cancel();
          _wsDebounce = Timer(const Duration(seconds: 2), _pollOnce);
        case 'camera.exposure_started':
          _exposureInFlight = true;
        case 'camera.exposure_complete':
        case 'camera.exposure_failed':
          _exposureInFlight = false;
      }
    });
    return const BackupStreamState();
  }

  String get _hostname => (hostnameResolver ?? () => Platform.localHostname)();

  BackupStreamClient? _resolveClient() {
    final server = ref.read(activeServerProvider);
    if (server == null) return null;
    if (_client == null || _clientServerId != server.baseUrl) {
      _client?.close();
      _client = (clientFactory ?? BackupStreamApi.new)(server);
      _clientServerId = server.baseUrl;
    }
    return _client;
  }

  /// Settings toggle entry point.
  Future<void> setEnabled(bool enabled) async {
    if (!enabled) {
      final client = _resolveClient();
      _stopLoop();
      state = state.copyWith(enabled: false, active: false, clearProblem: true);
      if (client != null) {
        try {
          await client.release(_hostname);
        } catch (e) {
          debugPrint('backup-stream release failed (best-effort): $e');
        }
      }
      return;
    }

    var root = state.localRoot;
    if (root.isEmpty) {
      root = await (defaultRootResolver ?? _defaultRoot)();
    }
    state = state.copyWith(enabled: true, localRoot: root, clearProblem: true);
    await _claimAndStart();
  }

  /// Settings folder field entry point.
  void setLocalRoot(String root) {
    state = state.copyWith(localRoot: root.trim());
  }

  static Future<String> _defaultRoot() async {
    final docs = await getApplicationDocumentsDirectory();
    return '${docs.path}${Platform.pathSeparator}OpenAstroAra${Platform.pathSeparator}Backups';
  }

  Future<void> _claimAndStart() async {
    final client = _resolveClient();
    if (client == null) {
      state = state.copyWith(active: false, problem: 'Connect to a server first.');
      return;
    }
    try {
      final granted = await client.claim(_hostname);
      if (!granted) {
        String? holder;
        try {
          holder = (await client.status()).activeTarget;
        } catch (_) {}
        state = state.copyWith(
            active: false,
            problem: 'Another desktop${holder == null ? '' : ' ($holder)'} is already streaming from this server.');
        return;
      }
      // NOTE: _reclaimedOnce is deliberately NOT reset here — only a
      // successful queue poll proves the slot is genuinely ours again
      // (resetting on claim alone would let a claim-then-lose flap reclaim
      // forever). _pollOnce clears it at the end of a clean pass.
      state = state.copyWith(active: true, clearProblem: true);
      _pollTimer?.cancel();
      _pollTimer = Timer.periodic(pollInterval, (_) => _pollOnce());
      unawaited(_pollOnce());
    } catch (e) {
      state = state.copyWith(active: false, problem: 'Could not start the backup stream: $e');
    }
  }

  Future<void> _pollOnce() async {
    if (!state.enabled || !state.active || _polling) return;
    // §44.4 capture-aware backoff-lite: never compete with an in-flight
    // exposure for the daemon's USB/IO; the timer retries after it completes.
    if (_exposureInFlight) return;
    final client = _resolveClient();
    if (client == null) return;
    _polling = true;
    BackupStreamSlotLostException? slotLost;
    try {
      final entries = await client.queue(_hostname);
      state = state.copyWith(pendingCount: entries.length, clearProblem: true);
      for (final entry in entries) {
        if (!state.enabled || !state.active) return;
        if (_exposureInFlight) return;
        // Null sha = the daemon hasn't hashed it yet (per-page budget) —
        // skip; it returns hashed on a later poll.
        if (entry.sha256 == null) continue;
        final ok = await _pullVerifyStore(client, entry) ||
            await _pullVerifyStore(client, entry); // one retry on sha mismatch/IO
        if (!ok) continue;
        await client.ack(_hostname, entry.id);
        state = state.copyWith(
          pendingCount: state.pendingCount > 0 ? state.pendingCount - 1 : 0,
          syncedThisSession: state.syncedThisSession + 1,
          syncedBytesThisSession: state.syncedBytesThisSession + entry.sizeBytes,
        );
      }
      _reclaimedOnce = false;
    } on BackupStreamSlotLostException catch (e) {
      // Handled below, AFTER the finally clears _polling — the reclaim's own
      // kick-off poll would otherwise be swallowed by the single-flight guard.
      slotLost = e;
    } catch (e) {
      // Transient transport/disk trouble: surface it, keep the loop; the next
      // tick retries. Persistent local-disk failure is the visible problem text.
      state = state.copyWith(problem: 'Backup stream hiccup: $e');
    } finally {
      _polling = false;
    }
    if (slotLost != null) {
      if (!_reclaimedOnce) {
        // Our own hostname re-claims idempotently; a crashed session resumes.
        _reclaimedOnce = true;
        await _claimAndStart();
      } else {
        _stopLoop();
        state = state.copyWith(
            enabled: false,
            active: false,
            problem: 'Backup stream stopped — ${slotLost.holder ?? 'another desktop'} took over the slot.');
      }
    }
  }

  Future<bool> _pullVerifyStore(BackupStreamClient client, BackupStreamQueueEntry entry) async {
    try {
      final bytes = await client.downloadFrame(entry.id);
      final digest = sha256.convert(bytes).toString();
      if (!_digestMatches(digest, entry.sha256!)) {
        debugPrint('backup-stream sha mismatch for ${entry.id} — retrying');
        return false;
      }
      final server = ref.read(activeServerProvider);
      final hostDir = _sanitize(server?.hostname ?? 'server');
      final dir = Directory(
          '${state.localRoot}${Platform.pathSeparator}$hostDir${Platform.pathSeparator}${_sanitize(entry.sessionId)}');
      await dir.create(recursive: true);
      final file = File('${dir.path}${Platform.pathSeparator}${_sanitize(entry.id)}.fits');
      // Temp + rename so a crash mid-write never leaves a plausible-looking
      // partial FITS that later reads as a corrupt backup.
      final tmp = File('${file.path}.tmp');
      await tmp.writeAsBytes(bytes, flush: true);
      await tmp.rename(file.path);
      return true;
    } on BackupStreamSlotLostException {
      rethrow;
    } catch (e) {
      debugPrint('backup-stream pull failed for ${entry.id}: $e');
      return false;
    }
  }

  static bool _digestMatches(String a, String b) => a.toLowerCase() == b.toLowerCase();

  /// Filenames come from server-issued GUIDs/hostnames, but never trust a path
  /// segment blindly — strip separators + parent-dir tokens.
  static String _sanitize(String segment) =>
      segment.replaceAll(RegExp(r'[/\\]'), '_').replaceAll('..', '_');

  void _stopLoop() {
    _pollTimer?.cancel();
    _pollTimer = null;
    _wsDebounce?.cancel();
    _wsDebounce = null;
  }

  void _teardown() {
    _stopLoop();
    _client?.close();
    _client = null;
  }
}

final backupStreamProvider =
    NotifierProvider<BackupStreamController, BackupStreamState>(BackupStreamController.new);
