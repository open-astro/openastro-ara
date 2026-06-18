import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/wizard/screens/screen_data_and_review.dart';

void main() {
  group('formatBytes', () {
    test('formats zero bytes', () {
      expect(formatBytes(0), '0 B');
    });

    test('scales to B / KB / MB / GB', () {
      expect(formatBytes(512), '512 B');
      expect(formatBytes(2 * 1024), '2 KB');
      expect(formatBytes(5 * 1024 * 1024), '5 MB');
      expect(formatBytes((1.5 * 1024 * 1024 * 1024).round()), '1.5 GB');
    });
  });
}
