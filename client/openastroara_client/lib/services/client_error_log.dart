import 'dart:async';
import 'dart:collection';
import 'dart:convert';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:package_info_plus/package_info_plus.dart';
import 'package:path_provider/path_provider.dart';

import '../app_version.dart';

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
  });

  /// Entries in the current log file, earlier runs included.
  final int entries;

  /// Entries recorded since this process started.
  final int sessionEntries;
  final String? lastKind;
  final DateTime? lastAt;

  /// First line of the most recent entry's error text.
  final String? lastMessage;

  /// False once a write has failed — the log directory is unwritable, say.
  final bool available;

  ClientErrorLogStatus copyWith({
    int? entries,
    int? sessionEntries,
    String? lastKind,
    DateTime? lastAt,
    String? lastMessage,
    bool? available,
  }) => ClientErrorLogStatus(
    entries: entries ?? this.entries,
    sessionEntries: sessionEntries ?? this.sessionEntries,
    lastKind: lastKind ?? this.lastKind,
    lastAt: lastAt ?? this.lastAt,
    lastMessage: lastMessage ?? this.lastMessage,
    available: available ?? this.available,
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

  final ListQueue<String> _ring = ListQueue<String>();
  Future<void> _chain = Future<void>.value();
  String? _headerCache;

  final ValueNotifier<ClientErrorLogStatus> _status = ValueNotifier(
    const ClientErrorLogStatus(),
  );

  /// Live counters for UI; see [ClientErrorLogStatus].
  ValueListenable<ClientErrorLogStatus> get status => _status;

  /// Completes when every queued write has finished. Tests await this; the
  /// bug-report card awaits it before copying the file.
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
    _ring.addLast(line);
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
    // Snapshot the ring NOW, not when the write runs — later prints (the
    // framework's own dump of this very error, for one) belong to the next
    // entry, and the file should show what led up to this one.
    final prints = List<String>.from(_ring);
    final text = _formatEntry(
      at: at,
      kind: kind,
      error: error,
      stack: stack,
      context: context,
      library: library,
      prints: prints,
    );
    final firstLine = _firstLine(_describe(error));
    return _enqueue(() async {
      await _append(text);
      // Informational notes are in the file but are not "errors recorded".
      if (kind == noteKind) return;
      _status.value = _status.value.copyWith(
        entries: _status.value.entries + 1,
        sessionEntries: _status.value.sessionEntries + 1,
        lastKind: kind,
        lastAt: at,
        lastMessage: firstLine,
      );
    });
  }

  /// Kind tag for [note] entries; excluded from the [status] counters.
  static const noteKind = 'note';

  /// A plain informational entry — launch, VM service URL, and the like.
  Future<void> note(String message) => record(noteKind, message);

  /// Writes the whole log — the rotated file first, then the current one —
  /// to [path]. Returns the number of bytes written. A log that has never
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
          for (final f in [rotated, current]) {
            if (!await f.exists()) continue;
            any = true;
            await sink.addStream(f.openRead());
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
    final s = _status.value;
    if (!s.available) return 'client log: unavailable (write failed)';
    if (s.entries == 0) return 'client log: no errors recorded';
    final noun = s.entries == 1 ? 'entry' : 'entries';
    final buf = StringBuffer('client log: ${s.entries} $noun');
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

  Future<void> _scanExisting() async {
    final dir = await _supportDir();
    final f = File('${dir.path}/$fileName');
    if (!await f.exists()) return;
    var count = 0;
    String? lastKind;
    DateTime? lastAt;
    String? lastMessage;
    var pendingMessage = false;
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

  Future<void> _append(String text) async {
    final dir = await _supportDir();
    await dir.create(recursive: true);
    final current = File('${dir.path}/$fileName');
    final exists = await current.exists();
    final size = exists ? await current.length() : 0;
    var startFresh = !exists;
    if (exists && size + text.length > maxBytes) {
      final rotated = File('${dir.path}/$rotatedFileName');
      if (await rotated.exists()) await rotated.delete();
      await current.rename(rotated.path);
      startFresh = true;
    }
    final sink = current.openWrite(mode: FileMode.append);
    try {
      if (startFresh) sink.write(await _header());
      sink.write(text);
    } finally {
      await sink.flush();
      await sink.close();
    }
  }

  Future<String> _header() async {
    if (_headerCache != null) return _headerCache!;
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
      ..writeln('# mode: $mode')
      ..writeln('# started: ${_now().toIso8601String()}')
      ..writeln();
    return _headerCache = buf.toString();
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
    var text = buf.toString();
    if (text.length > maxEntryBytes) {
      text = '${text.substring(0, maxEntryBytes)}\n  … (entry truncated)\n\n';
    }
    return text;
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
