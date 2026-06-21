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
            <String, dynamic>{'state': s},
          ).connectionState;
      expect(stateOf('disconnected'), SwitchConnectionState.disconnected);
      expect(stateOf('connecting'), SwitchConnectionState.connecting);
      expect(stateOf('error'), SwitchConnectionState.error);
    });
  });
}
