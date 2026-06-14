import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/backup_snapshot.dart';

void main() {
  group('BackupSnapshot.fromJson', () {
    test('parses a full snake_case payload', () {
      final s = BackupSnapshot.fromJson(const <String, dynamic>{
        'backup_id': '77777777-7777-7777-7777-777777777771',
        'created_utc': '2026-05-30T00:00:00Z',
        'size_bytes': 12345678,
        'sha256': 'abc123',
        'download_url': '/api/v1/backup/snapshot/77777777-7777-7777-7777-777777777771/download',
        'included_areas': <String>['profiles', 'sequences'],
      });
      expect(s.backupId, '77777777-7777-7777-7777-777777777771');
      expect(s.createdUtc, DateTime.utc(2026, 5, 30));
      expect(s.sizeBytes, 12345678);
      expect(s.sha256, 'abc123');
      expect(s.downloadUrl, contains('/download'));
      expect(s.includedAreas, <String>['profiles', 'sequences']);
    });

    test('degrades missing/wrong-typed fields rather than throwing', () {
      final s = BackupSnapshot.fromJson(const <String, dynamic>{
        'backup_id': 'x',
        'size_bytes': 'nope',
        'included_areas': 'not-a-list',
      });
      expect(s.backupId, 'x');
      expect(s.sizeBytes, 0, reason: 'a non-numeric size degrades to 0');
      expect(s.createdUtc, isNull);
      expect(s.includedAreas, isEmpty, reason: 'a non-list included_areas degrades to empty');
    });

    test('included_areas drops non-string entries', () {
      final s = BackupSnapshot.fromJson(const <String, dynamic>{
        'backup_id': 'x',
        'included_areas': <dynamic>['profiles', 7, null, 'sequences'],
      });
      expect(s.includedAreas, <String>['profiles', 'sequences']);
    });

    test('value equality holds for identical payloads', () {
      const json = <String, dynamic>{
        'backup_id': 'a',
        'size_bytes': 10,
        'included_areas': <String>['profiles'],
      };
      expect(BackupSnapshot.fromJson(json), equals(BackupSnapshot.fromJson(json)));
    });

    test('value equality is sensitive to included_areas order', () {
      final a = BackupSnapshot.fromJson(const {'backup_id': 'a', 'included_areas': ['profiles', 'sequences']});
      final b = BackupSnapshot.fromJson(const {'backup_id': 'a', 'included_areas': ['sequences', 'profiles']});
      expect(a, isNot(equals(b)));
    });
  });
}
