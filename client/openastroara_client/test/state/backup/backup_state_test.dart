import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/backup_snapshot.dart';
import 'package:openastroara/models/clone_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/backup_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/backup/backup_state.dart';
import 'package:openastroara/state/saved_server_state.dart';

class _FakeSavedServerService implements SavedServerService {
  _FakeSavedServerService(List<AraServer> stored) : _stored = [...stored];
  final List<AraServer> _stored;
  @override
  Future<List<AraServer>> loadAll() async => List.unmodifiable(_stored);
  @override
  Future<void> saveAll(List<AraServer> servers) async => _stored
    ..clear()
    ..addAll(servers);
  @override
  Future<void> add(AraServer server) async => _stored.add(server);
}

class _FakeBackupClient implements BackupClient {
  _FakeBackupClient(this.snapshots);
  List<BackupSnapshot> snapshots;
  int lists = 0;
  int creates = 0;
  bool throwOnCreate = false;
  bool throwOnList = false;
  String? lastRestoreSource;
  bool? lastRestoreProfiles;
  bool? lastRestoreSequences;
  bool throwOnRestore = false;

  @override
  Future<List<BackupSnapshot>> listSnapshots() async {
    lists++;
    if (throwOnList) throw StateError('list failed');
    return snapshots;
  }

  @override
  Future<String> createBackup() async {
    creates++;
    if (throwOnCreate) throw StateError('nothing to archive');
    final snap = BackupSnapshot(backupId: 'b${snapshots.length + 1}', downloadUrl: '/dl/${snapshots.length + 1}');
    snapshots = [snap, ...snapshots];
    return 'op-create';
  }

  @override
  Future<String> restore({required String sourceUrl, required bool profiles, required bool sequences}) async {
    lastRestoreSource = sourceUrl;
    lastRestoreProfiles = profiles;
    lastRestoreSequences = sequences;
    if (throwOnRestore) throw StateError('unknown snapshot');
    return 'op-restore';
  }

  // Returned in order by cloneStatus(), holding the last element once exhausted.
  List<CloneStatus> cloneStatusSequence = const [];
  int _cloneIdx = 0;
  int cloneStatusCalls = 0;

  @override
  Future<CloneStatus> cloneStatus() async {
    cloneStatusCalls++;
    if (cloneStatusSequence.isEmpty) {
      return const CloneStatus(state: 'idle');
    }
    final s = cloneStatusSequence[_cloneIdx];
    if (_cloneIdx < cloneStatusSequence.length - 1) {
      _cloneIdx++;
    }
    return s;
  }

  @override
  String absoluteDownloadUrl(BackupSnapshot snapshot) => 'http://h:5555${snapshot.downloadUrl}';

  @override
  void close() {}
}

const _server = AraServer(hostname: 'h', port: 5555);

ProviderContainer _container(List<AraServer> servers, BackupClient api) {
  final c = ProviderContainer(overrides: [
    savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(servers)),
    backupApiFactoryProvider.overrideWithValue((_) => api),
  ]);
  addTearDown(c.dispose);
  return c;
}

void main() {
  group('backupSnapshotsProvider', () {
    test('no saved server → null list', () async {
      final c = _container(const [], _FakeBackupClient(const []));
      await c.read(savedServersProvider.future);
      expect(await c.read(backupSnapshotsProvider.future), isNull);
    });

    test('loads snapshots from the active server', () async {
      final api = _FakeBackupClient(const [BackupSnapshot(backupId: 'b1')]);
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      final list = await c.read(backupSnapshotsProvider.future);
      expect(list, isNotNull);
      expect(list!.single.backupId, 'b1');
    });

    test('createBackup creates then refreshes so the new snapshot appears', () async {
      final api = _FakeBackupClient(const [BackupSnapshot(backupId: 'b1')]);
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(backupSnapshotsProvider.future);

      final id = await c.read(backupSnapshotsProvider.notifier).createBackup();
      expect(id, 'op-create');
      expect(api.creates, 1);
      final list = c.read(backupSnapshotsProvider).value;
      expect(list, isNotNull);
      expect(list!.length, 2, reason: 'the new snapshot is visible after the post-create refresh');
    });

    test('createBackup propagates a failure (the 422 nothing-to-archive contract)', () async {
      final api = _FakeBackupClient(const [])..throwOnCreate = true;
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(backupSnapshotsProvider.future);

      await expectLater(
        c.read(backupSnapshotsProvider.notifier).createBackup(),
        throwsA(isA<StateError>()),
      );
    });

    test('restore forwards the source url + area flags and returns the op id', () async {
      final snap = const BackupSnapshot(backupId: 'b1', downloadUrl: '/api/v1/backup/snapshot/b1/download');
      final api = _FakeBackupClient([snap]);
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(backupSnapshotsProvider.future);

      final id = await c
          .read(backupSnapshotsProvider.notifier)
          .restore(snap, profiles: true, sequences: false);
      expect(id, 'op-restore');
      expect(api.lastRestoreSource, '/api/v1/backup/snapshot/b1/download');
      expect(api.lastRestoreProfiles, isTrue);
      expect(api.lastRestoreSequences, isFalse);
    });

    test('restore propagates a failure (404/422 contract) without refreshing', () async {
      final snap = const BackupSnapshot(backupId: 'b1', downloadUrl: '/dl/b1');
      final api = _FakeBackupClient([snap])..throwOnRestore = true;
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(backupSnapshotsProvider.future);
      final listsBefore = api.lists;

      await expectLater(
        c.read(backupSnapshotsProvider.notifier).restore(snap, profiles: true, sequences: true),
        throwsA(isA<StateError>()),
      );
      expect(api.lists, listsBefore, reason: 'restore does not refresh the snapshot list');
    });

    test('awaitRestoreTerminal polls past running to a done status', () async {
      final api = _FakeBackupClient(const [BackupSnapshot(backupId: 'b1')])
        ..cloneStatusSequence = const [
          CloneStatus(state: 'running'),
          CloneStatus(state: 'running'),
          CloneStatus(state: 'done', progressPct: 100, message: 'Restored: profiles'),
        ];
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(backupSnapshotsProvider.future);

      final status = await c.read(backupSnapshotsProvider.notifier).awaitRestoreTerminal(
            interval: const Duration(milliseconds: 1),
          );
      expect(status.state, 'done');
      expect(status.isFailed, isFalse);
      expect(api.cloneStatusCalls, 3);
    });

    test('awaitRestoreTerminal surfaces a failed status with its message', () async {
      final api = _FakeBackupClient(const [BackupSnapshot(backupId: 'b1')])
        ..cloneStatusSequence = const [
          CloneStatus(state: 'running'),
          CloneStatus(state: 'failed', message: 'disk gone'),
        ];
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(backupSnapshotsProvider.future);

      final status = await c.read(backupSnapshotsProvider.notifier).awaitRestoreTerminal(
            interval: const Duration(milliseconds: 1),
          );
      expect(status.isFailed, isTrue);
      expect(status.message, 'disk gone');
    });

    test('awaitRestoreTerminal times out if the restore never finishes', () async {
      final api = _FakeBackupClient(const [BackupSnapshot(backupId: 'b1')])
        ..cloneStatusSequence = const [CloneStatus(state: 'running')]; // held forever
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(backupSnapshotsProvider.future);

      await expectLater(
        c.read(backupSnapshotsProvider.notifier).awaitRestoreTerminal(
              interval: const Duration(milliseconds: 1),
              timeout: const Duration(milliseconds: 30),
            ),
        throwsA(isA<TimeoutException>()),
      );
    });

    test('actions are no-ops (return null) when no server is bound', () async {
      final c = _container(const [], _FakeBackupClient(const []));
      await c.read(savedServersProvider.future);
      await c.read(backupSnapshotsProvider.future);
      final notifier = c.read(backupSnapshotsProvider.notifier);
      expect(await notifier.createBackup(), isNull);
      expect(
        await notifier.restore(const BackupSnapshot(backupId: 'b1', downloadUrl: '/dl/b1'),
            profiles: true, sequences: true),
        isNull,
      );
    });
  });
}
