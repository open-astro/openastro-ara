import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/stats_export_api.dart';

void main() {
  group('StatsExportApi.astrobinExportUrl', () {
    final api = StatsExportApi(const AraServer(hostname: 'pi', port: 5555), dio: Dio());

    test('builds an absolute export URL against the server base', () {
      final url = api.astrobinExportUrl('M31');
      expect(url, 'http://pi:5555/api/v1/stats/export/astrobin?target=M31');
    });

    test('percent-encodes a target with spaces and reserved chars', () {
      final url = api.astrobinExportUrl('Sh2-155 & friends');
      final uri = Uri.parse(url);
      expect(uri.path, '/api/v1/stats/export/astrobin');
      // The decoded query value round-trips exactly — encoding is correct.
      expect(uri.queryParameters['target'], 'Sh2-155 & friends');
    });
  });
}
