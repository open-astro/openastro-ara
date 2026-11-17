import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/alpacabridge_setup.dart';

import '../support/alpacabridge_setup_fakes.dart';

void main() {
  test('a driver reads AlpacaBridge\'s camelCase keys as they are sent', () {
    final d = zwoCameraDriver(
      extraFields: [
        {
          'key': 'usbBandwidth',
          'type': 'int',
          'default': 40,
          'required': false,
          'secret': false,
          'role': 'Plain',
          'range': {'min': 0, 'max': 100},
        },
      ],
    );
    expect((d.vendor, d.deviceType, d.available), ('zwo', 'camera', true));
    expect(d.key, 'zwo-camera');
    final bw = d.fields.last;
    expect(
      (bw.key, bw.type, bw.defaultValue, bw.min, bw.max),
      ('usbBandwidth', BridgeFieldType.integer, 40, 0, 100),
    );
  });

  test(
    'every type AlpacaBridge sends maps, and an unknown one is not dropped',
    () {
      final types = {
        'bool': BridgeFieldType.boolean,
        'int': BridgeFieldType.integer,
        'double': BridgeFieldType.number,
        'string': BridgeFieldType.string,
        'stringList': BridgeFieldType.stringList,
        'recordList': BridgeFieldType.recordList,
        'futuretype': BridgeFieldType.unknown,
      };
      for (final e in types.entries) {
        final d = BridgeDriver.fromJson({
          'vendor': 'v',
          'deviceType': 'camera',
          'displayName': 'x',
          'vendorLabel': 'V',
          'available': true,
          'fields': [
            {
              'key': 'k',
              'type': e.key,
              'required': false,
              'secret': false,
              'role': 'Plain',
            },
          ],
        });
        expect(d.fields.single.type, e.value, reason: e.key);
      }
    },
  );

  test('the catalog envelope flattens its device types, in order', () {
    final drivers = BridgeDriver.listFromCatalogJson({
      'device_types': [
        {
          'device_type': 'camera',
          'drivers': [zwoCameraDriverJson()],
        },
        {
          'device_type': 'switch',
          'drivers': [
            {
              'vendor': 'zwo',
              'deviceType': 'switch',
              'displayName': 's',
              'vendorLabel': 'ZWO',
              'available': true,
              'fields': [],
            },
          ],
        },
      ],
    });
    expect(drivers.map((d) => d.key), ['zwo-camera', 'zwo-switch']);
    expect(BridgeDriver.listFromCatalogJson({'device_types': []}), isEmpty);
    expect(
      BridgeDriver.listFromCatalogJson(const <String, Object?>{}),
      isEmpty,
      reason: 'absent key reads as none',
    );
  });
}
