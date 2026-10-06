import 'dart:async';
import 'dart:collection';
import 'dart:convert';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:package_info_plus/package_info_plus.dart';
import 'package:path_provider/path_provider.dart';

import '../app_version.dart';
import 'log_redaction.dart';

/// What the client log knows about itself without touching the disk — the
/// Support card and the help dialog's "Copy diagnostics" read this, so neither
/// does file IO inside a build.
@immutable
class ClientErrorLogStatus {
  const ClientErrorLogStatus({
    this.entries = 0,
    this.sessionEntries = 0,
    this.lastKind,
    this.lastAt,
    this.lastMessage,
    this.available = true,
    this.suppressedRepeats = 0,
  });

  /// Error entries on disk — the current file plus the rotated one, earlier
  /// runs included. [ClientErrorLog.exportTo] ships those same two files (redacted);
  /// notes and headers are in them too but are not counted here.
  final int entries;

  /// Entries recorded since this process started.
  final int sessionEntries;
  final String? lastKind;
  final DateTime? lastAt;

  /// First line of the most recent entry's error text.
  final String? lastMessage;

  /// False while the last write failed — the log directory is unwritable,
  /// say. Cleared by the next write that succeeds.
  final bool available;

  /// Entries this session that were counted instead of written: identical
  /// to their predecessor within [ClientErrorLog.repeatWindow], or over the
  /// [ClientErrorLog.burstLimit] for the window.
  final int suppressedRepeats;

  ClientErrorLogStatus copyWith({
    int? entries,
    int? sessionEntries,
    String? lastKind,
    DateTime? lastAt,
    String? lastMessage,
    bool? available,
    int? suppressedRepeats,
  }) => ClientErrorLogStatus(
    entries: entries ?? this.entries,
    sessionEntries: sessionEntries ?? this.sessionEntries,
    lastKind: lastKind ?? this.lastKind,
    lastAt: lastAt ?? this.lastAt,
    lastMessage: lastMessage ?? this.lastMessage,
    available: available ?? this.available,
    suppressedRepeats: suppressedRepeats ?? this.suppressedRepeats,
  );
}

/// The client's own error log — `client-errors.log` in the app-support
/// directory (#1111).
///
/// The daemon logs everything it does, but until this existed the client
/// logged nothing at all: a Flutter assertion painted the window red and the
/// trace went to a stderr nobody was watching (a debug app launched via
/// `open` has none). Every entry the handlers in `client_error_handlers.dart`
/// record lands here, together with the last [ringCapacity] `debugPrint`
/// lines — the framework's own error dump and the app's `developer.log`-style
/// prints — so the file reads like the console would have.
///
/// Design constraints, all deliberate:
/// - **Never throws and never awaits the caller.** [record] returns a future
///   for tests; the error handlers fire and forget. A logging failure while
///   handling an error must not become a second error.
/// - **Serialized writes** through one future chain, same pattern as
///   `NightModePrefsService`, so concurrent records can't interleave.
/// - **Bounded.** The file is capped at [maxBytes]; when an entry would push
///   it over, the current file becomes `client-errors.log.1` (replacing any
///   earlier rotation) and a fresh file starts with a new header. Two files
///   at most, so a crash loop can't fill a dark-site laptop's disk.
/// - **Self-describing.** Each file opens with a header: app version, build
///   date, platform, debug/profile/release. "Which build was that?" is the
///   first question on every bug report.
class ClientErrorLog {
  /// [supportDir] overrides the app-support directory lookup (tests use a
  /// temp dir; production uses path_provider). [appVersion] resolves the
  /// header's version line (production reads `package_info_plus`; tests pass
  /// a constant). [now] injects the clock.
  ClientErrorLog({
    Future<Directory> Function()? supportDir,
    Future<String> Function()? appVersion,
    DateTime Function()? now,
    this.maxBytes = 512 * 1024,
    this.ringCapacity = 200,
    this.maxEntryBytes = 64 * 1024,
    this.repeatWindow = const Duration(seconds: 5),
    this.burstLimit = 8,
    this.burstWindow = const Duration(seconds: 10),
    this.maxPrintLineChars = 4096,
  }) : _supportDir = supportDir ?? getApplicationSupportDirectory,
       _appVersion = appVersion ?? _packageVersion,
       _now = now ?? DateTime.now {
    // Count what earlier runs left behind so the Support card can say "3
    // errors recorded" before anything new happens in this session.
    _enqueue(_scanExisting);
  }

  static const fileName = 'client-errors.log';
  static const rotatedFileName = 'client-errors.log.1';

  /// The line that starts every entry; [_scanExisting] and [exportTo] rely
  /// on it, and so does anyone grepping the file.
  static const entryMarker = '=== ';

  final Future<Directory> Function() _supportDir;
  final Future<String> Function() _appVersion;
  final DateTime Function() _now;
  final int maxBytes;
  final int ringCapacity;
  final int maxEntryBytes;

  /// An entry identical to the previous one (same kind, same first line)
  /// arriving within this window is counted, not written. The failure this
  /// log exists for — an assertion that re-fires on every rebuild of a
  /// stream-driven widget — would otherwise queue a 64 KiB entry per frame
  /// and rotate the file every few entries; Flutter's console dump throttles
  /// repeats for the same reason. The count is written out when the streak
  /// ends (a different entry, or an export).
  final Duration repeatWindow;

  String? _lastKey;
  DateTime? _lastAt;
  int _repeats = 0;

  /// At most [burstLimit] entries are written per [burstWindow]; the rest
  /// are counted and reported as one line when the window ends. The
  /// consecutive-repeat check above catches one widget failing every
  /// frame; this catches two of them alternating, which defeats it.
  final int burstLimit;
  final Duration burstWindow;
  DateTime? _burstStart;
  int _burstCount = 0;
  int _dropped = 0;

  /// Longest `debugPrint` line kept in the ring. Flutter's console dump
  /// hands one call a whole formatted error plus stack; 200 of those can be
  /// megabytes resident, and the head is what matters.
  final int maxPrintLineChars;

  // Non-note entries per file, so [ClientErrorLogStatus.entries] tracks what
  // exportTo ships even after the second rotation deletes the older file.
  int _rotatedEntries = 0;
  int _currentEntries = 0;

  final ListQueue<String> _ring = ListQueue<String>();
  Future<void> _chain = Future<void>.value();
  String? _headerCache;

  final ValueNotifier<ClientErrorLogStatus> _status = ValueNotifier(
    const ClientErrorLogStatus(),
  );

  /// Live counters for UI; see [ClientErrorLogStatus].
  ValueListenable<ClientErrorLogStatus> get status => _status;

  /// Completes when every queued write has finished. Tests await this;
  /// [exportTo] queues itself behind the same chain, so callers copying the
  /// file need not.
  Future<void> get idle => _chain;

  /// The `debugPrint` lines held for the next entry, oldest first.
  List<String> get recentPrints => List.unmodifiable(_ring);

  static Future<String> _packageVersion() async =>
      formatFullVersion(await PackageInfo.fromPlatform());

  /// Remembers one `debugPrint` line for the ring buffer. Synchronous and
  /// allocation-light: it runs on every print the app makes.
  void notePrint(String line) {
    if (ringCapacity <= 0) return;
    while (_ring.length >= ringCapacity) {
      _ring.removeFirst();
    }
    if (line.length <= maxPrintLineChars) {
      _ring.addLast(line);
      return;
    }
    var cut = maxPrintLineChars.clamp(0, line.length);
    // Never end on a high surrogate.
    if (cut > 0) {
      final last = line.codeUnitAt(cut - 1);
      if (last >= 0xD800 && last <= 0xDBFF) cut--;
    }
    _ring.addLast('${line.substring(0, cut)} … (line truncated)');
  }

  /// Appends one entry. [kind] is a short tag (`flutter_error`,
  /// `uncaught`, `note`, …); [context] is the framework's "while building
  /// …" phrase or any caller-supplied situation line.
  Future<void> record(
    String kind,
    Object error, {
    StackTrace? stack,
    String? context,
    String? library,
  }) {
    final at = _now();
    final firstLine = _firstLine(_describe(error));
    final key = '$kind\n$firstLine';
    final last = _lastAt;
    if (key == _lastKey && last != null && at.difference(last) < repeatWindow) {
      _repeats++;
      _lastAt = at;
      // Deferred like every other status write: record() runs synchronously
      // inside FlutterError.onError, which a build-phase error reaches from
      // inside the build itself, and a listener's setState there is
      // "setState() called during build". Nothing in this method may touch
      // the notifier before returning.
      return _enqueue(() async {
        _status.value = _status.value.copyWith(
          suppressedRepeats: _status.value.suppressedRepeats + 1,
        );
      });
    }
    // Burst budget (notes are exempt: they are rare and never re-fire).
    // Decided BEFORE the repeat streak is consumed or the key advances: a
    // dropped entry must neither discard the previous entry's repeat count
    // nor become the owner of repeats that follow it (its own re-fires are
    // fresh records against the still-previous key, so they are dropped and
    // counted as drops, not written up as someone else's repeats).
    // The window is fixed, not sliding: it bounds file churn under the 512
    // KiB cap, it is not a rate limit.
    var droppedNote = '';
    if (kind != noteKind && burstLimit > 0) {
      final start = _burstStart;
      if (start == null || at.difference(start) >= burstWindow) {
        droppedNote = _takeDroppedNote();
        _burstStart = at;
        _burstCount = 0;
      }
      if (_burstCount >= burstLimit) {
        _dropped++;
        return _enqueue(() async {
          _status.value = _status.value.copyWith(
            suppressedRepeats: _status.value.suppressedRepeats + 1,
          );
        });
      }
      _burstCount++;
    }
    final repeatNote = _takeRepeatNote();
    _lastKey = key;
    _lastAt = at;
    // Snapshot the ring NOW, not when the write runs — later prints (the
    // framework's own dump of this very error, for one) belong to the next
    // entry, and the file should show what led up to this one. Only the
    // snapshot is taken here: formatting a 64 KiB entry belongs on the
    // chain, not inside FlutterError.onError mid-frame.
    final prints = List<String>.from(_ring);
    return _enqueue(() async {
      final text = _formatEntry(
        at: at,
        kind: kind,
        error: error,
        stack: stack,
        context: context,
        library: library,
        prints: prints,
      );
      await _append(repeatNote + droppedNote + text, countsAsEntry: kind != noteKind);
      // A successful write clears an earlier failure: the disk came back.
      if (!_status.value.available) {
        _status.value = _status.value.copyWith(available: true);
      }
      // Informational notes are in the file but are not "errors recorded".
      if (kind == noteKind) return;
      _status.value = _status.value.copyWith(
        entries: _rotatedEntries + _currentEntries,
        sessionEntries: _status.value.sessionEntries + 1,
        lastKind: kind,
        lastAt: at,
        lastMessage: firstLine,
      );
    });
  }

  /// The window-end line for entries the burst budget dropped, or ''.
  String _takeDroppedNote() {
    if (_dropped == 0) return '';
    final n = _dropped;
    _dropped = 0;
    return '($n ${n == 1 ? 'entry' : 'entries'} dropped: more than '
        '$burstLimit in ${burstWindow.inSeconds}s)\n\n';
  }

  /// Kind tag for [note] entries; excluded from the [status] counters.
  static const noteKind = 'note';

  /// A plain informational entry — launch, VM service URL, and the like.
  Future<void> note(String message) => record(noteKind, message);

  /// Writes the whole log — the rotated file first, then the current one —
  /// to [path], with the §54.6 credential blacklist applied line by line
  /// ([LogRedaction]): the file is written by this app, beside the daemon's
  /// bundle, so the daemon's own stripping never sees it (#1144). Returns the number of bytes written. A log that has never
  /// been written to still exports its header, so the file is never empty
  /// and a "no errors" export means exactly that.
  ///
  /// Unlike [record], this THROWS on failure: the caller chose [path] and
  /// wants to know the file is not there. A failed export says nothing
  /// about the log itself, so [status] is left alone.
  Future<int> exportTo(String path) async {
    var written = 0;
    Object? failure;
    StackTrace? failureStack;
    // A streak of suppressed repeats is written out first, so the export
    // says how often the last error fired.
    final pending = _takeRepeatNote() + _takeDroppedNote();
    if (pending.isNotEmpty) {
      _enqueue(() => _append(pending));
    }
    // Queued behind pending writes so the copy sees whole entries.
    await _enqueue(() async {
      try {
        final dir = await _supportDir();
        final current = File('${dir.path}/$fileName');
        final rotated = File('${dir.path}/$rotatedFileName');
        // Create first so a bad path fails here, as a plain exception —
        // an IOSink that cannot open reports it through `done`, which
        // surfaces as an unhandled async error if close() is never reached.
        final out = await File(path).create();
        final sink = out.openWrite();
        try {
          var any = false;
          // One redactor for both files (§54.6); rotation happens at entry
          // boundaries, so a key block never straddles it in practice.
          final redactor = LineRedactor();
          for (final f in [rotated, current]) {
            if (!await f.exists()) continue;
            any = true;
            await for (final line in f
                .openRead()
                .transform(const Utf8Decoder(allowMalformed: true))
                .transform(const LineSplitter())) {
              final kept = redactor.push(line);
              if (kept != null) sink.writeln(kept);
            }
          }
          if (!any) sink.write(await _header());
        } finally {
          // close() flushes; a write error is rethrown from here, once.
          await sink.close();
        }
        written = await out.length();
      } catch (e, st) {
        failure = e;
        failureStack = st;
      }
    });
    if (failure != null) Error.throwWithStackTrace(failure!, failureStack!);
    return written;
  }

  /// One line for "Copy diagnostics" and the Support card, e.g.
  /// `client log: 3 errors, last 2026-09-27 14:11 flutter_error: …`.
  String summary() {
    final s = status.value;
    if (!s.available) return 'client log: unavailable (write failed)';
    if (s.entries == 0) return 'client log: no errors recorded';
    final noun = s.entries == 1 ? 'entry' : 'entries';
    final buf = StringBuffer('client log: ${s.entries} $noun');
    if (s.suppressedRepeats > 0) {
      buf.write(' (+${s.suppressedRepeats} suppressed)');
    }
    if (s.lastAt != null) {
      buf.write(', last ${_stamp(s.lastAt!)} ${s.lastKind ?? ''}'.trimRight());
      if (s.lastMessage != null && s.lastMessage!.isNotEmpty) {
        buf.write(': ${s.lastMessage}');
      }
    }
    return buf.toString();
  }

  /// Runs [task] after every earlier task, swallowing whatever it throws.
  Future<void> _enqueue(Future<void> Function() task) {
    final next = _chain.then((_) async {
      try {
        await task();
      } catch (_) {
        _status.value = _status.value.copyWith(available: false);
      }
    });
    _chain = next;
    return next;
  }

  /// The streak-end line for suppressed repeats, or '' when there were none.
  String _takeRepeatNote() {
    if (_repeats == 0) return '';
    final n = _repeats;
    _repeats = 0;
    return '(previous entry repeated $n more '
        '${n == 1 ? 'time' : 'times'} within ${repeatWindow.inSeconds}s '
        'of each other)\n\n';
  }

  /// Counts the entries earlier runs left behind — the rotated file too,
  /// since [exportTo] ships both and the card's count should match what a
  /// bug report will contain.
  Future<void> _scanExisting() async {
    final dir = await _supportDir();
    var count = 0;
    String? lastKind;
    DateTime? lastAt;
    String? lastMessage;
    var pendingMessage = false;
    for (final name in [rotatedFileName, fileName]) {
      final f = File('${dir.path}/$name');
      if (!await f.exists()) continue;
      final before = count;
      await for (final line in f
          .openRead()
          .transform(utf8.decoder)
          .transform(const LineSplitter())) {
      if (line.startsWith(entryMarker)) {
        count++;
        // "=== <iso> <kind> ==="
        final parts = line
            .substring(entryMarker.length)
            .replaceFirst(RegExp(r' ===$'), '')
            .split(' ');
        final kind = parts.length > 1 ? parts[1] : null;
        if (kind == noteKind) {
          count--;
          pendingMessage = false;
          continue;
        }
        lastAt = parts.isNotEmpty ? DateTime.tryParse(parts.first) : null;
        lastKind = kind;
        lastMessage = null;
        pendingMessage = true;
      } else if (pendingMessage && line.startsWith('error: ')) {
        lastMessage = line.substring('error: '.length);
        pendingMessage = false;
      }
      }
      if (name == rotatedFileName) {
        _rotatedEntries = count - before;
      } else {
        _currentEntries = count - before;
      }
    }
    if (count == 0) return;
    // The scan is the first task on the chain, so nothing from this session
    // has been recorded yet and the file's last entry is the newest overall.
    _status.value = _status.value.copyWith(
      entries: _status.value.entries + count,
      lastKind: lastKind,
      lastAt: lastAt,
      lastMessage: lastMessage,
    );
  }

  Future<void> _append(String text, {bool countsAsEntry = false}) async {
    final dir = await _supportDir();
    await dir.create(recursive: true);
    final current = File('${dir.path}/$fileName');
    final exists = await current.exists();
    final size = exists ? await current.length() : 0;
    var startFresh = !exists;
    if (exists && size + utf8.encode(text).length > maxBytes) {
      final rotated = File('${dir.path}/$rotatedFileName');
      if (await rotated.exists()) await rotated.delete();
      await current.rename(rotated.path);
      startFresh = true;
      _rotatedEntries = _currentEntries;
      _currentEntries = 0;
      // A note or an export flush can rotate too; keep the published count
      // equal to what is on disk without waiting for the next entry.
      if (!countsAsEntry && _status.value.entries != _rotatedEntries) {
        _status.value = _status.value.copyWith(entries: _rotatedEntries);
      }
    }
    final sink = current.openWrite(mode: FileMode.append);
    try {
      if (startFresh) sink.write(await _header());
      sink.write(text);
    } finally {
      // close() flushes; a single await so a failed flush cannot leave the
      // handle open.
      await sink.close();
    }
    if (countsAsEntry) _currentEntries++;
  }

  Future<String> _header() async {
    // The static lines are built once; the started stamp is per file, so a
    // post-rotation file says when IT began, not when the process did.
    final static_ = _headerCache ??= await _staticHeader();
    return '$static_# started: ${_now().toIso8601String()}\n\n';
  }

  Future<String> _staticHeader() async {
    String version;
    try {
      version = await _appVersion();
    } catch (_) {
      version = '(unknown)';
    }
    final mode = kReleaseMode
        ? 'release'
        : kProfileMode
        ? 'profile'
        : 'debug';
    final buf = StringBuffer()
      ..writeln('# OpenAstro Ara client error log')
      ..writeln('# app: $version')
      ..writeln('# build date: $buildDateLabel')
      ..writeln(
        '# platform: ${Platform.operatingSystem} '
        '${Platform.operatingSystemVersion}',
      )
      ..writeln('# mode: $mode');
    return buf.toString();
  }

  String _formatEntry({
    required DateTime at,
    required String kind,
    required Object error,
    required StackTrace? stack,
    required String? context,
    required String? library,
    required List<String> prints,
  }) {
    final buf = StringBuffer()
      ..writeln('$entryMarker${at.toIso8601String()} $kind ===');
    if (library != null && library.isNotEmpty) buf.writeln('library: $library');
    if (context != null && context.isNotEmpty) buf.writeln('context: $context');
    buf.writeln('${kind == noteKind ? 'message' : 'error'}: ${_describe(error)}');
    if (stack != null) {
      final trace = stack.toString().trimRight();
      if (trace.isNotEmpty) {
        buf.writeln('stack:');
        for (final line in trace.split('\n')) {
          buf.writeln('  $line');
        }
      }
    }
    if (prints.isNotEmpty) {
      buf.writeln('recent debugPrint (${prints.length} lines):');
      for (final line in prints) {
        buf.writeln('  $line');
      }
    }
    buf.writeln();
    return _truncateToBytes(buf.toString(), maxEntryBytes);
  }

  /// Caps [text] at [maxBytes] of UTF-8 (the budgets are byte budgets, and
  /// `String.length` counts UTF-16 units) without splitting a surrogate
  /// pair, then marks the cut.
  @visibleForTesting
  static String truncateToBytes(String text, int maxBytes) =>
      _truncateToBytes(text, maxBytes);

  static String _truncateToBytes(String text, int maxBytes) {
    if (utf8.encode(text).length <= maxBytes) return text;
    // UTF-8 never needs fewer bytes than UTF-16 units, so maxBytes units is
    // an upper bound; shrink until the encoding fits.
    var cut = maxBytes.clamp(0, text.length);
    while (cut > 0 && utf8.encode(text.substring(0, cut)).length > maxBytes) {
      cut = (cut * 9) ~/ 10;
    }
    // Never end on a high surrogate.
    if (cut > 0) {
      final last = text.codeUnitAt(cut - 1);
      if (last >= 0xD800 && last <= 0xDBFF) cut--;
    }
    return '${text.substring(0, cut)}\n  … (entry truncated)\n\n';
  }

  static String _describe(Object error) {
    try {
      return error.toString();
    } catch (_) {
      return '<${error.runtimeType}: toString threw>';
    }
  }

  static String _firstLine(String s) {
    final i = s.indexOf('\n');
    return (i < 0 ? s : s.substring(0, i)).trim();
  }

  static String _stamp(DateTime t) {
    final l = t.toLocal();
    String two(int n) => n.toString().padLeft(2, '0');
    return '${l.year}-${two(l.month)}-${two(l.day)} '
        '${two(l.hour)}:${two(l.minute)}';
  }
}

/// The process-wide log. `main.dart` constructs the real one before
/// `runApp` and overrides this so the error handlers and the widget tree
/// share it; the default exists so widget tests that never touch the log
/// still build.
final clientErrorLogProvider = Provider<ClientErrorLog>(
  (ref) => ClientErrorLog(),
);
