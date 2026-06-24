import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/instruction_catalog.dart';
import 'package:openastroara/models/sequence/trigger_catalog.dart';

// §38 NINA import fidelity (slice 2b) — RunAutofocus, CenterAndRotate and the
// AutofocusAfterExposures trigger render as first-class catalog entries.

const _runAutofocus =
    'OpenAstroAra.Sequencer.SequenceItem.Autofocus.RunAutofocus, OpenAstroAra.Sequencer';
const _centerAndRotate =
    'OpenAstroAra.Sequencer.SequenceItem.Platesolving.CenterAndRotate, OpenAstroAra.Sequencer';
const _autofocusAfterExposures =
    'OpenAstroAra.Sequencer.Trigger.Autofocus.AutofocusAfterExposures, OpenAstroAra.Sequencer';

void main() {
  test('RunAutofocus is a catalogued leaf instruction', () {
    final def = instructionForType(_runAutofocus);
    expect(def, isNotNull);
    expect(def!.label, 'Run Autofocus');
    expect(def.isContainer, isFalse);
    expect(def.fields, isEmpty);
  });

  test('CenterAndRotate is catalogued with coordinates, position angle and inherited', () {
    final def = instructionForType(_centerAndRotate);
    expect(def, isNotNull);
    expect(def!.label, 'Center and Rotate');
    final keys = def.fields.map((f) => f.key).toSet();
    expect(keys, containsAll(<String>{'Coordinates', 'PositionAngle', 'Inherited'}));
    expect(def.fields.firstWhere((f) => f.key == 'Coordinates').type,
        InstructionFieldType.coordinates);
    expect(def.fields.firstWhere((f) => f.key == 'PositionAngle').type,
        InstructionFieldType.number);
  });

  test('AutofocusAfterExposures is a catalogued trigger with an AfterExposures field', () {
    final def = triggerForType(_autofocusAfterExposures);
    expect(def, isNotNull);
    expect(def!.label, 'Autofocus After Exposures');
    expect(def.fields.single.key, 'AfterExposures');
    expect(def.fields.single.type, InstructionFieldType.integer);
  });
}
