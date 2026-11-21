import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/util/nina_import.dart';

import 'sequence_v2_schema_check.dart';

/// Holds the Dart importer's kind and field mapping to the daemon's (#2026):
/// the checked-in `docs/specs/sequence-v2.schema.json` (generated from the
/// engine contract by `SequenceV2Schema.Generate()`) and the kind table
/// `OpenAstroAra.Sequencer/Format/SequenceKinds.cs`, which the C# migration
/// (#2027) reads too. A class or field on one side only fails here.

const _rootType =
    'NINA.Sequencer.Container.SequenceRootContainer, NINA.Sequencer';

Map<String, dynamic> _ninaNode(
  String cls, [
  Map<String, dynamic> props = const {},
]) => {
  r'$type': 'NINA.Sequencer.SequenceItem.Any.$cls, NINA.Sequencer',
  ...props,
};

/// A NINA root holding [node] in the list its role belongs in.
Map<String, dynamic> _rootWith(String role, Map<String, dynamic> node) => {
  r'$type': _rootType,
  'Name': 'Table',
  'Items': {
    r'$values': role == 'Instruction' || role == 'Container' ? [node] : [],
  },
  'Conditions': {
    r'$values': role == 'Condition' ? [node] : [],
  },
  'Triggers': {
    r'$values': role == 'Trigger' ? [node] : [],
  },
};

/// The v2 node the importer made for [node] (the one list entry under the root).
Map<String, dynamic>? _imported(String role, Map<String, dynamic> node) {
  final t = translateNinaSequence(_rootWith(role, node));
  final root = (t.body['root'] as Map?)?.cast<String, dynamic>();
  final list = switch (role) {
    'Condition' => root?['conditions'],
    'Trigger' => root?['triggers'],
    _ => root?['items'],
  };
  return list is List && list.length == 1
      ? (list.single as Map).cast<String, dynamic>()
      : null;
}

/// The NINA property name of a v2 field: the engine property, i.e. the field
/// with its first letter up, but for the renames in the spec.
String _ninaName(String field) => switch (field) {
  'usbLimit' => 'USBLimit',
  'expanded' => 'IsExpanded',
  _ => field[0].toUpperCase() + field.substring(1),
};

void main() {
  final daemon = daemonKinds();
  final schemaClasses = schemaKindClasses();
  // `root` is the document root, not a list entry.
  final listKinds = [
    for (final k in daemon)
      if (k.kind != 'root') k,
  ];

  test('the schema and SequenceKinds.cs name the same kinds and classes', () {
    expect(daemon, isNotEmpty);
    expect(schemaClasses, {for (final k in daemon) k.kind: k.engineClass});
  });

  group('every class in the kind table imports to its kind', () {
    for (final k in listKinds) {
      test('${k.engineClass} -> ${k.kind} (${k.role})', () {
        final props = <String, dynamic>{
          if (k.role == 'Container') ...{
            'Name': 'N',
            'Items': {r'$values': <Object?>[]},
          },
          // A target container needs the Target it hoists.
          if (k.kind == 'target')
            'Target': {
              'TargetName': 'T',
              'InputCoordinates': {
                'RAHours': 1,
                'RAMinutes': 2,
                'RASeconds': 3.0,
                'NegativeDec': false,
                'DecDegrees': 4,
                'DecMinutes': 5,
                'DecSeconds': 6.0,
              },
            },
        };
        final out = _imported(k.role, _ninaNode(k.engineClass, props));
        expect(out, isNotNull, reason: '${k.engineClass} produced no node');
        expect(out!['kind'], k.kind);
      });
    }
  });

  group('every field of a kind survives, and nothing else is added', () {
    final defs = loadSequenceV2Schema()[r'$defs'] as Map<String, dynamic>;
    // Fields with their own rule, tested in nina_import_test.dart.
    const special = {
      'kind',
      'items',
      'conditions',
      'triggers',
      'run',
      'provider',
      'targetId',
      'errorBehavior',
      'trackingMode',
      'filter',
      'binning',
    };
    for (final k in listKinds) {
      test(k.kind, () {
        final spec =
            (defs[k.kind] as Map)['properties'] as Map<String, dynamic>;
        final input = <String, dynamic>{};
        final expected = <String, dynamic>{'kind': k.kind};
        for (final entry in spec.entries) {
          final field = entry.key;
          if (special.contains(field)) continue;
          final type = entry.value['type'];
          final types = type is List ? type.cast<String>() : [type as String];
          final t = types.firstWhere((x) => x != 'null');
          final (nina, v2) = switch (t) {
            'string' => ('x', 'x'),
            'integer' => (7, 7),
            'number' => (1.5, 1.5),
            'boolean' => (true, true),
            'array' => (<Object?>[], <Object?>[]),
            _ => (<String, dynamic>{'Foo': 1}, <String, dynamic>{'foo': 1}),
          };
          input[_ninaName(field)] = nina;
          expected[field] = v2;
        }
        // The two nested values with their own key rule (spec "Renamed fields"):
        // only the spec keys stay, `_name` and `Name` alike.
        if (spec.containsKey('filter')) {
          input['Filter'] = {
            r'$type': 'x, y',
            '_name': 'L',
            '_position': 2,
            '_autoFocusGain': 1,
          };
          expected['filter'] = {'name': 'L', 'position': 2};
        }
        if (spec.containsKey('binning')) {
          input['Binning'] = {r'$type': 'x, y', 'X': 2, 'Y': 3, 'Name': 'n'};
          expected['binning'] = {'x': 2, 'y': 3};
        }
        if (k.role == 'Container') input['Items'] = {r'$values': <Object?>[]};
        if (k.kind == 'target') {
          input['Target'] = {
            'TargetName': 'T',
            'InputCoordinates': {'RAHours': 1},
          };
          expected['targetId'] = 't1';
        }
        final out = _imported(k.role, _ninaNode(k.engineClass, input));
        expect(out, isNotNull);
        final got = Map<String, dynamic>.of(out!);
        // The importer may add the empty child lists of a container.
        for (final list in const ['items', 'conditions', 'triggers']) {
          if (!expected.containsKey(list) &&
              got[list] is List &&
              (got[list] as List).isEmpty) {
            got.remove(list);
          }
        }
        expect(got, expected);
      });
    }
  });

  group('classes with no kind become placeholders', () {
    const absent = {
      'NINA.Sequencer.Container.ConditionalContainer, NINA.Sequencer':
          'Container',
      'NINA.Sequencer.SequenceItem.Imaging.GloverExposure, NINA.Sequencer':
          'Instruction',
      'NINA.Sequencer.Trigger.Connect.ReconnectOnDownloadFailure, NINA.Sequencer':
          'Trigger',
      'NINA.Plugins.Connector.Instructions.ReconnectOnDownloadFailure, NINA.Plugins.Connector':
          'Trigger',
      'NINA.Plugins.Connector.Instructions.SomethingNew, NINA.Plugins.Connector':
          'Instruction',
      // A plugin class that shares a name with a kind is still a plugin class.
      'NINA.Plugin.Example.SequenceItem.TakeExposure, NINA.Plugin.Example':
          'Instruction',
      'NINA.Plugin.Example.Trigger.DitherAfterExposures, NINA.Plugin.Example':
          'Trigger',
      // The Connector namespace names six classes, not every class of that name.
      'NINA.Plugins.Connector.Instructions.ParkScope, NINA.Plugins.Connector':
          'Instruction',
    };
    for (final e in absent.entries) {
      test(e.key.split(',').first.split('.').last, () {
        final out = _imported(e.value, {r'$type': e.key});
        expect(out, isNotNull);
        expect(out!['kind'], 'unknown');
        expect(out['originalType'], e.key);
        expect(out['role'], e.value.toLowerCase());
      });
    }
  });

  group('the area containers import as sequential', () {
    for (final cls in [
      'StartAreaContainer',
      'TargetAreaContainer',
      'EndAreaContainer',
    ]) {
      test(cls, () {
        final out = _imported('Container', {
          r'$type': 'NINA.Sequencer.Container.$cls, NINA.Sequencer',
          'Name': 'Area',
          'Items': {r'$values': <Object?>[]},
        });
        expect(out!['kind'], 'sequential');
        expect(out['name'], 'Area');
      });
    }
  });

  test('the output of every fixture is accepted by the schema', () {
    for (final f in Directory(
      'test/fixtures/nina',
    ).listSync().whereType<File>()) {
      final nina = jsonDecode(f.readAsStringSync()) as Map<String, dynamic>;
      expect(
        schemaViolations(translateNinaSequence(nina).body),
        isEmpty,
        reason: f.path,
      );
    }
  });
}
