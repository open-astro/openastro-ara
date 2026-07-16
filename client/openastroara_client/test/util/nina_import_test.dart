import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/util/nina_import.dart';

/// The daemon's NinaImportFidelityTest fixture, verbatim — a structurally
/// faithful NINA export exercising the NINA.* → OpenAstroAra.* remap across
/// Sequencer/Astrometry/Core namespaces and nested containers/triggers.
const _ninaJson = '''
{
  "\$type": "NINA.Sequencer.Container.SequentialContainer, NINA.Sequencer",
  "Strategy": { "\$type": "NINA.Sequencer.Container.ExecutionStrategy.SequentialStrategy, NINA.Sequencer" },
  "Name": "Root",
  "Conditions": [],
  "Items": [
    {
      "\$type": "NINA.Sequencer.Container.DeepSkyObjectContainer, NINA.Sequencer",
      "Target": {
        "\$type": "NINA.Astrometry.InputTarget, NINA.Astrometry",
        "TargetName": "T Cas",
        "PositionAngle": 12.5,
        "InputCoordinates": {
          "\$type": "NINA.Astrometry.InputCoordinates, NINA.Astrometry",
          "RAHours": 0, "RAMinutes": 23, "RASeconds": 14.0,
          "NegativeDec": false, "DecDegrees": 55, "DecMinutes": 47, "DecSeconds": 33.0
        }
      },
      "Items": [
        {
          "\$type": "NINA.Sequencer.SequenceItem.Imaging.TakeExposure, NINA.Sequencer",
          "ExposureTime": 60.0, "Gain": 100, "Offset": 10,
          "Binning": { "\$type": "NINA.Core.Model.Equipment.BinningMode, NINA.Core", "X": 1, "Y": 1 },
          "ImageType": "LIGHT", "ExposureCount": 0
        },
        { "\$type": "NINA.Sequencer.SequenceItem.Exotic.PulseLaser, NINA.Sequencer", "Watts": 5 }
      ]
    }
  ],
  "Triggers": []
}
''';

void main() {
  Map<String, dynamic> fixture() =>
      jsonDecode(_ninaJson) as Map<String, dynamic>;

  test(r'remaps every NINA $type to the OpenAstroAra twin, data verbatim', () {
    final t = translateNinaSequence(fixture());
    expect(t.body[r'$type'],
        'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer');
    final dso = (t.body['Items'] as List).first as Map;
    expect(dso[r'$type'],
        'OpenAstroAra.Sequencer.Container.DeepSkyObjectContainer, OpenAstroAra.Sequencer');
    final target = dso['Target'] as Map;
    expect(target[r'$type'],
        'OpenAstroAra.Astrometry.InputTarget, OpenAstroAra.Astrometry');
    final exposure = (dso['Items'] as List).first as Map;
    expect(exposure[r'$type'],
        'OpenAstroAra.Sequencer.SequenceItem.Imaging.TakeExposure, OpenAstroAra.Sequencer');
    expect((exposure['Binning'] as Map)[r'$type'],
        'OpenAstroAra.Core.Model.Equipment.BinningMode, OpenAstroAra.Core');
    // Everything non-$type survives byte-identical.
    expect(exposure['ExposureTime'], 60.0);
    expect(exposure['Gain'], 100);
    expect(target['TargetName'], 'T Cas');
    expect((target['InputCoordinates'] as Map)['DecMinutes'], 47);
  });

  test('backfills schemaVersion with a warning; keeps an existing one silently',
      () {
    final t = translateNinaSequence(fixture());
    expect(t.body['schemaVersion'], schemaVersion);
    expect(t.warnings.join(' '), contains('schemaVersion was missing'));

    final already = fixture()..['schemaVersion'] = schemaVersion;
    final t2 = translateNinaSequence(already);
    expect(t2.warnings.join(' '),
        isNot(contains('schemaVersion was missing')));
  });

  test('editor-unknown sequencer types are kept as-is but warned about', () {
    final t = translateNinaSequence(fixture());
    // The made-up PulseLaser instruction: remapped string, node untouched,
    // short name surfaced in the warning.
    final dso = (t.body['Items'] as List).first as Map;
    final laser = (dso['Items'] as List)[1] as Map;
    expect(laser['Watts'], 5, reason: 'unknown nodes keep all their data');
    expect(t.warnings.join(' '), contains('PulseLaser'));
    expect(t.warnings.join(' '), contains('not yet supported'));
  });

  test('strategies, conditions and triggers are NOT flagged as unsupported',
      () {
    // Every real NINA export has container Strategy nodes, and most carry
    // conditions/triggers — all first-class in the editor. Flagging them
    // would put a false "not yet supported" warning on essentially every
    // import (the #854 review catch).
    final nina = fixture();
    nina['Conditions'] = [
      {
        r'$type':
            'NINA.Sequencer.Conditions.TimeSpanCondition, NINA.Sequencer',
        'Time': 3600,
      }
    ];
    nina['Triggers'] = [
      {
        r'$type':
            'NINA.Sequencer.Trigger.MeridianFlip.MeridianFlipTrigger, NINA.Sequencer',
      }
    ];
    final t = translateNinaSequence(nina);
    final unsupported =
        t.warnings.where((w) => w.contains('not yet supported')).join(' ');
    // The made-up PulseLaser is the ONLY unsupported type — the fixture's
    // SequentialStrategy plus the condition and trigger all resolve.
    expect(unsupported, contains('PulseLaser'));
    expect(unsupported, isNot(contains('Strategy')));
    expect(unsupported, isNot(contains('TimeSpanCondition')));
    expect(unsupported, isNot(contains('MeridianFlipTrigger')));
    expect(unsupported, contains('1 instruction type(s)'));
  });

  test('legacy single-NINA-assembly form remaps too', () {
    final t = translateNinaSequence({
      r'$type': 'NINA.Sequencer.Container.SequentialContainer, NINA',
      'Items': <dynamic>[],
    });
    expect(t.body[r'$type'],
        'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer');
  });

  test('the input map is not mutated (translation is a copy)', () {
    final input = fixture();
    translateNinaSequence(input);
    expect(input[r'$type'], startsWith('NINA.Sequencer.'));
    expect(input.containsKey('schemaVersion'), isFalse);
  });
}
