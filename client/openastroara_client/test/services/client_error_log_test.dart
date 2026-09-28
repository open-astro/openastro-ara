import 'dart:convert';
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

  test('a post-rotation file carries its own started stamp', () async {
    final l = log(maxBytes: 600);
    final big = 'x' * 300;
    await l.record('a', big);
    await l.record('b', big);
    final oldStamp = RegExp(r'# started: (\S+)')
        .firstMatch(rotated().readAsStringSync())!
        .group(1);
    final newStamp = RegExp(r'# started: (\S+)')
        .firstMatch(current().readAsStringSync())!
        .group(1);
    expect(newStamp, isNot(oldStamp));
    expect(current().readAsStringSync(), contains('# app: 0.0.1a+37 (test)'));
  });

  test('entry and file caps are byte budgets and never split a surrogate',
      () {
    // 'x' + emoji (2 UTF-16 units, 4 UTF-8 bytes each): a 4-byte budget fits
    // only the 'x' — a code-unit cut at 4 would have kept 'x' plus one whole
    // emoji (5 bytes), and a cut at 2 would have split the first pair.
    const s = 'x😀😀😀';
    final t = ClientErrorLog.truncateToBytes(s, 4);
    expect(t, startsWith('x\n  … (entry truncated)'));
    expect(ClientErrorLog.truncateToBytes(s, 5), startsWith('x😀\n'));
    expect(utf8.encode(ClientErrorLog.truncateToBytes(s, 9).split('\n').first)
        .length, lessThanOrEqualTo(9));
    expect(ClientErrorLog.truncateToBytes('plain', 100), 'plain');
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

  test('identical errors inside the repeat window are counted, not written',
      () async {
    final l = log();
    for (var i = 0; i < 4; i++) {
      await l.record('flutter_error', 'same assertion\nframe $i');
    }
    var text = current().readAsStringSync();
    expect(ClientErrorLog.entryMarker.allMatches(text).length, 1);
    expect(l.status.value.entries, 1);
    expect(l.status.value.suppressedRepeats, 3);

    // A different error ends the streak and writes the count out first.
    await l.record('flutter_error', 'other');
    text = current().readAsStringSync();
    final note = text.indexOf('(previous entry repeated 3 more times within 5s');
    expect(note, greaterThan(0));
    expect(note, lessThan(text.indexOf(' flutter_error ===', note)));
    expect(ClientErrorLog.entryMarker.allMatches(text).length, 2);
  });

  test('a suppressed repeat never touches status synchronously', () async {
    // record() runs inside FlutterError.onError, possibly mid-build; a
    // synchronous notifier write there is "setState() called during build"
    // for any listening widget. Both paths must defer.
    final l = log();
    await l.record('a', 'x');
    final before = l.status.value;
    final pending = l.record('a', 'x');
    expect(identical(l.status.value, before), isTrue,
        reason: 'status changed before the call returned');
    await pending;
    expect(l.status.value.suppressedRepeats, 1);
  });

  test('a streak of repeats is flushed by an export', () async {
    final l = log();
    await l.record('a', 'x');
    await l.record('a', 'x');
    final out = '${dir.path}/export.log';
    await l.exportTo(out);
    expect(File(out).readAsStringSync(),
        contains('(previous entry repeated 1 more time within 5s'));
  });

  test('the same error outside the window is written again', () async {
    final l = ClientErrorLog(
      supportDir: () async => dir,
      appVersion: () async => 'v',
      now: () => DateTime.utc(2026, 9, 27, 14, 11, tick++),
      repeatWindow: Duration.zero,
    );
    await l.record('a', 'x');
    await l.record('a', 'x');
    expect(ClientErrorLog.entryMarker.allMatches(current().readAsStringSync())
        .length, 2);
    expect(l.status.value.suppressedRepeats, 0);
  });

  test('a write failure is cleared by the next successful write', () async {
    var fail = true;
    final l = ClientErrorLog(
      supportDir: () async {
        if (fail) throw const FileSystemException('offline disk');
        return dir;
      },
      appVersion: () async => 'v',
    );
    await l.record('a', 'x');
    expect(l.status.value.available, isFalse);
    fail = false;
    await l.record('b', 'y');
    expect(l.status.value.available, isTrue);
    expect(l.status.value.entries, 1);
  });

  test('a new instance counts the rotated file too', () async {
    final first = log(maxBytes: 600);
    final big = 'x' * 300;
    await first.record('a', big);
    await first.record('b', big);
    expect(rotated().existsSync(), isTrue);

    final second = log(maxBytes: 600);
    await second.idle;
    expect(second.status.value.entries, 2);
    expect(second.status.value.lastKind, 'b');
  });

  test('summary reads well empty and with entries', () async {
    final l = log();
    await l.idle;
    expect(l.summary(), 'client log: no errors recorded');
    await l.record('flutter_error', 'boom');
    expect(l.summary(), startsWith('client log: 1 entry, last '));
    expect(l.summary(), endsWith(' flutter_error: boom'));
    await l.record('flutter_error', 'boom');
    expect(l.summary(), startsWith('client log: 1 entry (+1 repeats), last '));
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
