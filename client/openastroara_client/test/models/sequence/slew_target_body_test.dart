import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/instruction_catalog.dart';
import 'package:openastroara/models/sequence/nina_dom.dart';
import 'package:openastroara/models/sequence/slew_target_body.dart';
import 'package:openastroara/util/input_coordinates.dart';

void main() {
  group('buildSlewTargetBody', () {
    test('produces a v1-stamped sequential container root', () {
      final body = buildSlewTargetBody(raDeg: 180, decDeg: 0);
      expect(body['schemaVersion'], sequenceSchemaVersion);
      expect(body[r'$type'], sequentialContainerType);
    });

    test('holds exactly one SlewScopeToRaDec child with the coordinates', () {
      final body = buildSlewTargetBody(raDeg: 180, decDeg: -45);
      final children = childrenOf(body);
      expect(children, hasLength(1));
      final slew = children.single;
      expect(slew[r'$type'], slewScopeToRaDecType);

      final coords = slew['Coordinates'] as Map<String, dynamic>;
      expect(coords[r'$type'], inputCoordinatesType);
      expect(coords['RAHours'], 12); // 180° / 15
      expect(coords['NegativeDec'], true);
      expect(coords['DecDegrees'], 45);
    });

    test('names the root container when a target name is given', () {
      final body =
          buildSlewTargetBody(raDeg: 0, decDeg: 0, targetName: '  M31  ');
      expect(body['Name'], 'M31'); // trimmed
    });

    test('keeps the catalog default name when target name is blank', () {
      final named = instructionForType(sequentialContainerType)!.build();
      final body = buildSlewTargetBody(raDeg: 0, decDeg: 0, targetName: '   ');
      expect(body['Name'], named['Name']);
    });

    test('the slew child keeps the base SequenceItem fields', () {
      final slew = childrenOf(buildSlewTargetBody(raDeg: 0, decDeg: 0)).single;
      // build() emits these for every leaf instruction — a body missing them
      // would not round-trip as a runnable NINA node.
      expect(slew.containsKey('Parent'), isTrue);
      expect(slew['Attempts'], 1);
    });
  });
}
