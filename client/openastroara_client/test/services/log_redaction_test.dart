import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/log_redaction.dart';

void main() {
  group('LogRedaction.redactLine (§54.6)', () {
    test('our own token header is stripped in any casing', () {
      expect(
        LogRedaction.redactLine('headers: {X-Ara-Token: abcdefghij12345}'),
        'headers: {X-Ara-Token: [REDACTED-TOKEN]}',
      );
      expect(
        LogRedaction.redactLine('x-ara-token=abcdefghij12345&x=1'),
        'x-ara-token=[REDACTED-TOKEN]&x=1',
      );
    });

    test('bearer tokens, query tokens and vendor key prefixes', () {
      expect(
        LogRedaction.redactLine(
          'Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abc',
        ),
        'Authorization: Bearer [REDACTED-TOKEN]',
      );
      expect(
        LogRedaction.redactLine('GET /dss/x.jpg?token=abcdefghijkl'),
        'GET /dss/x.jpg?token=[REDACTED-TOKEN]',
      );
      expect(
        LogRedaction.redactLine('key ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123 here'),
        'key ghp_[REDACTED-TOKEN] here',
      );
      expect(
        LogRedaction.redactLine('xoxb-1234567890-abcdefghijk'),
        'xoxb-[REDACTED-TOKEN]',
      );
    });

    test('base64-looking runs with padding are stripped, hashes are not', () {
      expect(
        LogRedaction.redactLine(
          'secret=QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo0MTIzNDU2Nzg5MA==',
        ),
        'secret=[REDACTED-TOKEN]',
      );
      // A git SHA has no padding and is diagnostic.
      const sha = 'commit 6c42c9af4a1b2c3d4e5f60718293a4b5c6d7e8f9';
      expect(LogRedaction.redactLine(sha), sha);
    });

    test('hostnames, paths and coordinates are left alone (§54.1)', () {
      const line =
          '[discovery] openastro.local 192.168.1.235 /Users/joey/x lat=30.27';
      expect(LogRedaction.redactLine(line), line);
    });
  });

  group('LineRedactor', () {
    test('a private-key block becomes one placeholder line', () {
      final text = [
        'before',
        '-----BEGIN OPENSSH PRIVATE KEY-----',
        'b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZW',
        '-----END OPENSSH PRIVATE KEY-----',
        'after',
      ].join('\n');
      expect(LogRedaction.redactText(text), 'before\n[REDACTED-KEY]\nafter');
    });

    test('an unterminated block swallows the rest, a one-liner does not', () {
      expect(
        LogRedaction.redactText('a\n-----BEGIN RSA PRIVATE KEY-----\nxx\nyy'),
        'a\n[REDACTED-KEY]',
      );
      expect(
        LogRedaction.redactText(
          '-----BEGIN RSA PRIVATE KEY----- -----END RSA PRIVATE KEY-----\nz',
        ),
        '[REDACTED-KEY]\nz',
      );
    });
  });
}
