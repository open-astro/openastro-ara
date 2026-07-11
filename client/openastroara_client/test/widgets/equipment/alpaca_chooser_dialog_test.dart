import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/wizard/screens/screen_equipment_discovery.dart'
    show equipmentDiscoveryApiFactoryProvider;
import 'package:openastroara/services/equipment_discovery_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/equipment_connection_state.dart'
    show EquipmentDeviceType;
import 'package:openastroara/widgets/equipment/alpaca_chooser_dialog.dart';

class _FakeSavedServerService implements SavedServerService {
  @override
  Future<List<AraServer>> loadAll() async =>
      const [AraServer(hostname: 'h', port: 5555)];
  @override
  Future<void> saveAll(List<AraServer> s) async {}
  @override
  Future<void> add(AraServer server) async {}
}

class _FakeDiscoveryApi extends EquipmentDiscoveryApi {
  _FakeDiscoveryApi(super.server);
  List<DiscoveredDevice> result = const [];

  @override
  Future<List<DiscoveredDevice>> discover(EquipmentDeviceType type,
          {bool forceRefresh = false}) async =>
      result;
}

void main() {
  testWidgets(
      'an empty discovery shows the §68.2 AlpacaBridge guidance, not a bare shrug',
      (tester) async {
    late _FakeDiscoveryApi fake;
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          savedServerServiceProvider
              .overrideWithValue(_FakeSavedServerService()),
          equipmentDiscoveryApiFactoryProvider
              .overrideWithValue((server) => fake = _FakeDiscoveryApi(server)),
        ],
        child: MaterialApp(
          home: Scaffold(
            body: Builder(
              builder: (context) => TextButton(
                onPressed: () => showAlpacaChooserDialog(
                  context,
                  EquipmentDeviceType.mount,
                  deviceTypeLabel: 'Mount',
                ),
                child: const Text('open'),
              ),
            ),
          ),
        ),
      ),
    );
    // Resolve the saved-servers chain so the active server binds.
    final container = ProviderScope.containerOf(
      tester.element(find.text('open')),
    );
    await container.read(savedServersProvider.future);
    await tester.pumpAndSettle();

    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();

    expect(fake.result, isEmpty);
    expect(find.text('No devices found'), findsOneWidget);
    expect(
      find.textContaining('sudo apt install alpaca-bridge'),
      findsOneWidget,
      reason: 'the §68.2 install command must ride the post-setup empty state',
    );
    expect(
      find.textContaining('systemctl status alpaca-bridge'),
      findsOneWidget,
      reason: 'the §68.2 service diagnostic must ride it too',
    );
  });
}
