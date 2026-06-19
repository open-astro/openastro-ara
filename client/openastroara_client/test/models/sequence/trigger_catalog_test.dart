import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/nina_dom.dart';
import 'package:openastroara/models/sequence/trigger_catalog.dart';

const _meridianFlip =
    'OpenAstroAra.Sequencer.Trigger.MeridianFlip.MeridianFlipTrigger, OpenAstroAra.Sequencer';

void main() {
  group('catalog integrity', () {
    test('every entry has an assembly-qualified Trigger \$type', () {
      for (final def in triggerCatalog) {
        expect(def.type, contains('OpenAstroAra.Sequencer.Trigger.'));
        expect(def.type, endsWith(', OpenAstroAra.Sequencer'));
        expect(def.label, isNotEmpty);
      }
    });

    test('no duplicate \$type', () {
      final types = triggerCatalog.map((d) => d.type).toList();
      expect(types.toSet(), hasLength(types.length));
    });

    test('triggerForType finds known types, null for unknown', () {
      for (final def in triggerCatalog) {
        expect(identical(triggerForType(def.type), def), isTrue);
      }
      expect(triggerForType('Nope.Not.A.Type, Whatever'), isNull);
    });
  });

  group('build()', () {
    test('emits \$type + Parent + an empty SequentialContainer TriggerRunner', () {
      for (final def in triggerCatalog) {
        final node = def.build();
        expect(node[r'$type'], def.type);
        expect(node.containsKey('Parent'), isTrue);
        expect(node['Parent'], isNull);
        // Triggers aren't SequenceItems — no ErrorBehavior/Attempts.
        expect(node.containsKey('ErrorBehavior'), isFalse, reason: def.label);
        expect(node.containsKey('Attempts'), isFalse, reason: def.label);
        // The runner is a real, empty SequentialContainer the DOM recognises.
        final runner = node['TriggerRunner'] as Map<String, dynamic>;
        expect(runner[r'$type'], contains('SequentialContainer'));
        expect(isContainer(runner), isTrue);
        expect(childrenOf(runner), isEmpty);
      }
    });

    test('MeridianFlipTrigger carries no editable fields', () {
      final def = triggerForType(_meridianFlip)!;
      expect(def.fields, isEmpty);
      final node = def.build();
      // exactly $type + Parent + TriggerRunner
      expect(node.keys.toSet(), {r'$type', 'Parent', 'TriggerRunner'});
    });

    test('two builds share no TriggerRunner instance (fresh each time)', () {
      final a = triggerForType(_meridianFlip)!.build();
      final b = triggerForType(_meridianFlip)!.build();
      final runnerA = a['TriggerRunner'] as Map<String, dynamic>;
      // Mutating one runner's Items must not touch the other.
      final mutated = withChildren(runnerA, [
        {r'$type': 'X.TakeExposure'}
      ]);
      a['TriggerRunner'] = mutated;
      expect(childrenOf(b['TriggerRunner'] as Map<String, dynamic>), isEmpty);
    });
  });
}
