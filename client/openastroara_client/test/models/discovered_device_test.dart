import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/state/settings/equipment_connection_state.dart';

void main() {
  group('DiscoveredDevice.fromJson', () {
    test('parses the daemon snake_case payload (the real wire shape)', () {
      // The daemon serializes DiscoveredDeviceDto with SnakeCaseLower, so the keys
      // are snake_case. Reading camelCase here used to drop every field to its
      // default — a blank, unconnectable device — which broke discovery.
      final d = DiscoveredDevice.fromJson(const <String, dynamic>{
        'unique_id': 'dome-host:11111:2',
        'name': 'NexDome',
        'type': 'dome',
        'host_name': 'domepi.local',
        'ip_address': '192.168.1.50',
        'ip_port': 11111,
        'alpaca_device_number': 2,
        'use_https': false,
      });
      expect(d.uniqueId, 'dome-host:11111:2');
      expect(d.name, 'NexDome');
      expect(d.deviceType, EquipmentDeviceType.dome);
      expect(d.hostName, 'domepi.local');
      expect(d.ipAddress, '192.168.1.50');
      expect(d.ipPort, 11111);
      expect(d.alpacaDeviceNumber, 2);
      expect(d.useHttps, isFalse);
    });

    test('parses a camelCase payload too (defensive fallback)', () {
      final d = DiscoveredDevice.fromJson(const <String, dynamic>{
        'uniqueId': 'cam-1',
        'name': 'ASI2600',
        'type': 'camera',
        'hostName': 'cam.local',
        'ipAddress': '10.0.0.2',
        'ipPort': 32323,
        'alpacaDeviceNumber': 0,
        'useHttps': true,
      });
      expect(d.uniqueId, 'cam-1');
      expect(d.deviceType, EquipmentDeviceType.camera);
      expect(d.ipPort, 32323);
      expect(d.useHttps, isTrue);
    });

    test('missing keys fall back to safe defaults', () {
      final d = DiscoveredDevice.fromJson(const <String, dynamic>{
        'type': 'focuser',
      });
      expect(d.uniqueId, '');
      expect(d.name, '');
      expect(d.alpacaDeviceNumber, 0);
      expect(d.useHttps, isFalse);
      expect(d.deviceType, EquipmentDeviceType.focuser);
    });
  });
}
