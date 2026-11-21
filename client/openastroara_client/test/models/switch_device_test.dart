import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/switch_device.dart';

void main() {
  group('SwitchDevice.fromJson', () {
    test('parses the daemon snake_case shape incl. device number + ports', () {
      final d = SwitchDevice.fromJson(const <String, dynamic>{
        'device_id': 'pbox:11111:0',
        'alpaca_device_number': 0,
        'name': 'PowerBox',
        'state': 'connected',
        'ports': <Map<String, dynamic>>[
          {'id': 0, 'name': 'Dew A', 'value': 1, 'min': 0, 'max': 1, 'can_write': true},
          {'id': 1, 'name': 'PWM', 'value': 55.5, 'min': 0, 'max': 100, 'can_write': true},
          {'id': 2, 'name': 'Volts', 'value': 12.1, 'min': 0, 'max': 30, 'can_write': false},
        ],
      });
      expect(d.deviceId, 'pbox:11111:0');
      expect(d.alpacaDeviceNumber, 0);
      expect(d.name, 'PowerBox');
      expect(d.connectionState, SwitchConnectionState.connected);
      expect(d.isConnected, isTrue);
      expect(d.ports, hasLength(3));
      expect(d.ports[0].isBoolean, isTrue, reason: 'min 0 / max 1 → boolean port');
      expect(d.ports[1].isBoolean, isFalse, reason: 'a 0..100 range is a value port');
      expect(d.ports[1].value, 55.5);
      expect(d.ports[2].canWrite, isFalse);
    });

    test("reads each port's step; an older daemon without one leaves it unknown (0)", () {
      final d = SwitchDevice.fromJson(const <String, dynamic>{
        'device_id': 'box',
        'alpaca_device_number': 0,
        'state': 'connected',
        'ports': <Map<String, dynamic>>[
          {'id': 0, 'name': 'Light Box', 'value': 40, 'min': 0, 'max': 100, 'can_write': true, 'step': 10},
          {'id': 1, 'name': 'PWM', 'value': 55, 'min': 0, 'max': 100, 'can_write': true},
        ],
      });
      expect(d.ports[0].step, 10);
      expect(d.ports[1].step, 0);
    });

    test('unknown state token + missing ports degrade safely', () {
      final d = SwitchDevice.fromJson(const <String, dynamic>{
        'device_id': 'x',
        'alpaca_device_number': 2,
        'name': 'X',
        'state': 'weird',
      });
      expect(d.connectionState, SwitchConnectionState.unknown);
      expect(d.isConnected, isFalse);
      expect(d.ports, isEmpty);
      expect(d.alpacaDeviceNumber, 2);
    });

    test('disconnected/error states map through', () {
      SwitchConnectionState stateOf(String s) => SwitchDevice.fromJson(
            <String, dynamic>{'alpaca_device_number': 0, 'state': s},
          ).connectionState;
      expect(stateOf('disconnected'), SwitchConnectionState.disconnected);
      expect(stateOf('connecting'), SwitchConnectionState.connecting);
      expect(stateOf('error'), SwitchConnectionState.error);
    });

    test('a missing alpaca_device_number asserts in debug (the address key)', () {
      expect(
        () => SwitchDevice.fromJson(const <String, dynamic>{'state': 'connected'}),
        throwsA(isA<AssertionError>()),
      );
    });
  });

  group('structural equality', () {
    Map<String, dynamic> payload() => <String, dynamic>{
          'device_id': 'sw-0',
          'alpaca_device_number': 0,
          'name': 'A',
          'state': 'connected',
          'ports': <Map<String, dynamic>>[
            {'id': 0, 'name': 'P', 'value': 1, 'min': 0, 'max': 1, 'can_write': true},
          ],
        };

    test('equal payloads compare equal (incl. ports)', () {
      expect(SwitchDevice.fromJson(payload()), SwitchDevice.fromJson(payload()));
      expect(SwitchDevice.fromJson(payload()).hashCode,
          SwitchDevice.fromJson(payload()).hashCode);
    });

    test('a changed port value breaks equality (drives UI rebuilds)', () {
      final changed = payload();
      (changed['ports'] as List).first['value'] = 0;
      expect(SwitchDevice.fromJson(payload()),
          isNot(SwitchDevice.fromJson(changed)));
    });
  });

  group('SwitchPort.isBoolean', () {
    SwitchPort port(double min, double max) =>
        SwitchPort.fromJson(<String, dynamic>{'id': 0, 'min': min, 'max': max});
    test('treats a [0,1] range (with float noise) as boolean', () {
      expect(port(0, 1).isBoolean, isTrue);
      expect(port(0.0000001, 0.9999999).isBoolean, isTrue,
          reason: 'ASCOM float noise on the bounds still reads as boolean');
    });
    test('a real value range is not boolean', () {
      expect(port(0, 100).isBoolean, isFalse);
      expect(port(0, 30).isBoolean, isFalse);
    });
  });

  group('CoolingFan (#1700)', () {
    const designated = CoolingFan(deviceId: 'box', portId: 3);
    SwitchDevice box({
      String name = 'Pegasus UPB',
      SwitchConnectionState state = SwitchConnectionState.connected,
      bool canWrite = true,
    }) =>
        SwitchDevice(
          deviceId: 'box',
          alpacaDeviceNumber: 0,
          name: name,
          connectionState: state,
          ports: [
            SwitchPort(id: 3, name: 'Output', value: 0, min: 0, max: 1, canWrite: canWrite),
          ],
        );

    test('a designated port is the fan whatever the device and port are called', () {
      expect(isCoolingFanPort(box(), box().ports.first, designated), isTrue);
      expect(isCoolingFanPort(box(name: 'Renamed'), box().ports.first, designated), isTrue);
    });

    test('a Thermal Switch Fan port is not the fan unless designated', () {
      final thermal = box(name: 'ToupTek Thermal Switch');
      final fan = SwitchPort(id: 3, name: 'Fan', value: 0, min: 0, max: 1, canWrite: true);
      expect(isCoolingFanPort(thermal, fan, CoolingFan.none), isFalse);
      expect(findCoolingFanPort([thermal], CoolingFan.none), isNull);
    });

    test('another port or another switch is not the fan', () {
      final other = SwitchPort(id: 4, name: 'Output', value: 0, min: 0, max: 1, canWrite: true);
      expect(isCoolingFanPort(box(), other, designated), isFalse);
      expect(
        isCoolingFanPort(box(), box().ports.first, const CoolingFan(deviceId: 'other', portId: 3)),
        isFalse,
      );
    });

    test('findCoolingFanPort skips a disconnected or read-only port', () {
      expect(findCoolingFanPort([box()], designated), isNotNull);
      expect(findCoolingFanPort([box(state: SwitchConnectionState.error)], designated), isNull);
      expect(findCoolingFanPort([box(canWrite: false)], designated), isNull);
    });

    test('a designation goes over the wire and an absent one is none', () {
      expect(CoolingFan.fromJson(designated.toJson()), designated);
      expect(CoolingFan.fromJson(const {}), CoolingFan.none);
      expect(CoolingFan.fromJson(const {'device_id': null, 'port_id': null}).isNone, isTrue);
    });
  });
}
