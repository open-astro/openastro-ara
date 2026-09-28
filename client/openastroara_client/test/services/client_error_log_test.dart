import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/client_error_log.dart';

void main() {
  late Directory dir;
  // Injected clock: one second per call. The header's "started" stamp takes
  // a tick too, so entry times are 0, 2, 3, … after a first entry.
  var tick = 0;
  setUp(() {
    tick = 0;
    dir = Directory.systemTemp.createTempSync('client_error_log');
  });
  tearDown(() => dir.deleteSync(recursive: true));

  ClientErrorLog log({int maxBytes = 512 * 1024, int ringCapacity = 200}) =>
      ClientErrorLog(
        supportDir: () async => dir,
        appVersion: () async => '0.0.1a+37 (test)',
        now: () => DateTime.utc(2026, 9, 27, 14, 11, tick++),
        maxBytes: maxBytes,
        ringCapacity: ringCapacity,
      );

  File current() => File('${dir.path}/${ClientErrorLog.fileName}');
  File rotated() => File('${dir.path}/${ClientErrorLog.rotatedFileName}');

  test('first entry writes a header, then context, error and stack', () async {
    final l = log();
    await l.record(
      'flutter_error',
      StateError('boom'),
      stack: StackTrace.fromString('#0 here\n#1 there'),
      context: 'while building Foo',
      library: 'widgets library',
    );
    final text = current().readAsStringSync();
    expect(text, startsWith('# OpenAstro Ara client error log\n'));
    expect(text, contains('# app: 0.0.1a+37 (test)\n'));
    expect(text, contains('# mode: debug\n'));
    expect(text, contains('=== 2026-09-27T14:11:00.000Z flutter_error ==='));
    expect(text, contains('library: widgets library\n'));
    expect(text, contains('context: while building Foo\n'));
    expect(text, contains('error: Bad state: boom\n'));
    expect(text, contains('stack:\n  #0 here\n  #1 there\n'));
    expect(l.status.value.entries, 1);
    expect(l.status.value.sessionEntries, 1);
    expect(l.status.value.lastKind, 'flutter_error');
    expect(l.status.value.lastMessage, 'Bad state: boom');
  });

  test('the debugPrint ring buffer is dumped into each entry, capped',
      () async {
    final l = log(ringCapacity: 3);
    for (var i = 0; i < 5; i++) {
      l.notePrint('line $i');
    }
    expect(l.recentPrints, ['line 2', 'line 3', 'line 4']);
    await l.record('uncaught', 'x');
    final text = current().readAsStringSync();
    expect(text, contains('recent debugPrint (3 lines):\n'));
    expect(text, contains('  line 2\n  line 3\n  line 4\n'));
    expect(text, isNot(contains('line 1')));
  });

  test('a second header is not written for a second entry', () async {
    final l = log();
    await l.record('a', 'one');
    await l.record('b', 'two');
    final text = current().readAsStringSync();
    expect('# OpenAstro Ara'.allMatches(text).length, 1);
    expect(ClientErrorLog.entryMarker.allMatches(text).length, 2);
  });

  test('rotation keeps one older file and starts a fresh headed one',
      () async {
    final l = log(maxBytes: 600);
    final big = 'x' * 300;
    await l.record('a', big);
    expect(rotated().existsSync(), isFalse);
    await l.record('b', big);
    expect(rotated().existsSync(), isTrue, reason: 'second entry overflowed');
    expect(rotated().readAsStringSync(), contains('=== 2026-09-27T14:11:00'));
    final fresh = current().readAsStringSync();
    expect(fresh, startsWith('# OpenAstro Ara client error log\n'));
    expect(fresh, contains(' b ==='));
    expect(fresh, isNot(contains(' a ===')));

    // A third overflow replaces the rotated file rather than keeping a chain.
    await l.record('c', big);
    expect(rotated().readAsStringSync(), contains(' b ==='));
    expect(rotated().readAsStringSync(), isNot(contains(' a ===')));
    expect(current().readAsStringSync(), contains(' c ==='));
  });

  test('exportTo concatenates the rotated file then the current one',
      () async {
    final l = log(maxBytes: 600);
    final big = 'x' * 300;
    await l.record('a', big);
    await l.record('b', big);
    final out = '${dir.path}/export.log';
    final bytes = await l.exportTo(out);
    final text = File(out).readAsStringSync();
    expect(bytes, text.length);
    expect(text.indexOf(' a ==='), lessThan(text.indexOf(' b ===')));
    expect(
      text,
      rotated().readAsStringSync() + current().readAsStringSync(),
    );
  });

  test('exportTo with nothing recorded still writes the header', () async {
    final l = log();
    final out = '${dir.path}/empty.log';
    await l.exportTo(out);
    expect(File(out).readAsStringSync(), startsWith('# OpenAstro Ara'));
  });

  test('concurrent records land whole and in order', () async {
    final l = log();
    await Future.wait([
      for (var i = 0; i < 20; i++) l.record('k$i', 'msg $i'),
    ]);
    final text = current().readAsStringSync();
    var last = -1;
    for (var i = 0; i < 20; i++) {
      final at = text.indexOf(' k$i ===');
      expect(at, greaterThan(last), reason: 'entry $i out of order');
      last = at;
    }
    expect(l.status.value.entries, 20);
  });

  test('a new instance counts what earlier runs left behind', () async {
    final first = log();
    await first.record('flutter_error', 'old failure\nsecond line');
    await first.note('launch');
    await first.record('uncaught', 'newest');

    final second = log();
    await second.idle;
    expect(second.status.value.entries, 2, reason: 'notes are not errors');
    expect(second.status.value.sessionEntries, 0);
    expect(second.status.value.lastKind, 'uncaught');
    expect(second.status.value.lastMessage, 'newest');
    expect(second.status.value.lastAt, DateTime.utc(2026, 9, 27, 14, 11, 3));

    await second.record('x', 'y');
    expect(second.status.value.entries, 3);
    expect(second.status.value.sessionEntries, 1);
  });

  test('a note is labelled as a message, not an error', () async {
    final l = log();
    await l.note('launch');
    expect(current().readAsStringSync(), contains('\nmessage: launch\n'));
    expect(l.status.value.entries, 0);
  });

  test('summary reads well empty and with entries', () async {
    final l = log();
    await l.idle;
    expect(l.summary(), 'client log: no errors recorded');
    await l.record('flutter_error', 'boom');
    expect(l.summary(), startsWith('client log: 1 entry, last '));
    expect(l.summary(), endsWith(' flutter_error: boom'));
  });

  test('an unwritable directory never throws and marks the log unavailable',
      () async {
    final l = ClientErrorLog(
      supportDir: () async => throw const FileSystemException('nope'),
      appVersion: () async => 'v',
    );
    await l.record('a', 'b');
    await l.note('c');
    expect(l.status.value.available, isFalse);
    expect(l.summary(), 'client log: unavailable (write failed)');
    await expectLater(
      l.exportTo('${dir.path}/never.log'),
      throwsA(isA<FileSystemException>()),
    );
  });

  test('an export to an unwritable path throws but leaves the log available',
      () async {
    final l = log();
    await l.record('a', 'b');
    await expectLater(
      l.exportTo('${dir.path}/missing-dir/out.log'),
      throwsA(isA<FileSystemException>()),
    );
    expect(l.status.value.available, isTrue);
    expect(l.status.value.entries, 1);
  });

  test('a header version lookup that throws falls back to (unknown)',
      () async {
    final l = ClientErrorLog(
      supportDir: () async => dir,
      appVersion: () async => throw StateError('no channel'),
    );
    await l.record('a', 'b');
    expect(current().readAsStringSync(), contains('# app: (unknown)\n'));
  });
}
