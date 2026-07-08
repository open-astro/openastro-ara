import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/instruction_catalog.dart';
import 'package:openastroara/models/sequence/nina_dom.dart';
import 'package:openastroara/models/sequence/trigger_catalog.dart';

const _meridianFlip =
    'OpenAstroAra.Sequencer.Trigger.MeridianFlip.MeridianFlipTrigger, OpenAstroAra.Sequencer';
const _reconnect =
    'OpenAstroAra.Sequencer.Trigger.Connect.ReconnectTrigger, OpenAstroAra.Sequencer';

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
    test('emits \$type + Parent + a SequentialContainer TriggerRunner seeded '
        'with the def\'s runner items', () {
      for (final def in triggerCatalog) {
        final node = def.build();
        expect(node[r'$type'], def.type);
        expect(node.containsKey('Parent'), isTrue);
        expect(node['Parent'], isNull);
        // Triggers aren't SequenceItems — no ErrorBehavior/Attempts.
        expect(node.containsKey('ErrorBehavior'), isFalse, reason: def.label);
        expect(node.containsKey('Attempts'), isFalse, reason: def.label);
        // The runner is a real SequentialContainer the DOM recognises, holding
        // exactly the def's action items (an empty runner is a silent no-op on
        // the daemon, so action triggers must ship theirs).
        final runner = node['TriggerRunner'] as Map<String, dynamic>;
        expect(runner[r'$type'], contains('SequentialContainer'));
        expect(isContainer(runner), isTrue);
        expect(
          childrenOf(runner).map((c) => c[r'$type']).toList(),
          def.runnerItems,
          reason: def.label,
        );
      }
    });

    test('every autofocus trigger fires a RunAutofocus; the dither trigger '
        'fires a Dither', () {
      for (final def in triggerCatalog) {
        if (def.type.contains('.Trigger.Autofocus.')) {
          expect(def.runnerItems, [runAutofocusType], reason: def.label);
        }
        if (def.type.contains('DitherAfterExposures')) {
          expect(def.runnerItems, [ditherType], reason: def.label);
        }
      }
    });

    test('MeridianFlipTrigger carries no editable fields', () {
      final def = triggerForType(_meridianFlip)!;
      expect(def.fields, isEmpty);
      final node = def.build();
      // exactly $type + Parent + TriggerRunner
      expect(node.keys.toSet(), {r'$type', 'Parent', 'TriggerRunner'});
    });

    test('each build produces its own TriggerRunner (not a shared instance)', () {
      final a = triggerForType(_meridianFlip)!.build();
      final b = triggerForType(_meridianFlip)!.build();
      // Distinct runner instances; deep fresh-list isolation of the container is
      // covered by the instruction catalog's container test (the runner is built
      // through instructionForType, so it inherits that guarantee).
      expect(identical(a['TriggerRunner'], b['TriggerRunner']), isFalse);
      expect(childrenOf(a['TriggerRunner'] as Map<String, dynamic>), isEmpty);
      expect(childrenOf(b['TriggerRunner'] as Map<String, dynamic>), isEmpty);
    });

    test('ReconnectTrigger builds with a SelectedDevice from the device set', () {
      final def = triggerForType(_reconnect)!;
      // SelectedDevice is a stringEnum over the grounded device names.
      final field = def.fields.single;
      expect(field.key, 'SelectedDevice');
      expect(field.type, InstructionFieldType.stringEnum);
      expect(field.enumValues, reconnectDeviceNames);
      expect(field.enumValues, contains(field.defaultValue)); // default is valid
      final node = def.build();
      expect(node['SelectedDevice'], 'Camera'); // C# constructor default
      expect(node.keys.toSet(), {r'$type', 'SelectedDevice', 'Parent', 'TriggerRunner'});
      // "Mount" replaced the legacy "Telescope" name; "Telescope" must be absent.
      expect(reconnectDeviceNames, contains('Mount'));
      expect(reconnectDeviceNames, isNot(contains('Telescope')));
    });

    test('build() throws if a field key collides with a reserved base key', () {
      const bad = TriggerDef(
        type: 'X.Bad, X',
        label: 'bad',
        icon: Icons.error,
        fields: [
          InstructionField('TriggerRunner', 'oops', InstructionFieldType.text,
              defaultValue: ''),
        ],
      );
      expect(() => bad.build(), throwsStateError);
    });
  });
}
