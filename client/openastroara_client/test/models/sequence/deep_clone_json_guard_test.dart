import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/instruction_catalog.dart';

void main() {
  // #1185 / §38 — the String-keyed-defaults invariant used to be a debug-only
  // assert; a release build would have died on the `as String` cast instead.
  test('deepCloneJson clones String-keyed maps and lists deeply', () {
    final source = <String, dynamic>{
      'a': 1,
      'nested': <String, dynamic>{'b': <dynamic>[1, 2, <String, dynamic>{'c': 3}]},
    };
    final clone = deepCloneJson(source) as Map<String, dynamic>;
    expect(clone, equals(source));
    expect(identical(clone['nested'], source['nested']), isFalse, reason: 'nested maps are copied');
    ((clone['nested'] as Map)['b'] as List).add(4);
    expect(((source['nested'] as Map)['b'] as List).length, 3, reason: 'the source is untouched');
  });

  test('deepCloneJson throws ArgumentError on a non-String-keyed map in every build mode', () {
    expect(
      () => deepCloneJson(<int, dynamic>{1: 'x'}),
      throwsA(isA<ArgumentError>().having((e) => e.message, 'message', contains('String-keyed'))),
    );
    expect(
      () => deepCloneJson(<String, dynamic>{'outer': <Object, Object>{'k': 1}}),
      throwsA(isA<ArgumentError>()),
      reason: 'a non-String-keyed map nested inside a valid one is caught too',
    );
  });
}
