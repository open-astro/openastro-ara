import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/instruction_catalog.dart';

void main() {
  group('catalog integrity', () {
    test('every entry has an assembly-qualified OpenAstroAra \$type', () {
      for (final def in instructionCatalog) {
        expect(def.type, contains('OpenAstroAra.Sequencer.SequenceItem.'));
        expect(def.type, endsWith(', OpenAstroAra.Sequencer'));
        expect(def.label, isNotEmpty);
      }
    });

    test('no duplicate \$type', () {
      final types = instructionCatalog.map((d) => d.type).toList();
      expect(types.toSet(), hasLength(types.length));
    });

    test('no instruction has duplicate field keys', () {
      for (final def in instructionCatalog) {
        final keys = def.fields.map((f) => f.key).toList();
        expect(keys.toSet(), hasLength(keys.length), reason: def.label);
      }
    });

    test('the outer category map is unmodifiable too', () {
      expect(() => instructionCatalogByCategory[InstructionCategory.camera] = const [],
          throwsUnsupportedError);
    });

    test('grouping preserves declaration order and drops nothing', () {
      final flattened =
          instructionCatalogByCategory.values.expand((e) => e).toList();
      expect(flattened, hasLength(instructionCatalog.length));
      // Camera entries come out in their declared order.
      final camera = instructionCatalogByCategory[InstructionCategory.camera]!;
      expect(camera.map((d) => d.label),
          containsAllInOrder(['Take Exposure', 'Cool Camera', 'Warm Camera']));
    });

    test('grouped inner lists are unmodifiable (singleton can\'t be corrupted)', () {
      final camera = instructionCatalogByCategory[InstructionCategory.camera]!;
      expect(() => camera.add(camera.first), throwsUnsupportedError);
    });

    test('instructionForType is consistent with the catalog list', () {
      for (final def in instructionCatalog) {
        expect(identical(instructionForType(def.type), def), isTrue);
      }
      expect(instructionForType('Nope.Not.A.Type, Whatever'), isNull);
    });

    test('SwitchFilter.Filter is flagged requiresUserInput', () {
      final def = instructionForType(
          'OpenAstroAra.Sequencer.SequenceItem.FilterWheel.SwitchFilter, OpenAstroAra.Sequencer')!;
      final filter = def.fields.singleWhere((f) => f.key == 'Filter');
      expect(filter.requiresUserInput, isTrue);
      // No other catalogued field demands user input by default.
      final others = instructionCatalog
          .expand((d) => d.fields)
          .where((f) => f.requiresUserInput && f.key != 'Filter');
      expect(others, isEmpty);
    });

    test('intEnum/stringEnum fields carry their option set', () {
      for (final def in instructionCatalog) {
        for (final f in def.fields) {
          if (f.type == InstructionFieldType.intEnum) {
            expect(f.enumLabels, isNotNull, reason: '${def.label}.${f.key}');
            expect(f.enumLabels!.containsKey(f.defaultValue), isTrue);
          }
          if (f.type == InstructionFieldType.stringEnum) {
            expect(f.enumValues, isNotNull, reason: '${def.label}.${f.key}');
            expect(f.enumValues, contains(f.defaultValue));
          }
        }
      }
    });
  });

  group('build()', () {
    test('emits \$type + base item fields the daemon templates use', () {
      for (final def in instructionCatalog) {
        final node = def.build();
        expect(node[r'$type'], def.type);
        expect(node.containsKey('Parent'), isTrue);
        expect(node['Parent'], isNull);
        expect(node['ErrorBehavior'], 0); // integer enum, per templates
        expect(node['Attempts'], 1);
        // every declared field is present at its default
        for (final f in def.fields) {
          expect(node.containsKey(f.key), isTrue, reason: '${def.label}.${f.key}');
        }
      }
    });

    test('no built node emits Name/Description (matches daemon templates)', () {
      for (final def in instructionCatalog) {
        final node = def.build();
        expect(node.containsKey('Name'), isFalse, reason: def.label);
        expect(node.containsKey('Description'), isFalse, reason: def.label);
      }
    });

    test('TakeExposure builds a runnable, capturable node', () {
      final def = instructionForType(
          'OpenAstroAra.Sequencer.SequenceItem.Imaging.TakeExposure, OpenAstroAra.Sequencer')!;
      final node = def.build();
      expect(node['ExposureTime'], 1.0);
      expect(node['Gain'], -1);
      expect(node['ImageType'], 'LIGHT');
      // Binning is a nested BinningMode object 1x1.
      final binning = node['Binning'] as Map<String, dynamic>;
      expect(binning[r'$type'], contains('BinningMode'));
      expect(binning['X'], 1);
      expect(binning['Y'], 1);
      // capturable per the server validator: contains .SequenceItem. but not a container.
      expect(node[r'$type'], contains('.SequenceItem.'));
      expect(node[r'$type'], isNot(contains('.SequenceItem.Container.')));
    });

    test('SlewScopeToRaDec builds nested InputCoordinates', () {
      final node = instructionForType(
              'OpenAstroAra.Sequencer.SequenceItem.Telescope.SlewScopeToRaDec, OpenAstroAra.Sequencer')!
          .build();
      final coords = node['Coordinates'] as Map<String, dynamic>;
      expect(coords[r'$type'], contains('InputCoordinates'));
      expect(coords['RAHours'], 0);
      expect(coords['NegativeDec'], false);
      expect(node['Inherited'], false);
    });

    test('SetTracking default is Sidereal (0)', () {
      final node = instructionForType(
              'OpenAstroAra.Sequencer.SequenceItem.Telescope.SetTracking, OpenAstroAra.Sequencer')!
          .build();
      expect(node['TrackingMode'], 0);
      expect(trackingModeLabels[0], 'Sidereal');
    });

    test('field-less instructions build to just \$type + base fields', () {
      final node = instructionForType(
              'OpenAstroAra.Sequencer.SequenceItem.Guider.Dither, OpenAstroAra.Sequencer')!
          .build();
      expect(node.keys, containsAll([r'$type', 'Parent', 'ErrorBehavior', 'Attempts']));
      expect(node.keys.where((k) => k != r'$type' && k != 'Parent' && k != 'ErrorBehavior' && k != 'Attempts'),
          isEmpty);
    });

    test('build() throws on duplicate field keys (enforced in release too)', () {
      const dup = InstructionDef(
        type: 'X.Dup, X',
        label: 'dup',
        category: InstructionCategory.utility,
        icon: Icons.error,
        fields: [
          InstructionField('K', 'a', InstructionFieldType.integer, defaultValue: 1),
          InstructionField('K', 'b', InstructionFieldType.integer, defaultValue: 2),
        ],
      );
      expect(() => dup.build(), throwsStateError);
    });

    test('build() asserts on a non-String-keyed map default (debug)', () {
      // `flutter test` runs in debug mode, where asserts are live; in a release
      // build the assert is stripped and a TypeError would surface at the cast.
      const bad = InstructionDef(
        type: 'X.Bad, X',
        label: 'bad',
        category: InstructionCategory.utility,
        icon: Icons.error,
        fields: [
          InstructionField('K', 'k', InstructionFieldType.binning, defaultValue: {1: 'a'}),
        ],
      );
      expect(() => bad.build(), throwsA(isA<AssertionError>()));
    });

    test('built nodes share no mutable sub-object (deep clone)', () {
      final def = instructionForType(
          'OpenAstroAra.Sequencer.SequenceItem.Imaging.TakeExposure, OpenAstroAra.Sequencer')!;
      final a = def.build();
      final b = def.build();
      (a['Binning'] as Map)['X'] = 4;
      expect((b['Binning'] as Map)['X'], 1); // unaffected
      // and the const catalog default is untouched
      expect(defaultBinning['X'], 1);
    });

    test('SwitchFilter Filter defaults to null (resolved in the editor)', () {
      final node = instructionForType(
              'OpenAstroAra.Sequencer.SequenceItem.FilterWheel.SwitchFilter, OpenAstroAra.Sequencer')!
          .build();
      expect(node.containsKey('Filter'), isTrue);
      expect(node['Filter'], isNull);
    });
  });

  group('instructionForType', () {
    test('finds a known type and returns null for an unknown one', () {
      expect(
          instructionForType(
              'OpenAstroAra.Sequencer.SequenceItem.Guider.Dither, OpenAstroAra.Sequencer'),
          isNotNull);
      expect(instructionForType('Nope.Not.A.Type, Whatever'), isNull);
    });
  });
}
