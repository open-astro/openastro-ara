import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/content_disposition.dart';

void main() {
  group('fileNameFromContentDisposition', () {
    test('plain quoted filename', () {
      expect(
        fileNameFromContentDisposition('attachment; filename="report.zip"'),
        'report.zip',
      );
    });

    test('plain unquoted filename', () {
      expect(
        fileNameFromContentDisposition('attachment; filename=report.zip'),
        'report.zip',
      );
    });

    test('RFC 5987 filename* (percent-encoded, prefers over plain)', () {
      expect(
        fileNameFromContentDisposition(
            "attachment; filename=fallback.zip; filename*=UTF-8''open%20astro.zip"),
        'open astro.zip',
      );
    });

    test('strips a path component (basename, defence-in-depth)', () {
      expect(
        fileNameFromContentDisposition('attachment; filename="../../evil.zip"'),
        'evil.zip',
      );
    });

    test('null header → null', () {
      expect(fileNameFromContentDisposition(null), isNull);
    });

    test('no filename token → null', () {
      expect(fileNameFromContentDisposition('attachment'), isNull);
    });

    test('malformed prefix-less filename* falls through to default (null)', () {
      // Missing the charset'language' prefix → not matched by the extended
      // branch, and the plain branch needs `filename=` not `filename*=`.
      expect(
        fileNameFromContentDisposition('attachment; filename*=evil.zip'),
        isNull,
      );
    });
  });
}
