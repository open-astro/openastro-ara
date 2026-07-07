import 'dart:io';
import 'dart:typed_data';

import 'package:crypto/crypto.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/backup_stream.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/backup_stream_api.dart';
import 'package:openastroara/services/backup_stream_prefs_service.dart';
import 'package:openastroara/state/backup/backup_stream_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';

/// §44 — the desktop puller against a scriptable fake client + a real temp
/// dir: claim → pull → sha-verify → temp-rename store → ack; the sha-mismatch
/// single retry; null-sha skip; slot-lost reclaim-once-then-disable; release
/// on disable.
class _FakeClient implements BackupStreamClient {
  final Map<String, Uint8List> frames = {};
  final List<BackupStreamQueueEntry> pending = [];
  final List<String> acked = [];
  int claimCalls = 0;
  int releaseCalls = 0;
  bool grantClaim = true;
  bool throwSlotLostOnQueue = false;

  /// Throws slot-lost on this many queue() calls, then behaves normally.
  int slotLostBudget = 0;

  /// Runs inside a (successful) queue() call — lets a test start an exposure
  /// between the queue read and the entry loop.
  Future<void> Function()? onQueue;
  Uint8List Function(String frameId)? corruptor;

  BackupStreamQueueEntry addFrame(String id, String payload, {bool withSha = true}) {
    final bytes = Uint8List.fromList(payload.codeUnits);
    frames[id] = bytes;
    final entry = BackupStreamQueueEntry(
      id: id,
      sha256: withSha ? sha256.convert(bytes).toString() : null,
      sizeBytes: bytes.length,
      capturedAt: DateTime.utc(2026, 1, 1),
      sessionId: 'session-1',
    );
    pending.add(entry);
    return entry;
  }

  @override
  Future<bool> claim(String hostname) async {
    claimCalls++;
    return grantClaim;
  }

  @override
  Future<void> release(String hostname) async => releaseCalls++;

  @override
  Future<BackupStreamStatus> status() async => BackupStreamStatus(
      enabled: true, activeTarget: 'other-desk', pendingCount: pending.length, syncedCount: 0, queueSizeBytes: 0);

  @override
  Future<List<BackupStreamQueueEntry>> queue(String hostname, {int limit = 50}) async {
    if (throwSlotLostOnQueue) throw const BackupStreamSlotLostException('other-desk');
    if (slotLostBudget > 0) {
      slotLostBudget--;
      throw const BackupStreamSlotLostException('other-desk');
    }
    await onQueue?.call();
    return List.of(pending);
  }

  @override
  Future<void> ack(String hostname, String frameId) async {
    acked.add(frameId);
    pending.removeWhere((e) => e.id == frameId);
  }

  @override
  Future<Uint8List> downloadFrame(String frameId) async {
    final bytes = frames[frameId];
    if (bytes == null) throw StateError('unknown frame $frameId');
    return corruptor?.call(frameId) ?? bytes;
  }

  @override
  void close() {}
}

void main() {
  late Directory tempDir;
  late Directory prefsDir;
  late _FakeClient fake;
  late ProviderContainer container;

  ProviderContainer buildContainer() => ProviderContainer(overrides: [
        savedServersProvider.overrideWith(_FixedServers.new),
        // No real socket in tests — the controller's frame.complete nudge is a
        // convenience over the poll loop, which these tests drive directly.
        wsEventStreamProvider.overrideWith((ref) => null),
        // Prefs land in a per-test temp dir, never the real app-support dir.
        backupStreamPrefsProvider.overrideWithValue(
            BackupStreamPrefsService(supportDir: () async => prefsDir)),
      ]);

  BackupStreamController controller() {
    final c = container.read(backupStreamProvider.notifier);
    c.clientFactory = (_) => fake;
    c.hostnameResolver = () => 'test-desk';
    c.defaultRootResolver = () async => tempDir.path;
    c.pollInterval = const Duration(hours: 1); // ticks driven manually via setEnabled's first poll
    return c;
  }

  setUp(() async {
    tempDir = await Directory.systemTemp.createTemp('oara-bstream-client');
    prefsDir = await Directory.systemTemp.createTemp('oara-bstream-prefs');
    fake = _FakeClient();
    container = buildContainer();
    // The active server derives from an ASYNC saved-servers load — settle it
    // before enabling, or the controller sees "no server yet".
    await container.read(savedServersProvider.future);
    // Materialize the provider so build() runs before seams are set.
    container.read(backupStreamProvider);
  });

  tearDown(() async {
    container.dispose();
    for (final dir in [tempDir, prefsDir]) {
      try {
        await dir.delete(recursive: true);
      } on FileSystemException {
        // best-effort temp cleanup
      }
    }
  });

  Future<void> settle() async {
    // The enable path runs claim + the first poll as untracked async work.
    for (var i = 0; i < 40; i++) {
      await Future<void>.delayed(const Duration(milliseconds: 25));
      if (!container.read(backupStreamProvider).enabled) break;
      if (container.read(backupStreamProvider).active && fake.pending.isEmpty) break;
    }
  }

  test('enable claims, pulls, verifies, stores under server/session, and acks', () async {
    final c = controller();
    final entry = fake.addFrame('frame-1', 'FITS-DATA-1');

    await c.setEnabled(true);
    await settle();

    final state = container.read(backupStreamProvider);
    expect(state.active, isTrue);
    expect(fake.acked, contains('frame-1'));
    expect(state.syncedThisSession, 1);
    expect(state.syncedBytesThisSession, entry.sizeBytes);
    final stored = File(
        '${tempDir.path}${Platform.pathSeparator}pi-test${Platform.pathSeparator}session-1${Platform.pathSeparator}frame-1.fits');
    expect(stored.existsSync(), isTrue, reason: 'the verified frame lands under <root>/<server>/<session>/');
    expect(stored.readAsStringSync(), 'FITS-DATA-1');
    expect(File('${stored.path}.tmp').existsSync(), isFalse, reason: 'temp file renamed away');
  });

  test('a corrupted download retries once then surfaces the problem without acking', () async {
    final c = controller();
    fake.addFrame('frame-bad', 'GOOD-BYTES');
    fake.corruptor = (_) => Uint8List.fromList('TAMPERED'.codeUnits);

    await c.setEnabled(true);
    await settle();

    expect(fake.acked, isEmpty, reason: 'a sha mismatch must never ack');
    final state = container.read(backupStreamProvider);
    expect(state.syncedThisSession, 0);
    expect(state.problem, contains('checksum'),
        reason: 'a persistently failing frame must be visible, not silently retried');
  });

  test('one persistently-bad frame does not block newer frames from mirroring', () async {
    final c = controller();
    fake.addFrame('frame-bad', 'GOOD-BYTES');
    fake.corruptor = (id) => id == 'frame-bad'
        ? Uint8List.fromList('TAMPERED'.codeUnits)
        : fake.frames[id]!;
    fake.addFrame('frame-good', 'FITS-DATA-2');

    await c.setEnabled(true);
    await settle();

    final state = container.read(backupStreamProvider);
    expect(fake.acked, contains('frame-good'),
        reason: 'the queue is oldest-first — a stuck head frame must not freeze the mirror');
    expect(fake.acked, isNot(contains('frame-bad')));
    expect(state.problem, contains('frame-bad'));
  });

  test('an unwritable backup folder surfaces the problem instead of looping silently', () async {
    final c = controller();
    // A regular FILE where the root dir should be — dir.create() fails.
    final blocker = File('${tempDir.path}${Platform.pathSeparator}blocker');
    await blocker.writeAsString('x');
    c.defaultRootResolver = () async => blocker.path;
    fake.addFrame('frame-1', 'FITS-DATA-1');

    await c.setEnabled(true);
    await settle();

    final state = container.read(backupStreamProvider);
    expect(fake.acked, isEmpty);
    expect(state.problem, isNotNull, reason: 'disk trouble must reach the panel');
  });

  test('a claim denied at enable time retries on the next tick without a manual off/on', () async {
    final c = controller();
    fake.grantClaim = false;
    fake.addFrame('frame-1', 'FITS-DATA-1');

    await c.setEnabled(true);
    await settle();
    var state = container.read(backupStreamProvider);
    expect(state.enabled, isTrue);
    expect(state.active, isFalse);
    expect(state.problem, contains('other-desk'));

    fake.grantClaim = true;
    await c.tickNow();
    await settle();

    state = container.read(backupStreamProvider);
    expect(state.active, isTrue, reason: 'the freed slot is picked up by the loop itself');
    expect(fake.acked, contains('frame-1'));
  });

  test('a stale exposure-in-flight flag self-heals past the watchdog', () async {
    final c = controller();
    fake.addFrame('frame-1', 'FITS-DATA-1');
    await c.setEnabled(true);
    await settle();

    fake.addFrame('frame-2', 'FITS-DATA-2');
    c.markExposureStarted(); // fresh — poll must hold off
    await c.tickNow();
    expect(fake.acked, isNot(contains('frame-2')),
        reason: 'an in-flight exposure pauses pulls');

    // Age the flag past the 15-minute watchdog: a missed completion event
    // must not stall the stream until app restart.
    c.markExposureStarted(DateTime.now().subtract(const Duration(minutes: 16)));
    await c.tickNow();
    await settle();
    expect(fake.acked, contains('frame-2'));
  });

  test('the toggle and folder persist and a persisted-on stream resumes after restart', () async {
    final c = controller();
    fake.addFrame('frame-1', 'FITS-DATA-1');
    await c.setEnabled(true);
    await settle();

    // "Restart": a fresh container over the same prefs dir.
    container.dispose();
    fake = _FakeClient();
    fake.addFrame('frame-2', 'FITS-DATA-2');
    container = buildContainer();
    await container.read(savedServersProvider.future);
    final c2 = controller();
    // The async prefs restore lands before the first tick.
    for (var i = 0; i < 40 && !container.read(backupStreamProvider).enabled; i++) {
      await Future<void>.delayed(const Duration(milliseconds: 25));
    }

    var state = container.read(backupStreamProvider);
    expect(state.enabled, isTrue, reason: 'the toggle survives a restart');
    expect(state.localRoot, tempDir.path, reason: 'the folder survives a restart');

    await c2.tickNow(); // resume rides the first loop beat
    await settle();
    state = container.read(backupStreamProvider);
    expect(state.active, isTrue);
    expect(fake.acked, contains('frame-2'));
  });

  test('entries without a sha are skipped until the daemon hashes them', () async {
    final c = controller();
    fake.addFrame('frame-nosha', 'BYTES', withSha: false);

    await c.setEnabled(true);
    await settle();

    expect(fake.acked, isEmpty);
    expect(container.read(backupStreamProvider).active, isTrue, reason: 'skipping is not an error');
  });

  test('a lost slot reclaims once, a second loss disables with the holder surfaced', () async {
    final c = controller();
    fake.throwSlotLostOnQueue = true;

    await c.setEnabled(true);
    await settle();
    // First loss → re-claim (claim called again) → second loss → disabled.
    for (var i = 0; i < 40 && container.read(backupStreamProvider).enabled; i++) {
      await Future<void>.delayed(const Duration(milliseconds: 25));
    }

    final state = container.read(backupStreamProvider);
    expect(fake.claimCalls, greaterThanOrEqualTo(2), reason: 'one idempotent re-claim attempt');
    expect(state.enabled, isFalse);
    expect(state.problem, contains('other-desk'));
  });

  test('a clean queue read re-arms the reclaim even when an exposure interrupts the pass', () async {
    final c = controller();
    fake.addFrame('frame-1', 'FITS-DATA-1');
    await c.setEnabled(true);
    await settle();
    expect(fake.acked, contains('frame-1'));

    // Flap 1: slot lost once, and the recovered pass is interrupted mid-loop
    // by an exposure starting — the successful queue read alone must re-arm
    // the one-time reclaim, or a busy imaging session (an exposure roughly
    // every poll) leaves it spent forever.
    fake.addFrame('frame-2', 'FITS-DATA-2');
    fake.slotLostBudget = 1;
    fake.onQueue = () async => c.markExposureStarted();
    await c.tickNow();
    for (var i = 0; i < 20; i++) {
      await Future<void>.delayed(const Duration(milliseconds: 25));
    }
    expect(fake.acked, isNot(contains('frame-2')),
        reason: 'the recovered pass was interrupted by the exposure');
    expect(container.read(backupStreamProvider).enabled, isTrue);

    // Flap 2 after the exposure ages out: must reclaim again, not disable.
    fake.onQueue = null;
    c.markExposureStarted(DateTime.now().subtract(const Duration(minutes: 16)));
    fake.slotLostBudget = 1;
    await c.tickNow();
    await settle();

    final state = container.read(backupStreamProvider);
    expect(state.enabled, isTrue,
        reason: 'an interrupted-but-clean pass must not leave the reclaim spent');
    expect(state.active, isTrue);
    expect(fake.acked, contains('frame-2'));
    expect(fake.claimCalls, 3, reason: 'enable + one reclaim per flap');
  });

  test('paceFloor: zero cap or degenerate size means no floor; math is bits over Mbps', () {
    expect(BackupStreamController.paceFloor(1000, 0), Duration.zero);
    expect(BackupStreamController.paceFloor(0, 10), Duration.zero);
    // 1 MB at 8 Mbps = 1 second.
    expect(BackupStreamController.paceFloor(1000000, 8),
        const Duration(seconds: 1));
  });

  test('a bandwidth cap paces between frames via the seam; first pull measures the link',
      () async {
    final c = controller();
    final requested = <Duration>[];
    c.pacer = (d) async => requested.add(d);
    fake.addFrame('frame-1', 'X' * 1000);
    fake.addFrame('frame-2', 'Y' * 1000);

    await c.setEnabled(true);
    await settle();
    c.setMaxMbps(1); // 1000 bytes → 8 ms floor per frame
    fake.addFrame('frame-3', 'Z' * 1000);
    await c.tickNow();
    await settle();

    expect(fake.acked, containsAll(['frame-1', 'frame-2', 'frame-3']));
    expect(requested, isNotEmpty, reason: 'the capped pull must pace');
    expect(requested.first.inMilliseconds, greaterThanOrEqualTo(4),
        reason: 'the requested wait is the 8 ms floor minus the (tiny) real elapsed');
    final state = container.read(backupStreamProvider);
    expect(state.measuredMbps, isNotNull,
        reason: 'the first pull doubles as the link measurement');
  });

  test('the bandwidth cap persists across restarts', () async {
    final c = controller();
    await c.setEnabled(true);
    await settle();
    c.setMaxMbps(25);

    container.dispose();
    fake = _FakeClient();
    container = buildContainer();
    await container.read(savedServersProvider.future);
    controller();
    for (var i = 0; i < 40 && !container.read(backupStreamProvider).enabled; i++) {
      await Future<void>.delayed(const Duration(milliseconds: 25));
    }

    expect(container.read(backupStreamProvider).maxMbps, 25);
  });

  test('disable releases the slot', () async {
    final c = controller();
    await c.setEnabled(true);
    await settle();

    await c.setEnabled(false);

    expect(fake.releaseCalls, 1);
    expect(container.read(backupStreamProvider).enabled, isFalse);
  });
}

/// savedServersProvider override with one fixed server.
class _FixedServers extends SavedServersNotifier {
  @override
  Future<List<AraServer>> build() async => const [AraServer(hostname: 'pi-test', port: 5555)];
}
