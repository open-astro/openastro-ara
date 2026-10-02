import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/server_discovery_service.dart';
import 'package:openastroara/util/friendly_error.dart';
import 'package:openastroara/util/gps_site_fill.dart';

void main() {
  // #1129, seen on a Pixel Tablet: the unreachable-rig copy said "this computer".
  test('an unreachable rig names the device the client runs on', () {
    final msg = friendlyError(const SocketException('No route to host'));
    expect(msg, contains('same network as ${thisDeviceLabel(clientPlatform)}.'));
  });

  // #1129: the rig list showed the whole service name.
  test('the rig list shows the mDNS instance name, not the service name', () {
    expect(ServerDiscoveryService.instanceName('openastro._openastroara._tcp.local'), 'openastro');
    expect(ServerDiscoveryService.instanceName('my rig._openastroara._tcp.local'), 'my rig');
    expect(ServerDiscoveryService.instanceName('odd-name'), 'odd-name', reason: 'unknown shape kept as is');
    expect(ServerDiscoveryService.instanceName('._openastroara._tcp.local'), '._openastroara._tcp.local',
        reason: 'never an empty name');
  });
}
