import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/weather_status.dart';
import 'package:openastroara/screens/settings/panels/equipment_weather_panel.dart';
import 'package:openastroara/services/equipment_device_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/equipment/weather_state.dart';
import 'package:openastroara/state/saved_server_state.dart';

class _FakeSavedServerService implements SavedServerService {
  _FakeSavedServerService(this._stored);
  final List<AraServer> _stored;
  @override
  Future<List<AraServer>> loadAll() async => List.unmodifiable(_stored);
  @override
  Future<void> saveAll(List<AraServer> servers) async {}
  @override
  Future<void> add(AraServer server) async {}
}

class _FakeWeatherApi implements EquipmentDeviceClient<WeatherStatus> {
  _FakeWeatherApi(this.status);
  WeatherStatus? status;
  final List<String> calls = [];
  @override
  Future<WeatherStatus?> getStatus() async => status;
  @override
  Future<void> connect(DiscoveredDevice device) async =>
      calls.add('connect:${device.name}');
  @override
  Future<void> disconnect() async => calls.add('disconnect');
  @override
  void close() {}
}

WeatherStatus _status({
  EquipmentConnectionState state = EquipmentConnectionState.connected,
  double? temperature = 4.5,
  double? dewPoint = -0.5,
  double? windGust = 6.1,
}) =>
    WeatherStatus(
      deviceId: 'oc-0',
      name: 'CloudWatcher',
      connectionState: state,
      temperatureC: temperature,
      humidityPct: 72,
      dewPointC: dewPoint,
      pressureHpa: 1013,
      cloudCoverPct: 20,
      windSpeedMs: 3.2,
      windGustMs: windGust,
      windDirectionDeg: 180,
      rainRate: 0,
      capturedAt: DateTime.utc(2026, 6, 22, 6),
    );

Future<void> _wideSurface(WidgetTester tester) async {
  await tester.binding.setSurfaceSize(const Size(1200, 1000));
  addTearDown(() => tester.binding.setSurfaceSize(null));
}

Future<_FakeWeatherApi> _pump(WidgetTester tester, WeatherStatus? status) async {
  await _wideSurface(tester);
  final api = _FakeWeatherApi(status);
  await tester.pumpWidget(ProviderScope(
    overrides: [
      savedServerServiceProvider.overrideWithValue(
          _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
      weatherApiFactoryProvider.overrideWithValue((_) => api),
    ],
    child: const MaterialApp(home: Scaffold(body: EquipmentWeatherPanel())),
  ));
  await tester.pumpAndSettle();
  return api;
}

void main() {
  testWidgets('renders the live sensors of a connected station', (tester) async {
    await _pump(tester, _status());
    expect(find.text('CloudWatcher'), findsOneWidget);
    expect(find.text('Connected'), findsOneWidget);
    expect(find.text('Temperature'), findsOneWidget);
    expect(find.text('Dew point'), findsOneWidget); // newly-added sensor
    expect(find.text('Wind gust'), findsOneWidget); // newly-added sensor
    expect(find.textContaining('UTC'), findsOneWidget); // captured-at line
  });

  testWidgets('omits sensors the device does not implement', (tester) async {
    await _pump(
        tester, _status(dewPoint: null, windGust: null, temperature: 10));
    expect(find.text('Temperature'), findsOneWidget);
    expect(find.text('Dew point'), findsNothing);
    expect(find.text('Wind gust'), findsNothing);
  });

  testWidgets('an error sub-state shows a distinct message, not "no sensors"',
      (tester) async {
    await _pump(
        tester,
        _status(
            state: EquipmentConnectionState.error,
            temperature: null,
            dewPoint: null,
            windGust: null));
    expect(find.text('Error'), findsOneWidget); // the chip
    expect(find.text('Sensor read failed — check the device.'), findsOneWidget);
    expect(find.text('This weather station reports no sensors.'), findsNothing);
  });

  testWidgets('no device connected shows the empty state + Connect…',
      (tester) async {
    await _pump(tester, null);
    expect(find.text('No weather station connected.'), findsOneWidget);
    expect(find.widgetWithText(TextButton, 'Connect…'), findsOneWidget);
  });

  testWidgets('disconnect targets the device', (tester) async {
    final api = await _pump(tester, _status());
    await tester.tap(find.byIcon(Icons.link_off));
    await tester.pumpAndSettle();
    expect(api.calls, contains('disconnect'));
  });
}
