import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/instruction_catalog.dart';
import 'package:openastroara/models/sequence/node_display.dart';
import 'package:openastroara/models/sequence/trigger_catalog.dart';

// §38 NINA import fidelity (slice 1c) — the three high-frequency types from #591/#592 now have
// first-class catalog entries, so an imported sequence renders with real labels/icons instead of
// generic fallbacks.

const _dso =
    'OpenAstroAra.Sequencer.Container.DeepSkyObjectContainer, OpenAstroAra.Sequencer';
const _smartExposure =
    'OpenAstroAra.Sequencer.SequenceItem.Imaging.SmartExposure, OpenAstroAra.Sequencer';
const _dither =
    'OpenAstroAra.Sequencer.Trigger.Guider.DitherAfterExposures, OpenAstroAra.Sequencer';

void main() {
  group('NINA import catalog entries', () {
    test('DeepSkyObjectContainer is a catalogued container', () {
      final def = instructionForType(_dso);
      expect(def, isNotNull);
      expect(def!.label, 'Deep Sky Object');
      expect(def.isContainer, isTrue, reason: 'DSO must render as a container (has a strategy)');
    });

    test('SmartExposure is a catalogued container', () {
      final def = instructionForType(_smartExposure);
      expect(def, isNotNull);
      expect(def!.label, 'Smart Exposure');
      expect(def.isContainer, isTrue);
    });

    test('DitherAfterExposures is a catalogued trigger with an AfterExposures field', () {
      final def = triggerForType(_dither);
      expect(def, isNotNull);
      expect(def!.label, 'Dither After Exposures');
      final field = def.fields.singleWhere((f) => f.key == 'AfterExposures');
      expect(field.type, InstructionFieldType.integer);
    });
  });

  group('Deep Sky Object label', () {
    Map<String, dynamic> dsoNode({String? name, String? targetName}) => {
          r'$type': _dso,
          'Strategy': {
            r'$type':
                'OpenAstroAra.Sequencer.Container.ExecutionStrategy.SequentialStrategy, OpenAstroAra.Sequencer'
          },
          'Name': ?name,
          'Items': {r'$values': <dynamic>[]},
          'Target': {
            r'$type': 'OpenAstroAra.Astrometry.InputTarget, OpenAstroAra.Astrometry',
            'TargetName': ?targetName,
          },
        };

    test('falls back to the target name when the container has no Name', () {
      expect(nodeLabel(dsoNode(targetName: 'T Cas')), 'T Cas');
    });

    test('a user-given Name still wins over the target', () {
      expect(nodeLabel(dsoNode(name: 'My Target Block', targetName: 'T Cas')), 'My Target Block');
    });

    test('falls back to the catalog label when neither Name nor target name is set', () {
      expect(nodeLabel(dsoNode()), 'Deep Sky Object');
    });
  });
}
