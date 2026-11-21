import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/client_error_log.dart';

class _CountingError {
  int calls = 0;
  @override
  String toString() {
    calls++;
    return 'counting error';
  }
}

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

  test('exportTo applies the §54.6 blacklist, the file on disk keeps it',
      () async {
    final l = log();
    await l.record('dio', 'headers {X-Ara-Token: abcdefghij12345} host pi.local');
    await l.record(
      'ssh',
      '-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----',
    );
    final out = '${dir.path}/export.log';
    await l.exportTo(out);
    final text = File(out).readAsStringSync();
    expect(text, contains('X-Ara-Token: [REDACTED-TOKEN]} host pi.local'));
    expect(text, contains('[REDACTED-KEY]'));
    expect(text, isNot(contains('abcdefghij12345')));
    expect(text, isNot(contains('AAAA')));
    expect(current().readAsStringSync(), contains('abcdefghij12345'),
        reason: 'the local log keeps full info (§54.1); only the export is cut');
  });

  test('exportTo with nothing recorded still writes the header', () async {
    final l = log();
    final out = '${dir.path}/empty.log';
    await l.exportTo(out);
    expect(File(out).readAsStringSync(), startsWith('# OpenAstro Ara'));
  });

  test('concurrent records land whole and in order', () async {
    // Distinct errors above the default burst budget: lift it, this test is
    // about write ordering.
    final l = ClientErrorLog(
      supportDir: () async => dir,
      appVersion: () async => 'v',
      now: () => DateTime.utc(2026, 9, 27, 14, 11, tick++),
      burstLimit: 100,
    );
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

  test('a burst of distinct errors is capped per window and reported',
      () async {
    final l = ClientErrorLog(
      supportDir: () async => dir,
      appVersion: () async => 'v',
      now: () => DateTime.utc(2026, 9, 27, 14, 11, tick++),
      burstLimit: 3,
      burstWindow: const Duration(seconds: 60),
    );
    // Two widgets alternating defeat the consecutive-repeat check: A, B, A
    // are written; B is dropped; A is a repeat of the last WRITTEN entry
    // (the dropped B never became the streak owner); B is dropped again.
    for (var i = 0; i < 6; i++) {
      await l.record('flutter_error', i.isEven ? 'widget A' : 'widget B');
    }
    var text = current().readAsStringSync();
    expect(ClientErrorLog.entryMarker.allMatches(text).length, 3);
    expect(l.status.value.entries, 3);
    expect(l.status.value.suppressedRepeats, 3);

    // Next window: the repeat and drop counts are written before the new
    // entry.
    tick += 100;
    await l.record('flutter_error', 'later');
    text = current().readAsStringSync();
    final rep = text.indexOf('(previous entry repeated 1 more time');
    final drop = text.indexOf('(2 entries dropped: more than 3 in 60s)');
    expect(rep, greaterThan(0));
    expect(drop, greaterThan(rep));
    expect(drop, lessThan(text.indexOf(' flutter_error ===', drop)));
    expect(ClientErrorLog.entryMarker.allMatches(text).length, 4);
  });

  test('a dropped entry keeps the previous entry\'s repeat streak intact',
      () async {
    final l = ClientErrorLog(
      supportDir: () async => dir,
      appVersion: () async => 'v',
      now: () => DateTime.utc(2026, 9, 27, 14, 11, tick++),
      burstLimit: 1,
      burstWindow: const Duration(seconds: 60),
    );
    await l.record('e', 'A'); // written, budget now full
    await l.record('e', 'A'); // repeat of A (repeats: 1)
    await l.record('e', 'B'); // dropped — must not consume A's streak
    await l.record('e', 'A'); // still a repeat of A (repeats: 2)
    tick += 100;
    await l.record('e', 'C'); // new window: both notes, then C
    final text = current().readAsStringSync();
    final rep = text.indexOf('(previous entry repeated 2 more times');
    final drop = text.indexOf('(1 entry dropped: more than 1 in 60s)');
    final c = text.indexOf(' e ===', text.indexOf(' e ===') + 1);
    expect(rep, greaterThan(0));
    expect(drop, greaterThan(rep));
    expect(c, greaterThan(drop));
    expect(ClientErrorLog.entryMarker.allMatches(text).length, 2);
  });

  test('a dropped entry that re-fires is counted as drops, not repeats',
      () async {
    final l = ClientErrorLog(
      supportDir: () async => dir,
      appVersion: () async => 'v',
      now: () => DateTime.utc(2026, 9, 27, 14, 11, tick++),
      burstLimit: 1,
      burstWindow: const Duration(seconds: 60),
    );
    await l.record('e', 'H'); // written
    for (var i = 0; i < 5; i++) {
      await l.record('e', 'I'); // all dropped
    }
    tick += 100;
    await l.record('e', 'J');
    final text = current().readAsStringSync();
    expect(text, contains('(5 entries dropped: more than 1 in 60s)'));
    expect(text, isNot(contains('repeated')),
        reason: 'H fired once; I was never written, so nothing repeated');
    expect(l.status.value.suppressedRepeats, 5);
  });

  test('notes are exempt from the burst budget', () async {
    final l = ClientErrorLog(
      supportDir: () async => dir,
      appVersion: () async => 'v',
      now: () => DateTime.utc(2026, 9, 27, 14, 11, tick++),
      burstLimit: 1,
      burstWindow: const Duration(seconds: 60),
    );
    await l.record('a', 'x');
    await l.note('launch');
    await l.note('second');
    expect(ClientErrorLog.entryMarker.allMatches(current().readAsStringSync())
        .length, 3);
  });

  test('the entry is formatted on the chain, not inside record()', () async {
    final l = log();
    final probe = _CountingError();
    final pending = l.record('a', probe);
    // toString() ran once, for the repeat key; the full entry (a second
    // toString) is built when the write runs.
    expect(probe.calls, 1);
    await pending;
    expect(probe.calls, 2);
  });

  test('an oversized debugPrint line is kept truncated in the ring', () {
    final l = ClientErrorLog(
      supportDir: () async => dir,
      appVersion: () async => 'v',
      maxPrintLineChars: 10,
    );
    l.notePrint('0123456789abcdef');
    expect(l.recentPrints.single, '0123456789 … (line truncated)');
    // A cut that would land between the halves of a surrogate pair backs
    // off one unit instead of leaving a lone high surrogate.
    l.notePrint('012345678😀abcdef');
    expect(l.recentPrints.last, '012345678 … (line truncated)');
    l.notePrint('short');
    expect(l.recentPrints.last, 'short');
  });

  test('zero-or-negative knobs are inert, never throwing', () async {
    final l = ClientErrorLog(
      supportDir: () async => dir,
      appVersion: () async => 'v',
      now: () => DateTime.utc(2026, 9, 27, 14, 11, tick++),
      maxPrintLineChars: 0,
      burstLimit: 0,
    );
    l.notePrint('anything');
    expect(l.recentPrints.single, ' … (line truncated)');
    for (var i = 0; i < 20; i++) {
      await l.record('e', 'distinct $i');
    }
    expect(l.status.value.entries, 20, reason: 'no budget means no drops');
    expect(l.status.value.suppressedRepeats, 0);
  });

  test('a rotation caused by a note republishes the entry count', () async {
    final l = log(maxBytes: 600);
    final big = 'x' * 300;
    await l.record('a', big);
    await l.record('b', big); // rotation 1: a → .1
    expect(l.status.value.entries, 2);
    await l.note('n' * 300); // rotation 2 by a NOTE: a is deleted
    expect(l.status.value.entries, 1, reason: 'only b is on disk now');
    final second = log(maxBytes: 600);
    await second.idle;
    expect(second.status.value.entries, 1);
  });

  test('entries tracks the two files even after a second rotation',
      () async {
    final l = log(maxBytes: 600);
    final big = 'x' * 300;
    await l.record('a', big);
    await l.record('b', big);
    await l.record('c', big); // deletes the file holding 'a'
    expect(l.status.value.entries, 2, reason: 'a is gone from disk');
    expect(l.status.value.sessionEntries, 3);
    final second = log(maxBytes: 600);
    await second.idle;
    expect(second.status.value.entries, 2);
  });

  test('summary reads well empty and with entries', () async {
    final l = log();
    await l.idle;
    expect(l.summary(), 'client log: no errors recorded');
    await l.record('flutter_error', 'boom');
    expect(l.summary(), startsWith('client log: 1 entry, last '));
    expect(l.summary(), endsWith(' flutter_error: boom'));
    await l.record('flutter_error', 'boom');
    expect(l.summary(), startsWith('client log: 1 entry (+1 suppressed), last '));
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
