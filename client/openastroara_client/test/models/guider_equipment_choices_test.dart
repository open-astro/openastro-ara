import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_equipment_choices.dart';

void main() {
  group('GuiderEquipmentChoices', () {
    test('parses the full snake_case payload', () {
      final c = GuiderEquipmentChoices.fromJson(const {
        'cameras': ['ZWO ASI120MM', 'Simulator'],
        'mounts': ['On-camera', 'INDI Mount'],
        'aux_mounts': ['None', 'Ask'],
        'adaptive_optics': ['None'],
        'rotators': ['None', 'Alpaca Rotator'],
      });
      expect(c.cameras, ['ZWO ASI120MM', 'Simulator']);
      expect(c.mounts, ['On-camera', 'INDI Mount']);
      expect(c.auxMounts, ['None', 'Ask']);
      expect(c.adaptiveOptics, ['None']);
      expect(c.rotators, ['None', 'Alpaca Rotator']);
    });

    test('missing / wrong-typed fields degrade to empty lists', () {
      final c = GuiderEquipmentChoices.fromJson(const {
        'cameras': 'not-a-list',
        'mounts': [1, 'INDI Mount', null],
      });
      expect(c.cameras, isEmpty);
      expect(c.mounts, ['INDI Mount'], reason: 'non-strings are dropped');
      expect(c.auxMounts, isEmpty);
      expect(c.adaptiveOptics, isEmpty);
      expect(c.rotators, isEmpty);
    });

    test('value equality', () {
      final a = GuiderEquipmentChoices.fromJson(const {
        'cameras': ['Simulator'],
      });
      final b = GuiderEquipmentChoices.fromJson(const {
        'cameras': ['Simulator'],
      });
      expect(a, b);
      expect(a.hashCode, b.hashCode);
      expect(a, isNot(const GuiderEquipmentChoices(cameras: ['Other'])));
    });
  });

  group('GuiderEquipmentChoicesResponse', () {
    test('connected envelope carries the choices', () {
      final r = GuiderEquipmentChoicesResponse.fromJson(const {
        'connected': true,
        'choices': {
          'cameras': ['Simulator'],
        },
      });
      expect(r.connected, isTrue);
      expect(r.choices!.cameras, ['Simulator']);
    });

    test('disconnected envelope has null choices', () {
      final r = GuiderEquipmentChoicesResponse.fromJson(const {
        'connected': false,
        'choices': null,
      });
      expect(r.connected, isFalse);
      expect(r.choices, isNull);
    });

    test('malformed envelope degrades to disconnected', () {
      final r = GuiderEquipmentChoicesResponse.fromJson(const {
        'connected': 'yes',
        'choices': 42,
      });
      expect(r.connected, isFalse);
      expect(r.choices, isNull);
    });
  });
}
