import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/nina_sequence_parser.dart';
import 'package:openastroara/models/sequence/sequence_node.dart';

/// A trimmed body mirroring the real NINA export shape: a SequenceRootContainer
/// whose `Items` ($values-wrapped) hold Start/Target/End areas, a DeepSkyObject
/// target, sequential/parallel containers, a SmartExposure (a node with its own
/// children), leaf instructions with scalar params, a Conditions/Triggers block
/// that must NOT become children, and a `$ref` back-reference that must be
/// skipped.
const _ninaBody = r'''
{
  "$id": "1",
  "$type": "NINA.Sequencer.Container.SequenceRootContainer, NINA.Sequencer",
  "schemaVersion": "openastroara-sequence-v1",
  "Name": "M42_Erics",
  "Items": {
    "$type": "...ObservableCollection...",
    "$values": [
      { "$id": "2", "$type": "NINA.Sequencer.Container.StartAreaContainer, NINA.Sequencer", "Name": "Start", "Items": { "$values": [] } },
      {
        "$id": "3",
        "$type": "NINA.Sequencer.Container.TargetAreaContainer, NINA.Sequencer",
        "Name": "Targets",
        "Items": { "$values": [
          {
            "$id": "4",
            "$type": "NINA.Sequencer.Container.DeepSkyObjectContainer, NINA.Sequencer",
            "Name": "M 42",
            "Items": { "$values": [
              {
                "$id": "5",
                "$type": "NINA.Sequencer.Container.SequentialContainer, NINA.Sequencer",
                "Name": "Sequential Instruction Set",
                "Conditions": { "$values": [ { "$type": "...LoopCondition..." } ] },
                "Triggers": { "$values": [ { "$type": "...DitherAfterExposures..." } ] },
                "Items": { "$values": [
                  {
                    "$id": "6",
                    "$type": "NINA.Sequencer.SequenceItem.Imaging.SmartExposure, NINA.Sequencer",
                    "Name": "Smart Exposure",
                    "Items": { "$values": [
                      { "$id": "7", "$type": "NINA.Sequencer.SequenceItem.FilterWheel.SwitchFilter, NINA.Sequencer" },
                      { "$id": "8", "$type": "NINA.Sequencer.SequenceItem.Imaging.TakeExposure, NINA.Sequencer", "ExposureTime": 180.0, "Gain": -1, "ImageType": "LIGHT" }
                    ] }
                  },
                  { "$ref": "7" }
                ] }
              },
              {
                "$id": "9",
                "$type": "NINA.Sequencer.SequenceItem.Camera.CoolCamera, NINA.Sequencer"
              }
            ] }
          }
        ] }
      },
      { "$id": "10", "$type": "NINA.Sequencer.Container.EndAreaContainer, NINA.Sequencer", "Name": "End", "Items": { "$values": [] } }
    ]
  }
}
''';

void main() {
  group('parseNinaSequenceBody', () {
    late SequenceNode root;
    setUp(() {
      root = parseNinaSequenceBody(jsonDecode(_ninaBody) as Map<String, dynamic>);
    });

    test('root keeps the sequence name and the three areas', () {
      expect(root.kind, SequenceNodeKind.root);
      expect(root.displayName, 'M42_Erics');
      expect(root.children.map((c) => c.displayName), ['Start', 'Targets', 'End']);
      expect(root.children.map((c) => c.kind),
          everyElement(SequenceNodeKind.area));
    });

    test('maps the target + container + instruction kinds', () {
      final target = root.children[1].children.single; // Targets → M 42
      expect(target.kind, SequenceNodeKind.target);
      expect(target.displayName, 'M 42');
      // M 42 holds a SequentialContainer and a CoolCamera leaf.
      expect(target.children.length, 2);

      final seq = target.children[0];
      expect(seq.kind, SequenceNodeKind.sequentialContainer);
      // SmartExposure has children, so it's shown as a generic container.
      final smart = seq.children.single;
      expect(smart.kind, SequenceNodeKind.sequentialContainer);
      expect(smart.displayName, 'Smart Exposure');
      // CoolCamera (a leaf) is an instruction with a humanized name.
      final cool = target.children[1];
      expect(cool.kind, SequenceNodeKind.instruction);
      expect(cool.displayName, 'Cool Camera');
      expect(cool.instructionType, 'CoolCamera');
    });

    test('SmartExposure children: leaf + params; the \$ref sibling is skipped', () {
      // Targets → M 42 → SequentialContainer → SmartExposure.
      final smart =
          root.children[1].children.single.children[0].children.single;
      // SwitchFilter + TakeExposure are real children; the { "\$ref": "7" } is not.
      expect(smart.children.map((c) => c.instructionType),
          ['SwitchFilter', 'TakeExposure']);
      final take = smart.children[1];
      expect(take.params['ExposureTime'], 180.0);
      expect(take.params['ImageType'], 'LIGHT');
      // Structural/metadata keys never leak into params.
      expect(take.params.containsKey('Items'), isFalse);
    });

    test('Conditions/Triggers are metadata, not tree children', () {
      final seq = root.children[1].children.single.children[0];
      // Only the SmartExposure is a child — not the condition/trigger blocks.
      expect(seq.children.length, 1);
    });

    test('a flat/non-tree body degrades to an empty named root', () {
      final flat = parseNinaSequenceBody(<String, dynamic>{
        'schemaVersion': 'openastroara-sequence-v1',
        'target': 'M31',
      });
      expect(flat.kind, SequenceNodeKind.root);
      expect(flat.displayName, 'M31');
      expect(flat.children, isEmpty);
    });
  });
}
