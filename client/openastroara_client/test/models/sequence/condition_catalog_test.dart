import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/condition_catalog.dart';
import 'package:openastroara/models/sequence/instruction_catalog.dart';

const _loop =
    'OpenAstroAra.Sequencer.Conditions.LoopCondition, OpenAstroAra.Sequencer';
const _timeSpan =
    'OpenAstroAra.Sequencer.Conditions.TimeSpanCondition, OpenAstroAra.Sequencer';
const _altitude =
    'OpenAstroAra.Sequencer.Conditions.AltitudeCondition, OpenAstroAra.Sequencer';
const _aboveHorizon =
    'OpenAstroAra.Sequencer.Conditions.AboveHorizonCondition, OpenAstroAra.Sequencer';

void main() {
  group('catalog integrity', () {
    test('every entry has an assembly-qualified Conditions \$type', () {
      for (final def in conditionCatalog) {
        expect(def.type, contains('OpenAstroAra.Sequencer.Conditions.'));
        expect(def.type, endsWith(', OpenAstroAra.Sequencer'));
        expect(def.label, isNotEmpty);
      }
    });

    test('no duplicate \$type', () {
      final types = conditionCatalog.map((d) => d.type).toList();
      expect(types.toSet(), hasLength(types.length));
    });

    test('no condition has duplicate field keys', () {
      for (final def in conditionCatalog) {
        final keys = def.fields.map((f) => f.key).toList();
        expect(keys.toSet(), hasLength(keys.length), reason: def.label);
      }
    });

    test('conditionForType finds known types, null for unknown', () {
      for (final def in conditionCatalog) {
        expect(identical(conditionForType(def.type), def), isTrue);
      }
      expect(conditionForType('Nope.Not.A.Type, Whatever'), isNull);
    });
  });

  group('build()', () {
    test('emits \$type + fields + Parent, but no ErrorBehavior/Attempts', () {
      for (final def in conditionCatalog) {
        final node = def.build();
        expect(node[r'$type'], def.type);
        expect(node.containsKey('Parent'), isTrue);
        expect(node['Parent'], isNull);
        // Conditions are not SequenceItems — these are deliberately absent.
        expect(node.containsKey('ErrorBehavior'), isFalse, reason: def.label);
        expect(node.containsKey('Attempts'), isFalse, reason: def.label);
        for (final f in def.fields) {
          expect(node.containsKey(f.key), isTrue, reason: '${def.label}.${f.key}');
        }
      }
    });

    test('LoopCondition defaults to 2 iterations with a zeroed runtime counter', () {
      final node = conditionForType(_loop)!.build();
      expect(node['Iterations'], 2);
      expect(node['CompletedIterations'], 0);
    });

    test('TimeSpanCondition defaults to a 1-minute span', () {
      final node = conditionForType(_timeSpan)!.build();
      expect(node['Hours'], 0);
      expect(node['Minutes'], 1);
      expect(node['Seconds'], 0);
    });

    test('TimeSpanCondition minutes/seconds clamp to 0..59, hours to >= 0', () {
      final def = conditionForType(_timeSpan)!;
      for (final key in ['Minutes', 'Seconds']) {
        final f = def.fields.firstWhere((f) => f.key == key);
        expect(f.min, 0, reason: key);
        expect(f.max, 59, reason: key);
      }
      final hours = def.fields.firstWhere((f) => f.key == 'Hours');
      expect(hours.min, 0);
      expect(hours.max, isNull);
    });

    test('LoopCondition iterations are bounded to >= 1', () {
      final iter =
          conditionForType(_loop)!.fields.firstWhere((f) => f.key == 'Iterations');
      expect(iter.min, 1);
      expect(iter.max, isNull);
    });

    test('TimeCondition builds a null provider (custom clock time) + clamped H/M/S', () {
      final def = conditionForType(
          'OpenAstroAra.Sequencer.Conditions.TimeCondition, OpenAstroAra.Sequencer')!;
      final node = def.build();
      expect(node.containsKey('SelectedProvider'), isTrue);
      expect(node['SelectedProvider'], isNull); // Custom time by default
      expect(node['Hours'], 0);
      expect(node['MinutesOffset'], 0);
      expect(def.fields.firstWhere((f) => f.key == 'Hours').max, 23);
      expect(def.fields.firstWhere((f) => f.key == 'Minutes').max, 59);
      expect(def.fields.firstWhere((f) => f.key == 'Seconds').max, 59);
    });

    test('TimeCondition H/M/S grey out under a sky-event provider (enabledWhen)', () {
      final def = conditionForType(
          'OpenAstroAra.Sequencer.Conditions.TimeCondition, OpenAstroAra.Sequencer')!;
      final provider = timeProviderValue('Civil Dusk'); // a recognised provider
      for (final key in ['Hours', 'Minutes', 'Seconds']) {
        final f = def.fields.firstWhere((f) => f.key == key);
        expect(f.enabledWhen, isNotNull, reason: key);
        expect(f.enabledWhen!(const {'SelectedProvider': null}), isTrue, reason: key);
        expect(f.enabledWhen!({'SelectedProvider': provider}), isFalse, reason: key);
      }
      // MinutesOffset is the inverse: enabled ONLY under a provider (the daemon
      // applies it to the computed event time; it's inert for custom clock time).
      final offset = def.fields.firstWhere((f) => f.key == 'MinutesOffset');
      expect(offset.enabledWhen, isNotNull);
      expect(offset.enabledWhen!(const {'SelectedProvider': null}), isFalse);
      expect(offset.enabledWhen!({'SelectedProvider': provider}), isTrue);
    });

    test('an unrecognised SelectedProvider reads as Custom time consistently', () {
      // The dropdown falls back to 'Custom time' for an unknown $type; the H/M/S
      // relevance must agree (enabled) rather than greying under that label.
      final def = conditionForType(
          'OpenAstroAra.Sequencer.Conditions.TimeCondition, OpenAstroAra.Sequencer')!;
      const unknown = {'SelectedProvider': {r'$type': 'Nope.NotAProvider'}};
      expect(timeProviderLabel(unknown['SelectedProvider']), 'Custom time');
      expect(def.fields.firstWhere((f) => f.key == 'Hours').enabledWhen!(unknown), isTrue);
      expect(
        def.fields.firstWhere((f) => f.key == 'MinutesOffset').enabledWhen!(unknown),
        isFalse,
      );
    });

    test('timeProvider helpers round-trip a provider \$type', () {
      expect(timeProviderValue('Custom time'), isNull);
      final dusk = timeProviderValue('Civil Dusk')!;
      expect(dusk[r'$type'], contains('CivilDuskProvider'));
      expect(timeProviderLabel(dusk), 'Civil Dusk');
      expect(timeProviderLabel(null), 'Custom time');
      expect(timeProviderLabel(const {r'$type': 'Unknown'}), 'Custom time');
    });

    test('two builds share no map instance (fresh node each time)', () {
      final a = conditionForType(_loop)!.build();
      final b = conditionForType(_loop)!.build();
      a['Iterations'] = 99;
      expect(b['Iterations'], 2); // unaffected
    });

    test('AltitudeCondition builds Data (WaitLoopData) + hidden HasDsoParent', () {
      final def = conditionForType(_altitude)!;
      final node = def.build();
      expect(node['HasDsoParent'], false);
      final data = node['Data'] as Map;
      expect(data[r'$type'], waitLoopDataType);
      expect(data['Offset'], 30.0);
      expect(data['Comparator'], 1); // LessThan
      expect((data['Coordinates'] as Map)[r'$type'], contains('InputCoordinates'));
      // HasDsoParent is a runtime-recomputed field — present but not user-editable.
      expect(def.fields.firstWhere((f) => f.key == 'HasDsoParent').editable, isFalse);
    });

    test('AboveHorizonCondition builds Data with offset 0 / GreaterThan', () {
      final node = conditionForType(_aboveHorizon)!.build();
      final data = node['Data'] as Map;
      expect(data['Offset'], 0.0);
      expect(data['Comparator'], 3); // GreaterThan
    });

    test('altitude comparator allow-list is exactly LessThan/GreaterThan', () {
      expect(altitudeComparators.keys, [1, 3]);
    });

    test('an altitude build deep-clones the nested WaitLoopData / Coordinates', () {
      final a = conditionForType(_altitude)!.build();
      final b = conditionForType(_altitude)!.build();
      (a['Data'] as Map)['Offset'] = 99.0;
      ((a['Data'] as Map)['Coordinates'] as Map)['RAHours'] = 12;
      expect((b['Data'] as Map)['Offset'], 30.0); // unaffected
      expect(((b['Data'] as Map)['Coordinates'] as Map)['RAHours'], 0);
    });

    test('build() throws if a field key collides with a reserved base key', () {
      const bad = ConditionDef(
        type: 'X.Bad, X',
        label: 'bad',
        icon: Icons.error,
        fields: [
          InstructionField('Parent', 'oops', InstructionFieldType.text, defaultValue: ''),
        ],
      );
      expect(() => bad.build(), throwsStateError);
    });

    test('build() throws on duplicate field keys (release too)', () {
      const dup = ConditionDef(
        type: 'X.Dup, X',
        label: 'dup',
        icon: Icons.error,
        fields: [
          InstructionField('K', 'a', InstructionFieldType.integer, defaultValue: 1),
          InstructionField('K', 'b', InstructionFieldType.integer, defaultValue: 2),
        ],
      );
      expect(() => dup.build(), throwsStateError);
    });

    test('build() deep-clones an object-valued default (no shared mutable state)', () {
      // Conditions are all scalar today, but build() clones unconditionally
      // (like InstructionDef) so a future object-valued default is safe.
      const def = ConditionDef(
        type: 'X.Obj, X',
        label: 'obj',
        icon: Icons.error,
        fields: [
          InstructionField('Data', 'data', InstructionFieldType.binning,
              defaultValue: {'X': 1}),
        ],
      );
      final a = def.build();
      final b = def.build();
      (a['Data'] as Map)['X'] = 99;
      expect((b['Data'] as Map)['X'], 1); // second build unaffected
    });
  });
}
