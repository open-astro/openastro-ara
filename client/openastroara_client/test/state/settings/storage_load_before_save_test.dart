import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/settings/panels/storage_panel.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/panel_save_registry.dart';
import 'package:openastroara/state/settings/storage_settings_state.dart';

class _Api extends ProfileApi {
  _Api() : super(const AraServer(hostname: 'test', port: 1));
  final puts = <StorageSettings>[];
  @override
  Future<StorageSettings> getStorageSettings() async =>
      throw Exception('rig unreachable');
  @override
  Future<StorageSettings> putStorageSettings(StorageSettings value) async {
    puts.add(value);
    return value;
  }
}

void main() {
  testWidgets('Save with the Storage load failed writes nothing (#1807)',
      (tester) async {
    tester.view.physicalSize = const Size(1400, 4000);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    final prior = FlutterError.onError;
    FlutterError.onError = (d) {
      if (!d.exceptionAsString().contains('overflowed')) prior?.call(d);
    };
    addTearDown(() => FlutterError.onError = prior);
    final api = _Api();
    final container = ProviderContainer(
        overrides: [profileApiProvider.overrideWithValue(api)]);
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: StoragePanel())),
    ));
    await tester.pumpAndSettle();
    await container.read(panelSaveActionProvider)!();
    await tester.pumpAndSettle();
    expect(api.puts, isEmpty);
  });
}
