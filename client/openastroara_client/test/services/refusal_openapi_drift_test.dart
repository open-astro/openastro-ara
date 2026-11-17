import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/refusal.dart';

/// ADR 0022 rule 4 — the app's list of Refusal causes equals the one the
/// daemon publishes in the committed `openapi.yaml` (schema `RefusalReason`).
/// A cause on one side only fails here.
Set<String> _publishedCauses(String yaml) {
  final lines = yaml.split('\n');
  final start = lines.indexOf('    RefusalReason:');
  if (start < 0) return const {};
  final causes = <String>{};
  var inEnum = false;
  for (final line in lines.skip(start + 1)) {
    if (line.trim().isEmpty) continue;
    final indent = line.length - line.trimLeft().length;
    if (indent <= 4) break; // the next schema
    if (line.trim() == 'enum:') {
      inEnum = true;
    } else if (inEnum && line.trimLeft().startsWith('- ')) {
      causes.add(line.trim().substring(2).trim());
    } else if (indent <= 6) {
      inEnum = false;
    }
  }
  return causes;
}

void main() {
  test('the cause enum matches the causes openapi.yaml publishes', () {
    final yaml = File('../../OpenAstroAra.Server/openapi.yaml')
        .readAsStringSync();
    final published = _publishedCauses(yaml);
    expect(
      published,
      isNotEmpty,
      reason: 'openapi.yaml publishes no RefusalReason schema',
    );
    expect(RefusalCause.values.map((c) => c.token).toSet(), published);
  });
}
