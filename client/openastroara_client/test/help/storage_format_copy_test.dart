import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/help/registry.dart';
import 'package:openastroara/settings/registry.dart';

/// #1148 — the storage copy states what the daemon actually does.
void main() {
  test('compression help quotes the measured CM4 cost of the shipped codec',
      () {
    final body = helpRegistry['session.storage.compression']!.body;
    expect(body, contains('0.5 s'));
    expect(body, contains('22.5 MB'));
    expect(body, isNot(contains('1.4 s')),
        reason: 'the level-3 figure; the writer runs level 1 now');
  });

  test('backup-folder copy names both frame extensions', () {
    final help = helpRegistry['session.storage.backup_stream_folder']!.body;
    final setting = settingsRegistry
        .singleWhere((s) => s.id == 'session.storage.backup_stream_folder')
        .description;
    for (final text in [help, setting]) {
      expect(text, contains('.fits'));
      expect(text, contains('.xisf'));
    }
  });
}
