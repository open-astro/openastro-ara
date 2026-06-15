// url_launcher_platform_interface / plugin_platform_interface are transitive deps of
// url_launcher, imported here only to mock the launch platform channel in tests.
// ignore_for_file: depend_on_referenced_packages
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/backup_snapshot.dart';
import 'package:openastroara/models/clone_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/backup_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/backup/backup_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/widgets/backup/backup_restore_modal.dart';
import 'package:plugin_platform_interface/plugin_platform_interface.dart';
import 'package:url_launcher_platform_interface/url_launcher_platform_interface.dart';

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
  int creates = 0;
  bool throwOnRestore = false;

  @override
  Future<List<BackupSnapshot>> listSnapshots() async => snapshots;
  @override
  Future<String> createBackup() async {
    creates++;
    snapshots = [const BackupSnapshot(backupId: 'new', downloadUrl: '/dl/new'), ...snapshots];
    return 'op';
  }

  @override
  Future<String> restore({required String sourceUrl, required bool profiles, required bool sequences}) async {
    if (throwOnRestore) throw StateError('restore failed');
    return 'op-r';
  }

  // §43-2b: the restore worker's terminal state the modal polls for after the POST.
  CloneStatus cloneStatusResult = const CloneStatus(state: 'done', progressPct: 100);
  @override
  Future<CloneStatus> cloneStatus() async => cloneStatusResult;

  @override
  String absoluteDownloadUrl(BackupSnapshot snapshot) => 'http://h:5555${snapshot.downloadUrl}';
  @override
  void close() {}
}

class _FakeUrlLauncher extends Fake with MockPlatformInterfaceMixin implements UrlLauncherPlatform {
  String? launched;
  bool throwOnLaunch = false;

  @override
  Future<bool> launchUrl(String url, LaunchOptions options) async {
    if (throwOnLaunch) throw PlatformException(code: 'no_handler');
    launched = url;
    return true;
  }
}

const _server = AraServer(hostname: 'h', port: 5555);

Widget _host(
  _FakeBackupClient api, {
  List<AraServer> servers = const [_server],
  Duration pollInterval = const Duration(milliseconds: 1),
  Duration pollTimeout = const Duration(seconds: 5),
}) =>
    ProviderScope(
      overrides: [
        savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(servers)),
        backupApiFactoryProvider.overrideWithValue((_) => api),
      ],
      child: MaterialApp(
        home: BackupRestoreModal(
          restorePollInterval: pollInterval,
          restorePollTimeout: pollTimeout,
        ),
      ),
    );

void main() {
  // The download tests overwrite the global UrlLauncherPlatform.instance; restore it
  // so a later test file calling launchUrl doesn't hit a leftover fake.
  final original = UrlLauncherPlatform.instance;
  tearDown(() => UrlLauncherPlatform.instance = original);

  testWidgets('empty list shows the no-backups message', (tester) async {
    await tester.pumpWidget(_host(_FakeBackupClient(const [])));
    await tester.pumpAndSettle();
    expect(find.textContaining('No backups yet'), findsOneWidget);
  });

  testWidgets('no server shows the connect message', (tester) async {
    await tester.pumpWidget(_host(_FakeBackupClient(const []), servers: const []));
    await tester.pumpAndSettle();
    expect(find.textContaining('Connect to a server'), findsOneWidget);
  });

  testWidgets('renders a snapshot row with size, areas, and a Restore action', (tester) async {
    final api = _FakeBackupClient([
      BackupSnapshot(
        backupId: 'b1',
        createdUtc: DateTime.utc(2026, 5, 30, 12),
        sizeBytes: 2048,
        downloadUrl: '/api/v1/backup/snapshot/b1/download',
        includedAreas: const ['profiles', 'sequences'],
      ),
    ]);
    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();

    expect(find.textContaining('2.0 KB'), findsOneWidget);
    expect(find.textContaining('profiles, sequences'), findsOneWidget);
    expect(find.text('Restore'), findsOneWidget);
  });

  testWidgets('Create backup invokes createBackup', (tester) async {
    final api = _FakeBackupClient(const []);
    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Create backup'));
    await tester.pumpAndSettle();
    expect(api.creates, 1);
  });

  testWidgets('Download launches the snapshot URL', (tester) async {
    final launcher = _FakeUrlLauncher();
    UrlLauncherPlatform.instance = launcher;
    final api = _FakeBackupClient([
      const BackupSnapshot(backupId: 'b1', downloadUrl: '/api/v1/backup/snapshot/b1/download'),
    ]);
    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();

    await tester.tap(find.byTooltip('Download'));
    await tester.pumpAndSettle();
    expect(launcher.launched, contains('/api/v1/backup/snapshot/b1/download'));
  });

  testWidgets('Download failure surfaces a snackbar instead of swallowing the error', (tester) async {
    UrlLauncherPlatform.instance = _FakeUrlLauncher()..throwOnLaunch = true;
    final api = _FakeBackupClient([const BackupSnapshot(backupId: 'b1', downloadUrl: '/dl/b1')]);
    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();

    await tester.tap(find.byTooltip('Download'));
    await tester.pumpAndSettle();
    expect(find.textContaining('Could not open the download'), findsOneWidget);
  });

  testWidgets('Restore on a snapshot with no areas refuses with a snackbar', (tester) async {
    final api = _FakeBackupClient([const BackupSnapshot(backupId: 'b1', downloadUrl: '/dl/b1', includedAreas: [])]);
    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Restore'));
    await tester.pump();
    expect(find.text('Restore backup'), findsNothing, reason: 'no confirm dialog opens');
    expect(find.textContaining('no restorable areas'), findsOneWidget);
  });

  testWidgets('Restore opens a destructive confirm dialog with area checkboxes', (tester) async {
    final api = _FakeBackupClient([
      const BackupSnapshot(backupId: 'b1', downloadUrl: '/dl/b1', includedAreas: ['profiles', 'sequences']),
    ]);
    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Restore'));
    await tester.pumpAndSettle();
    expect(find.textContaining('overwrites the selected areas'), findsOneWidget);
    expect(find.text('Profile settings'), findsOneWidget);
    expect(find.text('Sequences'), findsOneWidget);
  });

  testWidgets('a failed restore surfaces a snackbar', (tester) async {
    final api = _FakeBackupClient([
      const BackupSnapshot(backupId: 'b1', downloadUrl: '/dl/b1', includedAreas: ['profiles', 'sequences']),
    ])
      ..throwOnRestore = true;
    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Restore')); // row action → opens the confirm dialog
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Restore')); // confirm
    await tester.pumpAndSettle();
    expect(find.textContaining('Restore failed'), findsOneWidget);
  });

  testWidgets('a worker-side restore failure (clone-status failed) surfaces a snackbar', (tester) async {
    // §43-2b: the POST succeeds (202), but the background worker fails — the modal
    // now polls clone-status and must surface that, not a premature "complete".
    final api = _FakeBackupClient([
      const BackupSnapshot(backupId: 'b1', downloadUrl: '/dl/b1', includedAreas: ['profiles', 'sequences']),
    ])
      ..cloneStatusResult = const CloneStatus(state: 'failed', message: 'disk gone');
    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Restore'));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Restore'));
    await tester.pumpAndSettle();
    expect(find.textContaining('disk gone'), findsOneWidget);
  });

  testWidgets('a restore that never reaches a terminal state surfaces the timeout snackbar', (tester) async {
    // §43-2b: the worker stays `running` past the poll deadline — the modal must
    // surface a "taking longer" message via the on TimeoutException branch, not hang.
    final api = _FakeBackupClient([
      const BackupSnapshot(backupId: 'b1', downloadUrl: '/dl/b1', includedAreas: ['profiles', 'sequences']),
    ])
      ..cloneStatusResult = const CloneStatus(state: 'running', progressPct: 10);
    await tester.pumpWidget(_host(
      api,
      pollInterval: const Duration(milliseconds: 1),
      pollTimeout: const Duration(milliseconds: 30),
    ));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Restore'));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Restore'));
    await tester.pumpAndSettle();
    expect(find.textContaining('taking longer'), findsOneWidget);
  });
}
