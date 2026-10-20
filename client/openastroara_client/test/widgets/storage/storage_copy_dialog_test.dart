import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/storage_copy_api.dart';
import 'package:openastroara/services/storage_devices_api.dart';
import 'package:openastroara/widgets/storage/storage_copy_dialog.dart';

const _store = StorageDevice(
  path: '/dev/sdc1',
  uuid: '6EF6-FD21',
  label: 'ARA-DISK',
  model: 'USB 3.0 FD',
  fileSystem: 'exfat',
  mountPoint: '/media/openastroara',
  removable: true,
  isSystemDisk: false,
  isAraStore: true,
);
const _other = StorageDevice(
  path: '/dev/sdb1',
  uuid: 'EBDF-D246',
  label: 'ARA-DISK',
  model: 'Flash Drive FIT',
  fileSystem: 'exfat',
  removable: true,
  isSystemDisk: false,
  isAraStore: false,
);
const _ntfs = StorageDevice(
  path: '/dev/sdd1',
  uuid: '1234-ABCD',
  label: 'WINDOWS',
  fileSystem: 'ntfs',
  removable: true,
  isSystemDisk: false,
  isAraStore: false,
);
const _root = StorageDevice(
  path: '/dev/mmcblk0p2',
  uuid: '15f4c6be',
  fileSystem: 'ext4',
  mountPoint: '/',
  removable: false,
  isSystemDisk: true,
  isAraStore: false,
);

void main() {
  test('only another writable drive is a copy target', () {
    expect(copyTargets([_store, _other, _ntfs, _root]), [_other]);
  });

  test('status reads the server JSON', () {
    final s = StorageCopyStatus.fromJson({
      'state': 'copying',
      'target_uuid': 'EBDF-D246',
      'folders': ['manual/M31'],
      'files_to_copy': 4,
      'files_copied': 1,
      'bytes_to_copy': 400,
      'bytes_copied': 100,
      'current_file': 'manual/M31/a.fits',
      'target_released': false,
    });
    expect(s.isRunning, isTrue);
    expect(s.fraction, 0.25);
    expect(s.folders, ['manual/M31']);
    expect(const StorageCopyStatus(state: 'preparing').fraction, isNull,
        reason: 'no total while the file list is built');
  });

  test('a finished copy says what was copied, skipped and left alone', () {
    final lines = copySummary(const StorageCopyStatus(
      state: 'done',
      filesCopied: 3,
      bytesCopied: 2600000000,
      filesSkipped: 2,
      filesConflicting: 1,
      targetReleased: true,
    ));
    expect(lines, [
      'Copied 3 files (2.6 GB).',
      '2 files were already on the drive.',
      '1 file had a different file with the same name on the drive and were left alone.',
      'Safe to unplug the drive.',
    ]);
  });

  test('a re-run with nothing new says so', () {
    expect(
        copySummary(const StorageCopyStatus(
                state: 'done', filesSkipped: 5, targetReleased: true))
            .first,
        'Nothing new to copy.');
  });

  test('a drive left mounted asks for an eject', () {
    final lines = copySummary(const StorageCopyStatus(
        state: 'done', filesCopied: 1, errorCode: 'release_failed'));
    expect(lines.last, contains('Eject it before unplugging'));
  });

  test('failures read in the user\'s terms', () {
    expect(copyFailureMessage('not_enough_space', null, bytesToCopy: 5000000000),
        'The drive doesn\'t have room for 5.0 GB.');
    expect(copyFailureMessage('refused', 'is_store'),
        'That drive is the one frames are saved to.');
    expect(copyFailureMessage('device_busy', 'mounted_elsewhere'),
        contains('somewhere else'));
    expect(copyFailureMessage('weird', 'helper said so'), 'helper said so');
  });

  Future<void> pump(WidgetTester tester, List<StorageDevice> devices) async {
    await tester.pumpWidget(ProviderScope(
      overrides: [
        storageDevicesProvider.overrideWith((ref) async => devices),
        storageCopyStatusProvider
            .overrideWith((ref) => Stream.value(StorageCopyStatus.idle)),
      ],
      child: MaterialApp(
        home: Builder(
          builder: (context) => TextButton(
            onPressed: () => showStorageCopyDialog(context),
            child: const Text('open'),
          ),
        ),
      ),
    ));
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
  }

  testWidgets('with no second drive the dialog says to plug one in',
      (tester) async {
    await pump(tester, [_store, _root]);
    expect(find.textContaining('No other drive to copy to'), findsOneWidget);
  });

  testWidgets('Copy waits for a target drive to be picked', (tester) async {
    await pump(tester, [_store, _other, _root]);
    expect(find.text('From ARA-DISK (USB 3.0 FD) to:'), findsOneWidget,
        reason: 'both drives are ARA-DISK; the model tells them apart');
    FilledButton copy() =>
        tester.widget<FilledButton>(find.widgetWithText(FilledButton, 'Copy'));
    expect(copy().onPressed, isNull);
    await tester.tap(find.textContaining('Flash Drive FIT'));
    await tester.pump();
    expect(copy().onPressed, isNotNull);
  });
}
