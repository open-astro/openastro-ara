import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/data_package.dart';

void main() {
  group('DataPackage.fromJson', () {
    test('parses a full snake_case payload', () {
      final p = DataPackage.fromJson(const <String, dynamic>{
        'id': 'tycho-2',
        'name': 'Tycho-2 star catalog',
        'description': '2.5M stars',
        'category': 'catalog',
        'size_bytes': 187654321,
        'version': 'v2024.10',
        'is_installed': true,
        'installed_utc': '2025-01-02T03:04:05Z',
        'source_url': 'https://data.openastro.net/tycho-2/2024.10.tar.gz',
      });
      expect(p.id, 'tycho-2');
      expect(p.sizeBytes, 187654321);
      expect(p.isInstalled, isTrue);
      expect(p.installedUtc, DateTime.utc(2025, 1, 2, 3, 4, 5));
      expect(p.sourceUrl, contains('tycho-2'));
    });

    test('degrades missing/wrong-typed fields rather than throwing', () {
      final p = DataPackage.fromJson(const <String, dynamic>{'id': 'x', 'size_bytes': 'nope', 'is_installed': 1});
      expect(p.id, 'x');
      expect(p.sizeBytes, 0, reason: 'a non-numeric size degrades to 0');
      expect(p.isInstalled, isFalse, reason: 'a non-bool is_installed degrades to false');
      expect(p.installedUtc, isNull);
    });

    test('value equality holds for identical payloads', () {
      const json = <String, dynamic>{'id': 'a', 'size_bytes': 10, 'is_installed': false};
      expect(DataPackage.fromJson(json), equals(DataPackage.fromJson(json)));
    });
  });

  group('DownloadProgress.fromPayload', () {
    test('parses a progress payload for the given phase', () {
      final p = DownloadProgress.fromPayload(const <String, dynamic>{
        'download_id': 'd1',
        'package_id': 'tycho-2',
        'downloaded_bytes': 512,
        'total_bytes': 1024,
        'percent_complete': 50.0,
      }, DownloadPhase.downloading);
      expect(p, isNotNull);
      expect(p!.packageId, 'tycho-2');
      expect(p.downloadId, 'd1');
      expect(p.percentComplete, 50.0);
      expect(p.isActive, isTrue);
    });

    test('carries the error string on a failed phase', () {
      final p = DownloadProgress.fromPayload(const <String, dynamic>{
        'download_id': 'd1',
        'package_id': 'tycho-2',
        'error': 'cancelled',
      }, DownloadPhase.failed);
      expect(p!.phase, DownloadPhase.failed);
      expect(p.error, 'cancelled');
      expect(p.isActive, isFalse);
    });

    test('clamps an out-of-range percent into [0, 100]', () {
      final over = DownloadProgress.fromPayload(
          const {'download_id': 'd', 'package_id': 'p', 'percent_complete': 150.0}, DownloadPhase.downloading);
      final under = DownloadProgress.fromPayload(
          const {'download_id': 'd', 'package_id': 'p', 'percent_complete': -5.0}, DownloadPhase.downloading);
      expect(over!.percentComplete, 100.0);
      expect(over.fraction, 1.0, reason: 'fraction is the [0,1] form for a progress bar');
      expect(under!.percentComplete, 0.0);
    });

    test('returns null when the package/download id is missing', () {
      expect(DownloadProgress.fromPayload(const <String, dynamic>{'package_id': 'x'}, DownloadPhase.downloading),
          isNull);
    });
  });
}
