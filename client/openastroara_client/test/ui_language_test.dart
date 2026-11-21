import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

/// The app speaks to astrophotographers, not to its own authors. This test is
/// the gate that keeps it that way: 76 playbook section marks and 41 uses of
/// "daemon" had accumulated in visible copy before it existed, because nothing
/// mechanical was stopping them.
///
/// Domain vocabulary the user actually uses — HFR, ADU, dither, meridian flip,
/// plate solve, RMS, Bortle — is welcome and deliberately not listed here.
/// What is banned is OUR vocabulary: spec references, architecture nouns, and
/// wire-protocol words.
void main() {
  final lib = Directory('lib');

  /// Banned inside a user-visible string literal, with the reason shown on failure.
  const banned = <String, String>{
    '§': 'a playbook section mark — the user has never seen the playbook',
    'daemon': 'say "your rig", "the server", or just "Ara"',
    'endpoint': 'say where the thing is, not what we call the route',
    'DTO': 'an internal data-shape name',
    'provider': 'a Riverpod term, not a user-facing one',
    'mediator': 'an internal architecture term',
    'sub-PR': 'our development process',
    'Phase 12h': 'our development process',
    'WS notification': 'say "notification"',
  };

  /// Strings that are not shown to a person: asset keys, URLs, route names,
  /// registry ids, log/debug text, and code identifiers.
  bool isMachineString(String line, String value, [String preceding = '']) {
    final l = line.trimLeft();
    // Identifier and wire fields: ids, storage keys, search keywords, and
    // fully-qualified type names sent to the server are not prose.
    for (final field in const [
      'id:', 'profilePath:', 'settingKey:', 'keywords:', 'key:', 'type:', 'name:',
    ]) {
      if (l.startsWith(field)) {
        return true;
      }
    }
    final context = "$preceding\n$line";
    if (context.contains('throw ') || context.contains('Exception(') ||
        context.contains('StateError(') || context.contains('FormatException') ||
        context.contains('assert(')) {
      return true;
    }
    // A single token with no whitespace is never prose (JSON keys, enum
    // names, and fragments of interpolated expressions this naive scanner
    // splits on quotes).
    if (!value.trim().contains(' ')) {
      return true;
    }
    // A dotted machine identifier (wire enum, .NET assembly-qualified type).
    if ((!value.contains(' ') && value.contains('.')) ||
        RegExp(r'^[A-Z][A-Za-z0-9]*(\.[A-Z][A-Za-z0-9]*)+').hasMatch(value)) {
      return true;
    }
    return l.startsWith('//') ||
        l.startsWith('///') ||
        l.startsWith('import ') ||
        l.startsWith('export ') ||
        line.contains('debugPrint') ||
        line.contains('developer.log') ||
        line.contains('assert(') ||
        value.startsWith('http') ||
        value.startsWith('/api/') ||
        value.startsWith('assets/') ||
        value.startsWith('package:');
  }

  test('no developer jargon in user-visible strings', () {
    final offenders = <String>[];
    final stringLiteral = RegExp(r"'((?:[^'\\\n]|\\.)*)'");

    for (final entity in lib.listSync(recursive: true)) {
      if (entity is! File || !entity.path.endsWith('.dart')) {
        continue;
      }
      final lines = entity.readAsLinesSync();
      for (var i = 0; i < lines.length; i++) {
        final line = lines[i];
        for (final match in stringLiteral.allMatches(line)) {
          final value = match.group(1) ?? '';
          if (value.isEmpty ||
              isMachineString(line, value,
                  lines.sublist(i > 2 ? i - 3 : 0, i).join('\n'))) {
            continue;
          }
          // Only the PROSE is user-visible: interpolated expressions are code
          // ("${'$'}{state.daemonGitSha}" reads as "daemon" but never reaches a screen).
          final prose = value
              .replaceAll(RegExp(r'\$\{[^}]*\}'), '')
              .replaceAll(RegExp(r'\$[A-Za-z_][A-Za-z0-9_]*'), '');
          for (final entry in banned.entries) {
            if (prose.toLowerCase().contains(entry.key.toLowerCase())) {
              offenders.add('${entity.path}:${i + 1}\n'
                  '    "${value.length > 90 ? '${value.substring(0, 90)}…' : value}"\n'
                  '    → ${entry.value}');
            }
          }
        }
      }
    }

    expect(
      offenders,
      isEmpty,
      reason: 'User-visible copy contains developer vocabulary:\n\n'
          '${offenders.join('\n\n')}\n\n'
          'Rewrite it the way an astrophotographer would say it. If the string '
          'is genuinely not shown to a person (a log line, an asset key), move '
          'it behind debugPrint/developer.log or a constant this test skips.',
    );
  });
}
