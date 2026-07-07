import 'dart:io';
import 'dart:typed_data';

import 'package:crypto/crypto.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/backup_stream.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/backup_stream_api.dart';
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
  late _FakeClient fake;
  late ProviderContainer container;

  ProviderContainer buildContainer() => ProviderContainer(overrides: [
        savedServersProvider.overrideWith(_FixedServers.new),
        // No real socket in tests — the controller's frame.complete nudge is a
        // convenience over the poll loop, which these tests drive directly.
        wsEventStreamProvider.overrideWith((ref) => null),
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
    try {
      await tempDir.delete(recursive: true);
    } on FileSystemException {
      // best-effort temp cleanup
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

  test('a corrupted download retries once then skips without acking', () async {
    final c = controller();
    fake.addFrame('frame-bad', 'GOOD-BYTES');
    fake.corruptor = (_) => Uint8List.fromList('TAMPERED'.codeUnits);

    await c.setEnabled(true);
    await settle();

    expect(fake.acked, isEmpty, reason: 'a sha mismatch must never ack');
    expect(container.read(backupStreamProvider).syncedThisSession, 0);
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
