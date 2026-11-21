import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/util/nina_import.dart';

import 'sequence_v2_schema_check.dart';

/// The NINA importer writes sequence v2 (docs/specs/sequence-v2.md, "NINA JSON
/// import", #2026). The fixtures are NINA-shaped Json.NET files: `$id`,
/// `$values` wrappers, a `Parent` `$ref` on every child, `TriggerRunner`.

const _refused = 'This file is not a NINA sequence.';

Map<String, dynamic> _fixture(String name) =>
    jsonDecode(File('test/fixtures/nina/$name.json').readAsStringSync())
        as Map<String, dynamic>;

String _placeholderWarning(String originalType) =>
    '"$originalType" is not supported by OpenAstro Ara and was not imported. '
    'It stays in the sequence as a placeholder that is skipped when the '
    'sequence runs.';

/// Translates and checks the one thing every success test depends on, so a
/// failure names the document version, not a missing key further down.
NinaImportTranslation _import(Map<String, dynamic> nina) {
  final t = translateNinaSequence(nina);
  expect(t.body['schemaVersion'], 'openastroara-sequence-v2');
  return t;
}

Map<String, dynamic> _root(NinaImportTranslation t) =>
    (t.body['root'] as Map?)?.cast<String, dynamic>() ?? <String, dynamic>{};

List<Map<String, dynamic>> _all(NinaImportTranslation t) =>
    v2Nodes(_root(t)).toList();

List<Map<String, dynamic>> _ofKind(NinaImportTranslation t, String kind) =>
    _all(t).where((n) => n['kind'] == kind).toList();

List<String> _kinds(Object? nodes) => [
  for (final n in nodes as List) (n as Map)['kind'] as String,
];

Map<String, dynamic> _ninaRoot({
  List<Object?> items = const [],
  Map<String, dynamic>? extra,
}) => {
  r'$type': 'NINA.Sequencer.Container.SequenceRootContainer, NINA.Sequencer',
  'Name': 'Root',
  'Items': {r'$values': items},
  ...?extra,
};

/// The first node of the NINA file [source] whose `$type` contains [type], as
/// found (the payload of a placeholder is this, unchanged).
Map<String, dynamic>? _source(Object? source, String type) {
  if (source is Map) {
    if ('${source[r'$type']}'.contains(type)) {
      return source.cast<String, dynamic>();
    }
    for (final v in source.values) {
      final found = _source(v, type);
      if (found != null) return found;
    }
  } else if (source is List) {
    for (final v in source) {
      final found = _source(v, type);
      if (found != null) return found;
    }
  }
  return null;
}

Matcher _refusedWith(String text) =>
    throwsA(predicate((e) => e.toString().contains(text)));

/// Every key in [value], at any depth, outside a placeholder's payload.
Iterable<String> _keys(Object? value) sync* {
  if (value is Map) {
    for (final e in value.entries) {
      yield e.key as String;
      if (e.key == 'originalPayload') continue;
      yield* _keys(e.value);
    }
  } else if (value is List) {
    for (final v in value) {
      yield* _keys(v);
    }
  }
}

void main() {
  group('a target block', () {
    late NinaImportTranslation t;
    setUp(() => t = _import(_fixture('target_block')));

    test('becomes a v2 document with 18 nodes and no schema violation', () {
      expect(t.body['schemaVersion'], 'openastroara-sequence-v2');
      expect(t.body['name'], 'Target block');
      expect(_root(t)['kind'], 'root');
      expect(_root(t)['name'], 'Target block');
      expect(_all(t), hasLength(18));
      expect(schemaViolations(t.body), isEmpty);
      expect(t.warnings, isEmpty);
    });

    test(r'drops $type, $id, Parent, Strategy and the $values wrappers', () {
      final keys = _keys(t.body).toSet();
      expect(keys.where((k) => k.startsWith(r'$')), isEmpty);
      expect(keys, isNot(contains('Parent')));
      expect(keys, isNot(contains('Strategy')));
      expect(keys, isNot(contains('parent')));
      expect(keys, isNot(contains('strategy')));
    });

    test(
      'hoists the DeepSkyObjectContainer Target into targets with a targetId',
      () {
        expect(t.body['targets'], [
          {
            'id': 't1',
            'name': 'M42',
            'coordinates': {
              'raHours': 5,
              'raMinutes': 35,
              'raSeconds': 17.3,
              'negativeDec': true,
              'decDegrees': 5,
              'decMinutes': 23,
              'decSeconds': 28.0,
            },
            'positionAngle': 0.0,
          },
        ]);
        final dso = _ofKind(t, 'target').single;
        expect(dso['targetId'], 't1');
        expect(dso['name'], 'M42');
        expect(dso.containsKey('target'), isFalse);
        expect(dso['exposureInfoList'], isEmpty);
        expect(dso['exposureInfoListExpanded'], isFalse);
        expect(dso['expanded'], isTrue);
        expect(dso['items'], hasLength(5));
      },
    );

    test('maps the capture fields, the error behaviour and the binning', () {
      final captures = _ofKind(t, 'capture');
      expect(captures, hasLength(2));
      final light = captures.firstWhere((c) => c['imageType'] == 'LIGHT');
      expect(light['exposureTime'], 120.0);
      expect(light['gain'], 100);
      expect(light['offset'], 50);
      expect(light['binning'], {'x': 1, 'y': 1});
      expect(light['exposureCount'], 0);
      expect(light['errorBehavior'], 'skip-instruction-set-on-error');
      expect(light['attempts'], 2);
    });

    test('DARKFLAT becomes DARK', () {
      final other = _ofKind(
        t,
        'capture',
      ).firstWhere((c) => c['imageType'] != 'LIGHT');
      expect(other['imageType'], 'DARK');
      expect(other['exposureTime'], 30.0);
    });

    test('SelectedProvider becomes the provider name; the offset is kept', () {
      final wait = _ofKind(t, 'wait-for-time').single;
      expect(wait['provider'], 'nautical-dawn');
      expect(wait['hours'], 0);
      expect(wait['minutesOffset'], -30);
      expect(wait['seconds'], 0);
      expect(wait.containsKey('selectedProvider'), isFalse);
    });

    test('keeps the filter object with its keys in lowerCamelCase', () {
      expect(_ofKind(t, 'switch-filter').single['filter'], {
        'name': 'L',
        'position': 0,
        'focusOffset': 0,
        'autoFocusExposureTime': -1.0,
        'autoFocusFilter': false,
      });
    });

    test(
      'maps the loop condition and the two triggers with their run lists',
      () {
        final loop = _ofKind(t, 'loop-iterations').single;
        expect(loop['iterations'], 3);
        expect(loop['completedIterations'], 0);
        final dither = _ofKind(t, 'dither-after-exposures').single;
        expect(dither['afterExposures'], 3);
        expect(_kinds(dither['run']), ['dither']);
        final focus = _ofKind(t, 'autofocus-after-filter-change').single;
        expect(_kinds(focus['run']), ['run-autofocus']);
        expect(dither.containsKey('triggerRunner'), isFalse);
      },
    );

    test('keeps the inherited slew and centre flags and the guiding flags', () {
      expect(_ofKind(t, 'slew-radec').single['inherited'], isTrue);
      expect(_ofKind(t, 'center-and-rotate').single['positionAngle'], 0.0);
      final guiding = _ofKind(t, 'start-guiding').single;
      expect(guiding['forceCalibration'], isFalse);
      expect(guiding['calibrateEachNight'], isFalse);
    });

    test(
      'a NINA name on an instruction, condition or trigger is not carried',
      () {
        final named = [
          for (final n in _all(t))
            if (n.containsKey('name')) n['kind'],
        ];
        expect(named, everyElement(anyOf('root', 'sequential', 'target')));
      },
    );
  });

  group('a Smart Exposure with triggers', () {
    late NinaImportTranslation t;
    setUp(() => t = _import(_fixture('smart_exposure')));

    test('imports as smart-exposure with its children, 9 nodes', () {
      expect(schemaViolations(t.body), isEmpty);
      expect(_all(t), hasLength(9));
      final smart = _ofKind(t, 'smart-exposure').single;
      expect(smart['name'], 'Smart Exposure');
      expect(_kinds(smart['items']), ['switch-filter', 'capture', 'dither']);
      expect(_kinds(smart['conditions']), ['loop-iterations']);
      expect(_kinds(smart['triggers']), [
        'autofocus-after-exposures',
        'meridian-flip',
      ]);
      expect(t.warnings, isEmpty);
    });

    test('keeps the condition counters and the trigger fields', () {
      final loop = _ofKind(t, 'loop-iterations').single;
      expect(loop['iterations'], 5);
      expect(loop['completedIterations'], 2);
      final focus = _ofKind(t, 'autofocus-after-exposures').single;
      expect(focus['afterExposures'], 10);
      expect(_kinds(focus['run']), ['run-autofocus']);
      expect(_ofKind(t, 'meridian-flip').single['recalibrateGuider'], isTrue);
      expect(_ofKind(t, 'meridian-flip').single.containsKey('run'), isFalse);
      expect(_ofKind(t, 'capture').single['exposureTime'], 300.0);
      expect(_ofKind(t, 'switch-filter').single['filter']['name'], 'Ha');
    });
  });

  group('the Connector plugin', () {
    late NinaImportTranslation t;
    setUp(() => t = _import(_fixture('connector_plugin')));

    test('the five instructions map to the same-named Ara kinds', () {
      expect(_kinds(_root(t)['items']), [
        'connect-all-equipment',
        'connect-equipment',
        'connect-equipment',
        'disconnect-equipment',
        'disconnect-all-equipment',
        'switch-profile',
      ]);
      final profile = _ofKind(t, 'switch-profile').single;
      expect(
        profile['selectedProfileId'],
        '00000000-0000-0000-0000-000000000001',
      );
      expect(profile['reconnect'], isFalse);
    });

    test('ReconnectTrigger becomes reconnect with its run list', () {
      final reconnect = _ofKind(t, 'reconnect').single;
      expect(reconnect['selectedDevice'], 'Camera');
      expect(_kinds(reconnect['run']), ['connect-equipment']);
    });

    test('ReconnectOnDownloadFailure becomes a placeholder, children not imported', () {
      const token =
          'NINA.Plugins.Connector.Instructions.ReconnectOnDownloadFailure, NINA.Plugins.Connector';
      final ph = _ofKind(t, 'unknown').single;
      expect(ph['role'], 'trigger');
      expect(ph['originalType'], token);
      expect(ph['name'], token);
      expect(
        ph['reason'],
        isA<String>().having((s) => s.isNotEmpty, 'not empty', isTrue),
      );
      expect(
        ph['originalPayload'],
        _source(_fixture('connector_plugin'), 'ReconnectOnDownloadFailure'),
        reason: 'the source node is kept as found: \$id, Parent, \$values',
      );
      expect(ph.containsKey('run'), isFalse);
      expect(_all(t), hasLength(10), reason: 'the payload is not walked');
      expect(schemaViolations(t.body), isEmpty);
    });

    test(
      'a device that is not a rig role warns once; the id is kept as found',
      () {
        final devices = [
          for (final n in _ofKind(t, 'connect-equipment')) n['selectedDevice'],
        ];
        expect(devices, containsAll(['Camera', 'Example Camera 7']));
        final unmatched = t.warnings
            .where((w) => w.contains('Example Camera 7'))
            .toList();
        expect(unmatched, hasLength(1));
        expect(
          t.warnings,
          hasLength(2),
          reason: 'one placeholder, one unmatched device',
        );
      },
    );
  });

  group('instructions Ara does not ship', () {
    late NinaImportTranslation t;
    setUp(() => t = _import(_fixture('unsupported')));

    test('become placeholders with role, originalType, payload and reason', () {
      expect(schemaViolations(t.body), isEmpty);
      final byType = {
        for (final n in _ofKind(t, 'unknown')) n['originalType'] as String: n,
      };
      expect(
        byType.keys,
        unorderedEquals([
          'NINA.Plugin.Example.Instructions.ExampleInstruction, NINA.Plugin.Example',
          'NINA.Sequencer.Container.ConditionalContainer, NINA.Sequencer',
          '',
          'NINA.Plugin.Example.Conditions.LoopWhileExample, NINA.Plugin.Example',
        ]),
      );
      expect(
        byType.values.map((n) => n['role']),
        unorderedEquals([
          'instruction',
          'container',
          'instruction',
          'condition',
        ]),
      );
      for (final n in byType.values) {
        expect(n['name'], n['originalType']);
        expect(
          n['reason'],
          isA<String>().having((s) => s.isNotEmpty, 'not empty', isTrue),
        );
        expect(n['originalPayload'], isA<Map>());
      }
    });

    test(
      'a container placeholder keeps its children inside the payload only',
      () {
        final area = _ofKind(
          t,
          'unknown',
        ).firstWhere((n) => n['role'] == 'container');
        expect(area.containsKey('items'), isFalse);
        expect(
          area['originalPayload'],
          _source(_fixture('unsupported'), 'ConditionalContainer'),
        );
        expect(
          _ofKind(t, 'annotation'),
          isEmpty,
          reason: 'the Annotation inside ConditionalContainer is not imported',
        );
        expect(_all(t), hasLength(8));
      },
    );

    test(
      'a node with no \$type is a placeholder with an empty originalType',
      () {
        final typeless = _ofKind(
          t,
          'unknown',
        ).firstWhere((n) => n['originalType'] == '');
        expect((typeless['originalPayload'] as Map)['Name'], 'Mystery');
      },
    );

    test('the warning is the spec sentence, one per placeholder', () {
      expect(
        t.warnings,
        unorderedEquals([
          _placeholderWarning(
            'NINA.Plugin.Example.Instructions.ExampleInstruction, NINA.Plugin.Example',
          ),
          _placeholderWarning(
            'NINA.Sequencer.Container.ConditionalContainer, NINA.Sequencer',
          ),
          _placeholderWarning(''),
          _placeholderWarning(
            'NINA.Plugin.Example.Conditions.LoopWhileExample, NINA.Plugin.Example',
          ),
        ]),
      );
    });

    test('the supported nodes around them still import', () {
      expect(_ofKind(t, 'capture'), hasLength(2));
      expect(_ofKind(t, 'sequential').single['name'], 'Guarded');
    });
  });

  group('detection and refusal', () {
    test('a body that already says openastroara-sequence-v2 is not mapped', () {
      final v2 = {
        'schemaVersion': 'openastroara-sequence-v2',
        'name': 'Already v2',
        'targets': <Object?>[],
        'root': {
          'kind': 'root',
          'items': [
            {'kind': 'capture', 'exposureTime': 30.0},
          ],
        },
      };
      final before = jsonEncode(v2);
      final t = _import(v2);
      expect(jsonEncode(t.body), before);
      expect(t.warnings, isEmpty);
      expect(jsonEncode(v2), before, reason: 'the input is not mutated');
    });

    test(
      'a v1 body is refused (it has a schemaVersion, so it is not NINA)',
      () {
        expect(
          () => translateNinaSequence({
            'schemaVersion': 'openastroara-sequence-v1',
            ..._ninaRoot(),
          }),
          _refusedWith(_refused),
        );
      },
    );

    test(r'a body with no $type and no schemaVersion is refused', () {
      expect(
        () => translateNinaSequence({'Name': 'M42'}),
        _refusedWith(_refused),
      );
      expect(
        () => translateNinaSequence(<String, dynamic>{}),
        _refusedWith(_refused),
      );
    });

    test('a root that is not a container is refused', () {
      expect(
        () => translateNinaSequence({
          r'$type': 'NINA.Sequencer.SequenceItem.Imaging.TakeExposure, NINA.Sequencer',
          'ExposureTime': 60.0,
        }),
        _refusedWith(_refused),
      );
    });

    test('a root that is an unmapped container type is refused', () {
      expect(
        () => translateNinaSequence({
          r'$type':
              'NINA.Sequencer.Container.ConditionalContainer, NINA.Sequencer',
          'Items': {r'$values': <Object?>[]},
        }),
        _refusedWith(_refused),
      );
    });

    test(r'a $ref to a node already seen is refused as a shared node', () {
      final shared = {
        r'$id': '7',
        r'$type':
            'NINA.Sequencer.SequenceItem.Utility.Annotation, NINA.Sequencer',
        'Text': 'once',
      };
      expect(
        () => translateNinaSequence(
          _ninaRoot(
            items: [
              shared,
              {r'$ref': '7'},
            ],
          ),
        ),
        _refusedWith('shared node'),
      );
    });

    test('a child that is not an object refuses the whole import', () {
      expect(
        () => translateNinaSequence(_ninaRoot(items: ['not a node'])),
        _refusedWith(_refused),
      );
    });

    test(
      'a registered container other than the root class is wrapped in a root',
      () {
        final t = _import({
          r'$type':
              'NINA.Sequencer.Container.SequentialContainer, NINA.Sequencer',
          'Name': 'Plain',
          'Items': {
            r'$values': [
              {
                r'$type': 'NINA.Sequencer.SequenceItem.Imaging.TakeExposure, NINA.Sequencer',
                'ExposureTime': 60.0,
              },
            ],
          },
        });
        expect(_root(t)['kind'], 'root');
        expect(t.body['name'], 'Plain');
        expect(_kinds(_root(t)['items']), ['sequential']);
        expect(schemaViolations(t.body), isEmpty);
      },
    );
  });

  group('mapping rules', () {
    test(
      'the NINA and OpenAstroAra namespaces and every assembly form map',
      () {
        final t = _import({
          r'$type': 'OpenAstroAra.Sequencer.Container.SequenceRootContainer, OpenAstroAra.Sequencer',
          'Name': 'Mixed',
          'Items': {
            r'$values': [
              {
                r'$type': 'NINA.Sequencer.SequenceItem.Imaging.TakeExposure, NINA.Sequencer',
                'ExposureTime': 10.0,
              },
              {
                r'$type':
                    'NINA.Sequencer.SequenceItem.Utility.Annotation, NINA',
                'Text': 'legacy assembly',
              },
              {
                r'$type': 'OpenAstroAra.Sequencer.SequenceItem.Utility.Annotation, OpenAstroAra.Sequencer',
                'Text': 'twin',
              },
            ],
          },
        });
        expect(_kinds(_root(t)['items']), [
          'capture',
          'annotation',
          'annotation',
        ]);
        expect(t.warnings, isEmpty);
      },
    );

    test(r'a plain array stands for $values', () {
      final t = _import({
        r'$type':
            'NINA.Sequencer.Container.SequenceRootContainer, NINA.Sequencer',
        'Name': 'Plain array',
        'Items': [
          {
            r'$type': 'NINA.Sequencer.SequenceItem.Telescope.ParkScope, NINA.Sequencer',
          },
        ],
      });
      expect(_kinds(_root(t)['items']), ['park-mount']);
    });

    test('an enum written as a number becomes the v2 token', () {
      Map<String, dynamic> instr(Object behaviour, Object tracking) => {
        r'$type':
            'NINA.Sequencer.SequenceItem.Telescope.SetTracking, NINA.Sequencer',
        'TrackingMode': tracking,
        'ErrorBehavior': behaviour,
      };
      final t = _import(
        _ninaRoot(
          items: [
            instr(0, 0),
            instr(1, 1),
            instr(2, 2),
            instr(3, 3),
            instr('abort-on-error', 4),
            instr(0, 5),
            instr(0, 'Solar'),
          ],
        ),
      );
      final nodes = _ofKind(t, 'set-tracking');
      expect(nodes.map((n) => n['errorBehavior']), [
        'continue-on-error',
        'skip-instruction-set-on-error',
        'abort-on-error',
        'skip-to-sequence-end-instructions',
        'abort-on-error',
        'continue-on-error',
        'continue-on-error',
      ]);
      expect(nodes.map((n) => n['trackingMode']), [
        'Sidereal',
        'Lunar',
        'Solar',
        'King',
        'Custom',
        'Stopped',
        'Solar',
      ]);
    });

    test('the input map is not mutated', () {
      final input = _fixture('target_block');
      final before = jsonEncode(input);
      _import(input);
      expect(jsonEncode(input), before);
    });

    test('there is no glover-exposure in an import', () {
      for (final name in [
        'target_block',
        'smart_exposure',
        'connector_plugin',
      ]) {
        expect(_ofKind(_import(_fixture(name)), 'glover-exposure'), isEmpty);
      }
    });
  });

  group('the area containers of a real NINA file', () {
    late NinaImportTranslation t;
    setUp(() => t = _import(_fixture('area_containers')));

    test('each becomes a sequential with its own name and its children', () {
      final areas = (_root(t)['items'] as List).cast<Map<String, dynamic>>();
      expect(_kinds(areas), ['sequential', 'sequential', 'sequential']);
      expect(areas.map((a) => a['name']), ['Start', 'Targets', 'End']);
      expect(_kinds(areas[0]['items']), ['cool-camera', 'unpark-mount']);
      expect(_kinds(areas[1]['items']), ['target']);
      expect(_kinds(areas[2]['items']), ['warm-camera', 'park-mount']);
    });

    test('the triggers of an area container are imported', () {
      final areas = (_root(t)['items'] as List).cast<Map<String, dynamic>>();
      expect(_kinds(areas[1]['triggers']), ['meridian-flip']);
    });

    test('the file imports with a capture reachable through items', () {
      expect(schemaViolations(t.body), isEmpty);
      expect(hasCapturable(t.body), isTrue);
      expect(_ofKind(t, 'unknown'), isEmpty);
      expect(t.warnings, isEmpty);
    });

    test('the conditions of an area container are imported', () {
      final t = _import(
        _ninaRoot(
          items: [
            {
              r'$type':
                  'NINA.Sequencer.Container.EndAreaContainer, NINA.Sequencer',
              'Name': 'End',
              'Items': {r'$values': <Object?>[]},
              'Conditions': {
                r'$values': [
                  {
                    r'$type': 'NINA.Sequencer.Conditions.LoopCondition, NINA.Sequencer',
                    'Iterations': 2,
                  },
                ],
              },
            },
          ],
        ),
      );
      final end = (_root(t)['items'] as List).single as Map;
      expect(end['kind'], 'sequential');
      expect(_kinds(end['conditions']), ['loop-iterations']);
    });

    test('an area container at the top is wrapped in a root', () {
      final t = _import({
        r'$type': 'NINA.Sequencer.Container.StartAreaContainer, NINA.Sequencer',
        'Name': 'Start',
        'Items': {r'$values': <Object?>[]},
      });
      expect(_kinds(_root(t)['items']), ['sequential']);
    });
  });

  group('the targets', () {
    Map<String, dynamic> dso(String name, [List<Object?> inner = const []]) => {
      r'$type':
          'NINA.Sequencer.Container.DeepSkyObjectContainer, NINA.Sequencer',
      'Name': name,
      'Items': {r'$values': inner},
      'Target': {
        'TargetName': name,
        'InputCoordinates': {'RAHours': 1, 'DecDegrees': 2},
      },
    };

    test('are numbered in tree order, each container naming its own', () {
      final t = _import(
        _ninaRoot(
          items: [
            {
              r'$type': 'NINA.Sequencer.Container.SequentialContainer, NINA.Sequencer',
              'Name': 'Wrapper',
              'Items': {
                r'$values': [
                  dso('A', [dso('B')]),
                ],
              },
            },
            dso('C'),
          ],
        ),
      );
      final targets = (t.body['targets'] as List).cast<Map>();
      expect(targets.map((x) => (x['id'], x['name'])), [
        ('t1', 'A'),
        ('t2', 'B'),
        ('t3', 'C'),
      ]);
      expect(_ofKind(t, 'target').map((n) => (n['name'], n['targetId'])), [
        ('A', 't1'),
        ('B', 't2'),
        ('C', 't3'),
      ]);
      expect(schemaViolations(t.body), isEmpty);
    });
  });

  group('the altitude deprecation shims', () {
    const coordinates = {
      'RAHours': 5,
      'RAMinutes': 35,
      'RASeconds': 17.3,
      'NegativeDec': true,
      'DecDegrees': 5,
      'DecMinutes': 23,
      'DecSeconds': 28.0,
    };
    // kind, the list it sits in, its NINA class
    const classes = [
      (
        'wait-for-sun-altitude',
        'Items',
        'NINA.Sequencer.SequenceItem.Utility.WaitForSunAltitude, NINA.Sequencer',
      ),
      (
        'loop-while-above-horizon',
        'Conditions',
        'NINA.Sequencer.Conditions.AboveHorizonCondition, NINA.Sequencer',
      ),
      (
        'loop-while-altitude',
        'Conditions',
        'NINA.Sequencer.Conditions.AltitudeCondition, NINA.Sequencer',
      ),
    ];

    Map<String, dynamic> data(
      (String, String, String) c,
      Map<String, dynamic> props,
    ) {
      final t = _import(
        _ninaRoot(
          items: [
            if (c.$2 == 'Items') {r'$type': c.$3, ...props},
          ],
          extra: {
            if (c.$2 == 'Conditions')
              'Conditions': {
                r'$values': [
                  {r'$type': c.$3, ...props},
                ],
              },
          },
        ),
      );
      expect(schemaViolations(t.body), isEmpty);
      return _ofKind(t, c.$1).single['data'] as Map<String, dynamic>;
    }

    for (final c in classes) {
      test('fold into data for ${c.$1}', () {
        expect(data(c, {'Altitude': 30.0, 'Comparator': 4}), {
          'offset': 30.0,
          'comparator': 'GreaterThan',
        });
        expect(
          data(c, {'UserSunAltitude': -12.0, 'Comparator': 'LessThanOrEqual'}),
          {'offset': -12.0, 'comparator': 'LessThan'},
        );
        expect(data(c, {'UserMoonAltitude': 5.0, 'AltitudeOffset': 7.0}), {
          'offset': 7.0,
        });
        expect(data(c, {'Coordinates': coordinates}), {
          'coordinates': {
            'raHours': 5,
            'raMinutes': 35,
            'raSeconds': 17.3,
            'negativeDec': true,
            'decDegrees': 5,
            'decMinutes': 23,
            'decSeconds': 28.0,
          },
        });
      });
    }

    test('a shim wins over Data when a file has both', () {
      final c = classes[2];
      expect(
        data(c, {
          'Data': {r'$type': 'x, y', 'Offset': 10.0, 'Comparator': 3},
          'Altitude': 20.0,
          'Comparator': 1,
        }),
        {'offset': 20.0, 'comparator': 'LessThan'},
      );
    });

    test('data.comparator is an engine name; the OrEqual forms fold', () {
      final c = classes[0];
      Object? comparator(Object value) => data(c, {
        'Data': {'Offset': 1.0, 'Comparator': value},
      })['comparator'];
      expect([0, 1, 2, 3, 4, 5].map(comparator), [
        'EQUALS',
        'LessThan',
        'LessThan',
        'GreaterThan',
        'GreaterThan',
        'NotEqual',
      ]);
      expect(comparator('GreaterThanOrEqual'), 'GreaterThan');
    });
  });

  group('the keys of a value', () {
    test('are recased inside list elements and nested coordinates', () {
      final t = _import(
        _ninaRoot(
          items: [
            {
              r'$type': 'NINA.Sequencer.Container.DeepSkyObjectContainer, NINA.Sequencer',
              'Name': 'M42',
              'Items': {r'$values': <Object?>[]},
              'Target': {'TargetName': 'M42', 'InputCoordinates': {}},
              'ExposureInfoList': {
                r'$id': '9',
                r'$type': 'System.Collections.ObjectModel.ObservableCollection`1[[x]]',
                r'$values': [
                  {
                    r'$id': '10',
                    r'$type':
                        'NINA.Sequencer.Utility.ExposureInfo, NINA.Sequencer',
                    'Count': 2,
                    'ExposureTime': 60.0,
                    'Gain': 100,
                    'ImageType': 'DARKFLAT',
                    'BinningX': 1,
                    'BinningY': 1,
                    'ROI': 1.0,
                  },
                ],
              },
            },
            {
              r'$type': 'NINA.Sequencer.SequenceItem.Telescope.SlewScopeToRaDec, NINA.Sequencer',
              'Inherited': false,
              'Coordinates': {
                r'$type': 'NINA.Astrometry.InputCoordinates, NINA.Astrometry',
                'RAHours': 5,
                'RAMinutes': 35,
                'RASeconds': 17.3,
                'NegativeDec': true,
                'DecDegrees': 5,
                'DecMinutes': 23,
                'DecSeconds': 28.0,
              },
            },
          ],
        ),
      );
      expect(_ofKind(t, 'target').single['exposureInfoList'], [
        {
          'count': 2,
          'exposureTime': 60.0,
          'gain': 100,
          'imageType': 'DARK',
          'binningX': 1,
          'binningY': 1,
          'roi': 1.0,
        },
      ]);
      expect(_ofKind(t, 'slew-radec').single['coordinates'], {
        'raHours': 5,
        'raMinutes': 35,
        'raSeconds': 17.3,
        'negativeDec': true,
        'decDegrees': 5,
        'decMinutes': 23,
        'decSeconds': 28.0,
      });
    });

    test('of a shared value are expanded from its \$id', () {
      Map<String, dynamic> capture(Object binning) => {
        r'$type':
            'NINA.Sequencer.SequenceItem.Imaging.TakeExposure, NINA.Sequencer',
        'Binning': binning,
      };
      final t = _import(
        _ninaRoot(
          items: [
            capture({
              r'$id': '5',
              r'$type': 'NINA.Core.Model.Equipment.BinningMode, NINA.Core',
              'X': 2,
              'Y': 2,
            }),
            capture({r'$ref': '5'}),
          ],
        ),
      );
      expect(_ofKind(t, 'capture').map((c) => c['binning']), [
        {'x': 2, 'y': 2},
        {'x': 2, 'y': 2},
      ]);
    });

    test(r'a $ref back into its own value refuses the import', () {
      expect(
        () => translateNinaSequence(
          _ninaRoot(
            items: [
              {
                r'$type': 'NINA.Sequencer.SequenceItem.Telescope.SlewScopeToRaDec, NINA.Sequencer',
                'Coordinates': {
                  r'$id': '8',
                  'RAHours': 1,
                  'Again': {r'$ref': '8'},
                },
              },
            ],
          ),
        ),
        _refusedWith(_refused),
      );
    });

    test(r'a $ref that names no $id refuses the import', () {
      expect(
        () => translateNinaSequence(
          _ninaRoot(
            items: [
              {
                r'$type': 'NINA.Sequencer.SequenceItem.Imaging.TakeExposure, NINA.Sequencer',
                'Binning': {r'$ref': '404'},
              },
            ],
          ),
        ),
        _refusedWith(_refused),
      );
    });
  });

  group('node positions', () {
    test(r'a $ref as the TriggerRunner is a shared node', () {
      expect(
        () => translateNinaSequence(
          _ninaRoot(
            extra: {
              'Triggers': {
                r'$values': [
                  {
                    r'$type': 'NINA.Sequencer.Trigger.Guider.DitherAfterExposures, NINA.Sequencer',
                    'TriggerRunner': {r'$ref': '1'},
                  },
                ],
              },
            },
          ),
        ),
        _refusedWith('shared node'),
      );
    });

    test('a node in a list that does not take its role is a placeholder', () {
      final t = _import(
        _ninaRoot(
          extra: {
            'Conditions': {
              r'$values': [
                {
                  r'$type': 'NINA.Sequencer.SequenceItem.Imaging.TakeExposure, NINA.Sequencer',
                },
              ],
            },
          },
        ),
      );
      expect(_ofKind(t, 'capture'), isEmpty);
      expect(_ofKind(t, 'unknown').single['role'], 'condition');
      expect(schemaViolations(t.body), isEmpty);
    });

    test('a root container nested in the tree is a placeholder', () {
      final t = _import(
        _ninaRoot(
          items: [
            {
              r'$type': 'NINA.Sequencer.Container.SequenceRootContainer, NINA.Sequencer',
              'Name': 'Inner',
            },
          ],
        ),
      );
      expect(_ofKind(t, 'root'), hasLength(1));
      expect(_ofKind(t, 'unknown').single['role'], 'container');
      expect(schemaViolations(t.body), isEmpty);
    });
  });

  group('device ids', () {
    Map<String, dynamic> switchValue(int device) => {
      r'$type':
          'NINA.Sequencer.SequenceItem.Switch.SetSwitchValue, NINA.Sequencer',
      'Value': 1.0,
      'SwitchIndex': 0,
      'AlpacaDeviceNumber': device,
    };

    test('a set-switch-value device number other than -1 warns once', () {
      final t = _import(_ninaRoot(items: [switchValue(3), switchValue(-1)]));
      expect(
        _ofKind(t, 'set-switch-value').map((n) => n['alpacaDeviceNumber']),
        [3, -1],
      );
      expect(t.warnings, [
        'The device "3" of "set-switch-value" is not one of this rig\'s '
            'equipment roles. It was kept as found; assign it in the editor.',
      ]);
    });

    test('a connect-equipment device that is not a rig role warns by name', () {
      final t = _import(
        _ninaRoot(
          items: [
            {
              r'$type': 'NINA.Plugins.Connector.Instructions.ConnectEquipment, NINA.Plugins.Connector',
              'SelectedDevice': 'Example Camera 7',
            },
            {
              r'$type': 'NINA.Plugins.Connector.Instructions.ConnectEquipment, NINA.Plugins.Connector',
              'SelectedDevice': '',
            },
          ],
        ),
      );
      expect(t.warnings, [
        'The device "Example Camera 7" of "connect-equipment" is not one of '
            'this rig\'s equipment roles. It was kept as found; assign it in '
            'the editor.',
      ]);
    });
  });

  group('a provider the table does not name', () {
    test('is left out, so the field takes its default', () {
      final t = _import(
        _ninaRoot(
          items: [
            {
              r'$type': 'NINA.Sequencer.SequenceItem.Utility.WaitForTime, NINA.Sequencer',
              'SelectedProvider': {
                r'$type': 'NINA.Plugin.Example.Provider, NINA.Plugin.Example',
              },
              'Hours': 3,
            },
          ],
        ),
      );
      final wait = _ofKind(t, 'wait-for-time').single;
      expect(wait.containsKey('provider'), isFalse);
      expect(wait['hours'], 3);
    });
  });
}
