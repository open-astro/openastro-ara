import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/storage_devices_api.dart';
import 'package:openastroara/widgets/storage/storage_drive_dialog.dart';

void main() {
  const disk = StorageDevice(
    path: '/dev/sdc1',
    uuid: '6EF6-FD21',
    label: 'ARA-DISK',
    removable: true,
    isSystemDisk: false,
    isAraStore: false,
  );

  StorageConfigureOutcome failure(String code, [String? detail]) =>
      StorageConfigureOutcome(success: false, code: code, detail: detail);

  test('a busy current store says the switch is what is blocked', () {
    final text = storageConfigureMessage(
        failure('device_busy', 'previous_store'), disk);
    expect(text, contains('frames are saved to now is in use'));
    expect(text, contains('switch'));
  });

  test('a busy chosen disk keeps the plain in-use message', () {
    expect(storageConfigureMessage(failure('device_busy'), disk),
        'The disk is in use. Close anything reading it, then try again.');
  });

  test('an unknown code falls back to the helper detail, then the disk name', () {
    expect(storageConfigureMessage(failure('odd', 'helper said so'), disk),
        'helper said so');
    expect(storageConfigureMessage(failure('odd'), disk),
        'Could not use ARA-DISK.');
  });
}
