import 'package:clock/clock.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/safety_monitor_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/weather_status.dart';
import 'package:openastroara/screens/simple/simple_device_windows.dart';
import 'package:openastroara/services/equipment_driver_api.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/equipment/safety_monitor_state.dart';
import 'package:openastroara/state/equipment/weather_state.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';
import 'package:openastroara/theme/ara_colors.dart';

class _Monitor extends SafetyMonitorNotifier {
  _Monitor(this.json);
  final Map<String, dynamic> json;

  @override
  Future<SafetyMonitorStatus?> build() async =>
      SafetyMonitorStatus.fromJson(json);
}

class _Weather extends WeatherNotifier {
  _Weather(this.json);
  final Map<String, dynamic> json;

  @override
  Future<WeatherStatus?> build() async => WeatherStatus.fromJson(json);
}

/// A station with the wind past a 36 km/h limit (41 km/h) and the rest calm.
Map<String, dynamic> _windy({double windMs = 11.4}) => {
  'device_id': 'w',
  'name': 'CloudWatcher',
  'state': 'connected',
  'captured_at': DateTime.now().toUtc().toIso8601String(),
  'wind_speed_ms': windMs,
  'humidity_pct': 60.0,
  'temperature_c': 8.0,
  'dew_point_c': 1.0,
};

/// A rig whose safety policies load as [policies].
class _Api extends ProfileApi {
  _Api(this.policies) : super(const AraServer(hostname: 'test', port: 1));
  final SafetyPolicies? policies;

  @override
  Future<SafetyPolicies> getSafetyPolicies() async =>
      policies ?? (throw StateError('unreachable'));
}

/// 02:14 local, 1 h 12 min before "now".
final _now = DateTime(2026, 11, 8, 3, 26);
final _changed = DateTime(2026, 11, 8, 2, 14);

Map<String, dynamic> _wire({
  String state = 'connected',
  bool safe = true,
  DateTime? changed,
}) => {
  'device_id': 's',
  'name': 'Solo',
  'state': state,
  'safe': safe,
  'last_transition_at': changed?.toUtc().toIso8601String(),
};

Future<void> _pump(
  WidgetTester tester,
  Map<String, dynamic> wire, {
  bool inSettings = false,
  DeviceDriver? driver,
  SafetyPolicies? policies = const SafetyPolicies(),
  Locale? locale,
  Map<String, dynamic>? weather,
}) async {
  await tester.pumpWidget(const SizedBox());
  final c = ProviderContainer(
    overrides: [
      safetyMonitorProvider.overrideWith(() => _Monitor(wire)),
      deviceDriverProvider.overrideWith(
        (ref, type) async => type == 'safetymonitor' ? driver : null,
      ),
      profileApiProvider.overrideWithValue(_Api(policies)),
      if (weather != null)
        weatherProvider.overrideWith(() => _Weather(weather)),
    ],
  );
  addTearDown(c.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: c,
      child: MaterialApp(
        locale: locale,
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          body: Align(
            alignment: Alignment.topLeft,
            child: SizedBox(
              width: 360,
              child: SimpleSafetyMonitorWindow(inSettings: inSettings),
            ),
          ),
        ),
      ),
    ),
  );
  await tester.pump(); // the status
  await tester.pump(); // the policies
}

Color? _colour(WidgetTester t, String text) =>
    t.widget<Text>(find.text(text)).style?.color;

void main() {
  testWidgets('Safe in green, and since when in local 24 h time', (
    tester,
  ) async {
    await withClock(Clock.fixed(_now), () async {
      await _pump(tester, _wire(changed: _changed));
      expect(find.text('Safety monitor · Solo'), findsOneWidget);
      expect(find.text('Safe'), findsOneWidget);
      expect(_colour(tester, 'Safe'), AraColors.accentConnected);
      expect(find.text('since 02:14 · 1 h 12 min'), findsOneWidget);
      expect(
        find.textContaining('Resumes'),
        findsNothing,
        reason: 'what happens next shows only while Unsafe',
      );
    });
  });

  testWidgets('Unsafe in the warning colour, with what happens next', (
    tester,
  ) async {
    await withClock(Clock.fixed(_now), () async {
      await _pump(
        tester,
        _wire(safe: false, changed: _changed),
        policies: const SafetyPolicies(resumeDelayMin: 15),
      );
      expect(find.text('Unsafe'), findsOneWidget);
      expect(_colour(tester, 'Unsafe'), AraColors.accentWarning);
      expect(find.text('Resumes 15 min after Safe'), findsOneWidget);
    });
  });

  testWidgets('Unsafe says the monitor gives no reason, and what Ara does', (
    tester,
  ) async {
    await _pump(tester, _wire(safe: false));
    expect(find.text('Why'), findsOneWidget);
    expect(
      find.text("Solo says unsafe. It doesn't say why: check the device."),
      findsOneWidget,
    );
    expect(find.text('What Ara does'), findsOneWidget);
    expect(
      find.text('On unsafe, Ara pauses the plan, parks and closes the dome.'),
      findsOneWidget,
    );
    expect(find.text('Resumes 10 min after Safe'), findsOneWidget);
  });

  testWidgets(
    'each On unsafe policy says what Ara does; only a pause resumes',
    (tester) async {
      for (final (action, text) in [
        (UnsafeAction.parkOnly, 'On unsafe, Ara parks; the plan carries on.'),
        (UnsafeAction.abortAndPark, 'On unsafe, Ara stops the plan and parks.'),
        (
          UnsafeAction.ignore,
          'On unsafe, Ara does nothing; imaging carries on.',
        ),
      ]) {
        await _pump(
          tester,
          _wire(safe: false),
          policies: SafetyPolicies(onUnsafe: action),
        );
        expect(find.text(text), findsOneWidget, reason: '$action');
        expect(find.textContaining('Resumes'), findsNothing, reason: '$action');
      }
    },
  );

  testWidgets(
    'a weather reading past its limit is a reason too, while weather triggers safety',
    (tester) async {
      await _pump(
        tester,
        _wire(safe: false),
        policies: const SafetyPolicies(weatherTriggersEnabled: true),
        weather: _windy(),
      );
      expect(
        find.text('Wind 41 km/h, over the 36 km/h limit'),
        findsOneWidget,
      );
      await _pump(
        tester,
        _wire(safe: false),
        policies: const SafetyPolicies(weatherTriggersEnabled: false),
        weather: _windy(),
      );
      expect(
        find.textContaining('Wind'),
        findsNothing,
        reason: 'not a reason while weather doesn\'t trigger safety',
      );
    },
  );

  testWidgets(
    'the monitor Safe but the wind past its limit: the card reads Unsafe, the monitor Safe under it',
    (tester) async {
      await _pump(
        tester,
        _wire(),
        policies: const SafetyPolicies(weatherTriggersEnabled: true),
        weather: _windy(),
      );
      // The card says what the rig is, not just the monitor (#1690 audit).
      expect(find.text('Unsafe'), findsOneWidget);
      expect(_colour(tester, 'Unsafe'), AraColors.accentWarning);
      expect(find.text('Solo reads Safe'), findsOneWidget);
      expect(find.textContaining('says unsafe'), findsNothing);
      expect(
        find.text('Wind 41 km/h, over the 36 km/h limit'),
        findsOneWidget,
      );
      expect(find.text('What Ara does'), findsOneWidget);
    },
  );

  testWidgets('humidity and the dew point past their limits read as reasons', (
    tester,
  ) async {
    await _pump(
      tester,
      _wire(safe: false),
      policies: const SafetyPolicies(weatherTriggersEnabled: true),
      weather: {
        ..._windy(windMs: 1),
        'humidity_pct': 90.0,
        'temperature_c': 8.0,
        'dew_point_c': 7.0,
      },
    );
    expect(find.text('Humidity 90 %, over the 85 % limit'), findsOneWidget);
    expect(
      find.text('Dew point 1 °C below the air, under the 2 °C margin'),
      findsOneWidget,
    );
  });

  testWidgets('Safe with the weather calm says nothing more', (tester) async {
    await _pump(
      tester,
      _wire(),
      policies: const SafetyPolicies(weatherTriggersEnabled: true),
      weather: _windy(windMs: 1),
    );
    expect(find.text('Why'), findsNothing);
    expect(find.text('What Ara does'), findsNothing);
  });

  testWidgets('Unsafe without auto-resume stays paused until you resume', (
    tester,
  ) async {
    await withClock(Clock.fixed(_now), () async {
      await _pump(
        tester,
        _wire(safe: false, changed: _changed),
        policies: const SafetyPolicies(autoResumeWhenSafe: false),
      );
      expect(find.text('Stays paused until you resume'), findsOneWidget);
    });
  });

  testWidgets('policies that fail to load say nothing about what is next', (
    tester,
  ) async {
    await _pump(tester, _wire(safe: false), policies: null);
    expect(find.text('Unsafe'), findsOneWidget);
    expect(find.textContaining('Resumes'), findsNothing);
    expect(find.textContaining('Stays paused'), findsNothing);
    expect(find.text('What Ara does'), findsNothing);
    expect(
      find.textContaining('says unsafe'),
      findsOneWidget,
      reason: 'the monitor\'s own word needs no policy',
    );
  });

  testWidgets('no change seen yet leaves the since line out', (tester) async {
    await _pump(tester, _wire());
    expect(find.text('Safe'), findsOneWidget);
    expect(find.textContaining('since'), findsNothing);
  });

  testWidgets('under an hour reads minutes only', (tester) async {
    await withClock(Clock.fixed(_now), () async {
      await _pump(
        tester,
        _wire(changed: _now.subtract(const Duration(minutes: 7))),
      );
      expect(find.text('since 03:19 · 7 min'), findsOneWidget);
    });
  });

  testWidgets('a failed read shows no state, so an old Safe cannot pass', (
    tester,
  ) async {
    await _pump(tester, _wire(state: 'error'));
    expect(find.text('Sensor read failed — check the device.'), findsOneWidget);
    expect(find.text('Safe'), findsNothing);
    expect(find.text('Unsafe'), findsNothing);
  });

  testWidgets('a monitor that isn\'t connected shows only that', (
    tester,
  ) async {
    await _pump(tester, _wire(state: 'disconnected'));
    expect(find.text('Not connected'), findsOneWidget);
    expect(find.text('Safe'), findsNothing);
    expect(find.text('Unsafe'), findsNothing);
  });

  testWidgets('screen readers hear the state and since when as one', (
    tester,
  ) async {
    final handle = tester.ensureSemantics();
    await withClock(Clock.fixed(_now), () async {
      await _pump(tester, _wire(safe: false, changed: _changed));
      expect(
        find.bySemanticsLabel('Unsafe, since 02:14 · 1 h 12 min'),
        findsOneWidget,
      );
    });
    handle.dispose();
  });

  testWidgets('in pt-BR', (tester) async {
    await withClock(Clock.fixed(_now), () async {
      await _pump(
        tester,
        _wire(safe: false, changed: _changed),
        locale: const Locale('pt', 'BR'),
      );
      expect(find.text('Inseguro'), findsOneWidget);
      expect(find.text('desde 02:14 · 1 h 12 min'), findsOneWidget);
      expect(find.text('Retoma 10 min após Seguro'), findsOneWidget);
    });
  });

  testWidgets(
    'in Settings it ends with the monitor\'s versions; from the dot, none',
    (tester) async {
      const driver = DeviceDriver(driverVersion: '1.4');
      await _pump(tester, _wire(), driver: driver);
      expect(find.text('Versions'), findsNothing);
      await _pump(tester, _wire(), driver: driver, inSettings: true);
      await tester.pump();
      expect(find.text('Driver'), findsOneWidget);
      expect(find.text('1.4'), findsOneWidget);
    },
  );
}
