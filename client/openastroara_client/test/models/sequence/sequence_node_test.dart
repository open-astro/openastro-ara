import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_node.dart';

void main() {
  group('SequenceNode', () {
    test('default params + children are unmodifiable', () {
      final node = SequenceNode(
        id: 'n1',
        kind: SequenceNodeKind.instruction,
        displayName: 'Test',
      );
      expect(() => node.params['k'] = 'v', throwsUnsupportedError);
      expect(
        () => node.children.add(SequenceNode(
          id: 'x',
          kind: SequenceNodeKind.instruction,
          displayName: 'X',
        )),
        throwsUnsupportedError,
      );
    });

    test('deep-freeze prevents nested list mutation', () {
      final node = SequenceNode(
        id: 'loop',
        kind: SequenceNodeKind.loopContainer,
        displayName: 'Filter loop',
        params: const <String, Object?>{
          'filters': <String>['L', 'R', 'G', 'B'],
        },
      );
      final filters = node.params['filters'] as List<Object?>;
      expect(() => filters.add('Hα'), throwsUnsupportedError);
    });

    test('deep-freeze prevents nested map mutation', () {
      final node = SequenceNode(
        id: 'cond',
        kind: SequenceNodeKind.conditionalContainer,
        displayName: 'If safe',
        params: const <String, Object?>{
          'nested': <String, Object?>{'a': 1},
        },
      );
      final nested = node.params['nested'] as Map<Object?, Object?>;
      expect(() => nested['b'] = 2, throwsUnsupportedError);
    });

    test('equality is deep over nested lists', () {
      SequenceNode build() => SequenceNode(
            id: 'loop',
            kind: SequenceNodeKind.loopContainer,
            displayName: 'Filter loop',
            params: const <String, Object?>{
              'filters': <String>['L', 'R', 'G', 'B'],
            },
          );
      final a = build();
      final b = build();
      expect(a, equals(b));
      expect(a.hashCode, equals(b.hashCode));
    });

    test('different nested values compare unequal', () {
      final a = SequenceNode(
        id: 'l',
        kind: SequenceNodeKind.loopContainer,
        displayName: 'L',
        params: const <String, Object?>{
          'filters': <String>['L'],
        },
      );
      final b = SequenceNode(
        id: 'l',
        kind: SequenceNodeKind.loopContainer,
        displayName: 'L',
        params: const <String, Object?>{
          'filters': <String>['R'],
        },
      );
      expect(a, isNot(equals(b)));
    });

    test('copyWith preserves other fields when changing displayName', () {
      final original = SequenceNode(
        id: 'n1',
        kind: SequenceNodeKind.instruction,
        instructionType: 'Foo',
        displayName: 'Original',
        params: const <String, Object?>{'k': 'v'},
      );
      final updated = original.copyWith(displayName: 'New');
      expect(updated.id, original.id);
      expect(updated.kind, original.kind);
      expect(updated.instructionType, 'Foo');
      expect(updated.params, original.params);
      expect(updated.displayName, 'New');
    });

    test('copyWith sentinel clears instructionType', () {
      final original = SequenceNode(
        id: 'n1',
        kind: SequenceNodeKind.instruction,
        instructionType: 'Foo',
        displayName: 'X',
      );
      final cleared = original.copyWith(instructionType: null);
      expect(cleared.instructionType, isNull);
    });

    test('isContainer is true for non-instruction kinds', () {
      for (final kind in SequenceNodeKind.values) {
        final node = SequenceNode(id: 'n', kind: kind, displayName: 'x');
        expect(
          node.isContainer,
          kind != SequenceNodeKind.instruction,
          reason: 'kind=$kind',
        );
      }
    });
  });
}
