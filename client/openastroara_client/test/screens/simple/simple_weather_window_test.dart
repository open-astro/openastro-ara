import 'package:clock/clock.dart';
import 'package:flutter/cupertino.dart' show CupertinoIcons;
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/weather_status.dart';
import 'package:openastroara/screens/simple/simple_device_windows.dart';
import 'package:openastroara/services/equipment_driver_api.dart';
import 'package:openastroara/state/equipment/weather_state.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';
import 'package:openastroara/theme/ara_colors.dart';

class _Weather extends WeatherNotifier {
  _Weather(this.json);
  final Map<String, dynamic> json;

  @override
  Future<WeatherStatus?> build() async => WeatherStatus.fromJson(json);
}

/// Safety policies as the rig has them, without a server to load from.
class _Policies extends SafetyPoliciesNotifier {
  _Policies(this.policies);
  final SafetyPolicies policies;

  @override
  SafetyPolicies build() => policies;
}

Map<String, dynamic> _wire({
  String state = 'connected',
  Duration age = const Duration(seconds: 8),
  Map<String, dynamic> readings = const {
    'cloud_cover_pct': 12.0,
    'sky_quality_mag_arcsec2': 21.34,
    'temperature_c': 8.4,
    'dew_point_c': 6.9,
    'humidity_pct': 88.0,
    'wind_speed_ms': 4.2,
    'wind_gust_ms': 7.5,
    'wind_direction_deg': 315.0,
    'rain_rate': 0.0,
    'pressure_hpa': 1012.0,
  },
}) => {
  'device_id': 'w',
  'name': 'CloudWatcher',
  'state': state,
  'captured_at': clock.now().toUtc().subtract(age).toIso8601String(),
  ...readings,
};

Future<void> _pump(
  WidgetTester tester,
  Map<String, dynamic> wire, {
  bool inSettings = false,
  DeviceDriver? driver,
  SafetyPolicies policies = const SafetyPolicies(
    weatherTriggersEnabled: true,
    maxWindKmh: 30,
    maxHumidityPct: 85,
    minDewDeltaC: 2,
  ),
}) async {
  await tester.pumpWidget(const SizedBox());
  final c = ProviderContainer(
    overrides: [
      weatherProvider.overrideWith(() => _Weather(wire)),
      safetyPoliciesProvider.overrideWith(() => _Policies(policies)),
      deviceDriverProvider.overrideWith(
        (ref, type) async => type == 'observingconditions' ? driver : null,
      ),
    ],
  );
  addTearDown(c.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: c,
      child: MaterialApp(
        home: Scaffold(
          body: Align(
            alignment: Alignment.topLeft,
            child: SizedBox(
              width: 360,
              child: SimpleWeatherWindow(inSettings: inSettings),
            ),
          ),
        ),
      ),
    ),
  );
  await tester.pump();
}

Finder _tile(String id) => find.byKey(Key('simple-weather-$id'));
String _said(WidgetTester t, String id) => t.getSemantics(_tile(id)).label;

void main() {
  testWidgets(
    'a tile for each reading, wind in km/h with its direction and gusts',
    (tester) async {
      await _pump(tester, _wire());
      expect(find.text('Weather · CloudWatcher'), findsOneWidget);
      expect(_said(tester, 'cloud'), startsWith('Cloud cover 12 %'));
      expect(_said(tester, 'sqm'), startsWith('Sky quality 21.34 mag/arcsec²'));
      expect(_said(tester, 'temperature'), startsWith('Temperature 8.4 °C'));
      expect(_said(tester, 'rain'), 'Rain None');
      expect(
        find.textContaining('Wind · NW · gusts 27'),
        findsOneWidget,
        reason: '7.5 m/s is 27 km/h; 315° is NW',
      );
      expect(
        _said(tester, 'wind'),
        startsWith('Wind · NW · gusts 27 15.1 km/h'),
      );
      expect(
        _tile('moon'),
        findsOneWidget,
        reason: 'the moon after the readings',
      );
      expect(
        find.textContaining('Pressure'),
        findsNothing,
        reason: 'full UI only',
      );
    },
  );

  testWidgets('each tile has its icon in front of its value', (tester) async {
    await _pump(tester, _wire());
    for (final (id, icon) in [
      ('cloud', Icons.cloud_outlined),
      ('sqm', Icons.auto_awesome_outlined),
      ('temperature', Icons.thermostat),
      ('dew', Icons.dew_point),
      ('humidity', Icons.water_drop_outlined),
      ('rain', CupertinoIcons.cloud_rain),
      ('wind', Icons.air),
      ('moon', Icons.nightlight_outlined),
    ]) {
      expect(
        find.descendant(of: _tile(id), matching: find.byIcon(icon)),
        findsOneWidget,
        reason: id,
      );
    }
    final humidity = tester.widget<Icon>(
      find.descendant(of: _tile('humidity'), matching: find.byType(Icon)),
    );
    expect(humidity.color, AraColors.accentWarning, reason: 'past its limit');
  });

  testWidgets('every tile is the same height, however long its caption', (
    tester,
  ) async {
    await _pump(tester, _wire());
    final h = tester.getSize(_tile('cloud')).height;
    for (final id in ['sqm', 'dew', 'humidity', 'rain', 'wind', 'moon']) {
      expect(tester.getSize(_tile(id)).height, h, reason: id);
    }
  });

  testWidgets('a reading the station does not report has no tile', (
    tester,
  ) async {
    await _pump(
      tester,
      _wire(readings: {'sky_quality_mag_arcsec2': 21.3, 'humidity_pct': null}),
    );
    expect(_tile('sqm'), findsOneWidget);
    for (final id in [
      'cloud',
      'temperature',
      'dew',
      'humidity',
      'wind',
      'rain',
    ]) {
      expect(_tile(id), findsNothing, reason: id);
    }
  });

  testWidgets('an odd count of tiles: the last one spans the row', (
    tester,
  ) async {
    await _pump(
      tester,
      _wire(readings: {'cloud_cover_pct': 10.0, 'temperature_c': 5.0}),
    );
    // Cloud, Temperature, Moon.
    final cloud = tester.getSize(_tile('cloud')).width;
    expect(tester.getSize(_tile('temperature')).width, cloud);
    expect(tester.getSize(_tile('moon')).width, greaterThan(cloud * 2));
  });

  testWidgets('limits: humidity and dew marked, wind under its limit not', (
    tester,
  ) async {
    await _pump(tester, _wire());
    expect(_said(tester, 'humidity'), 'Humidity 88 %, past the safety limit');
    expect(find.textContaining('Humidity · limit 85 %'), findsOneWidget);
    expect(find.textContaining('Dew point · margin 2 °C'), findsOneWidget);
    expect(_said(tester, 'wind'), isNot(contains('past the safety limit')));
    expect(_said(tester, 'cloud'), isNot(contains('past the safety limit')));
  });

  testWidgets('wind is checked against its gust, as the daemon does', (
    tester,
  ) async {
    await _pump(
      tester,
      _wire(readings: {'wind_speed_ms': 5.0, 'wind_gust_ms': 10.0}),
    );
    expect(
      _said(tester, 'wind'),
      endsWith('past the safety limit'),
      reason: '18 km/h sustained, but 36 km/h gusts are over 30',
    );
  });

  testWidgets('nothing is marked while weather does not trigger safety', (
    tester,
  ) async {
    await _pump(tester, _wire(), policies: const SafetyPolicies());
    expect(find.textContaining('past the safety limit'), findsNothing);
    expect(find.textContaining('limit 85 %'), findsNothing);
  });

  testWidgets('the readings\' age, amber past a minute', (tester) async {
    await _pump(tester, _wire());
    expect(find.text('Updated 8 s ago'), findsOneWidget);
    await tester.pump(const Duration(seconds: 2));
    expect(find.text('Updated 10 s ago'), findsOneWidget, reason: 'counts up');

    await _pump(tester, _wire(age: const Duration(minutes: 4, seconds: 12)));
    final age = tester.widget<Text>(
      find.byKey(const Key('simple-weather-age')),
    );
    expect(age.data, 'Last reading 4 min ago');
    expect(age.style?.color, AraColors.accentWarning);
  });

  testWidgets('a failed read shows no readings', (tester) async {
    await _pump(tester, _wire(state: 'error'));
    expect(find.text('Sensor read failed — check the device.'), findsOneWidget);
    expect(_tile('temperature'), findsNothing);
    expect(find.byKey(const Key('simple-weather-age')), findsNothing);
  });

  testWidgets('disconnected: the state and no readings', (tester) async {
    await _pump(tester, _wire(state: 'disconnected'));
    expect(find.text('Not connected'), findsOneWidget);
    expect(_tile('moon'), findsNothing);
  });

  testWidgets(
    'in Settings it ends with the versions; from the dot it does not',
    (tester) async {
      const driver = DeviceDriver(driverVersion: '7.2.4');
      await _pump(tester, _wire(), driver: driver);
      expect(find.text('Versions'), findsNothing);
      await _pump(tester, _wire(), inSettings: true, driver: driver);
      await tester.pump();
      expect(find.text('Versions'), findsOneWidget);
      expect(find.text('7.2.4'), findsOneWidget);
    },
  );
}
