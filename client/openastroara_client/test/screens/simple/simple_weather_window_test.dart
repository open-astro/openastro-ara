import 'dart:io';
import 'dart:typed_data' show ByteData;

import 'package:clock/clock.dart';
import 'package:flutter/cupertino.dart' show CupertinoIcons;
import 'package:flutter/material.dart';
import 'package:flutter/services.dart' show FontLoader;
import 'package:flutter/rendering.dart' show RenderParagraph;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/weather_status.dart';
import 'package:openastroara/screens/simple/simple_device_windows.dart';
import 'package:openastroara/services/equipment_driver_api.dart';
import 'package:openastroara/state/equipment/weather_state.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/widgets/equipment/equipment_glyphs.dart';

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
  ProfileApi? api,
  Locale? locale,
  double width = 360,
  String? font,
}) async {
  await tester.pumpWidget(const SizedBox());
  final c = ProviderContainer(
    overrides: [
      weatherProvider.overrideWith(() => _Weather(wire)),
      safetyPoliciesProvider.overrideWith(() => _Policies(policies)),
      deviceDriverProvider.overrideWith(
        (ref, type) async => type == 'observingconditions' ? driver : null,
      ),
      profileApiProvider.overrideWithValue(api),
    ],
  );
  addTearDown(c.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: c,
      child: MaterialApp(
        locale: locale,
        theme: ThemeData(fontFamily: font),
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          body: Align(
            alignment: Alignment.topLeft,
            child: SizedBox(
              width: width,
              child: SimpleWeatherWindow(inSettings: inSettings),
            ),
          ),
        ),
      ),
    ),
  );
  await tester.pump();
}

/// A rig whose safety policies can't be read.
class _NoPolicies extends ProfileApi {
  _NoPolicies() : super(const AraServer(hostname: 'test', port: 1));

  @override
  Future<SafetyPolicies> getSafetyPolicies() async =>
      throw StateError('unreachable');
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
      expect(
        _said(tester, 'sqm'),
        'Sky quality 21.34 mag/arcsec²',
        reason: 'the value next to the name, then its unit',
      );
      expect(
        find.textContaining('Sky quality\nmag/arcsec²'),
        findsOneWidget,
        reason: 'the long unit on its own line under the name, whole',
      );
      expect(_said(tester, 'temperature'), startsWith('Temperature 8.4 °C'));
      expect(
        _said(tester, 'rain'),
        'Rain 0 mm/h',
        reason:
            'a number, not a word: "Nenhuma" did not fit a narrow half tile',
      );
      expect(
        find.textContaining('Wind · NW · gusts 27'),
        findsOneWidget,
        reason: '7.5 m/s is 27 km/h; 315° is NW',
      );
      expect(
        _said(tester, 'wind'),
        'Wind 15.1 km/h, NW, gusts 27 km/h',
        reason: 'the value next to the name, the gust with its unit',
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
    expect(
      find.descendant(of: _tile('dew'), matching: find.byType(EquipmentGlyph)),
      findsOneWidget,
      reason: 'a leaf with a drop: Material\'s dew point mark reads as a thermometer',
    );
    expect(
      tester
          .widget<EquipmentGlyph>(
            find.descendant(
              of: _tile('dew'),
              matching: find.byType(EquipmentGlyph),
            ),
          )
          .color,
      AraColors.accentWarning,
      reason: 'past its margin, like the number',
    );
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
    expect(
      _said(tester, 'humidity'),
      'Humidity 88 %, past the safety limit (limit 85 %)',
    );
    expect(
      find.descendant(of: _tile('humidity'), matching: find.text('limit 85 %')),
      findsOneWidget,
      reason: 'the limit on its own line, never cut off by the caption',
    );
    expect(
      find.descendant(of: _tile('dew'), matching: find.text('margin 2 °C')),
      findsOneWidget,
    );
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
      contains('past the safety limit'),
      reason: '18 km/h sustained, but 36 km/h gusts are over 30',
    );
  });

  testWidgets('a gust past the limit: the gust reads amber, not the lower '
      'sustained speed', (tester) async {
    await _pump(
      tester,
      _wire(readings: {'wind_speed_ms': 5.0, 'wind_gust_ms': 10.0}),
    );
    final number = tester.widget<Text>(
      find.descendant(of: _tile('wind'), matching: find.byType(Text)).first,
    );
    expect(number.textSpan!.toPlainText(), '18 km/h');
    expect(number.style?.color, isNot(AraColors.accentWarning));
    final caption = tester
        .widgetList<Text>(
          find.descendant(of: _tile('wind'), matching: find.byType(Text)),
        )
        .firstWhere(
          (t) => t.textSpan?.toPlainText().contains('gusts') ?? false,
        );
    TextSpan? gust;
    caption.textSpan!.visitChildren((span) {
      if (span is TextSpan && span.text == 'gusts 36') gust = span;
      return gust == null;
    });
    expect(gust?.style?.color, AraColors.accentWarning);
    expect(
      find.descendant(of: _tile('wind'), matching: find.text('limit 30 km/h')),
      findsOneWidget,
    );
  });

  testWidgets('sustained wind past the limit: the number reads amber', (
    tester,
  ) async {
    await _pump(
      tester,
      _wire(readings: {'wind_speed_ms': 10.0, 'wind_gust_ms': 12.0}),
    );
    final number = tester.widget<Text>(
      find.descendant(of: _tile('wind'), matching: find.byType(Text)).first,
    );
    expect(number.style?.color, AraColors.accentWarning);
  });

  testWidgets('a NaN wind reading marks nothing, as the daemon\'s Math.Max', (
    tester,
  ) async {
    await _pump(
      tester,
      _wire(readings: {'wind_speed_ms': double.nan, 'wind_gust_ms': 20.0}),
    );
    expect(_said(tester, 'wind'), isNot(contains('past the safety limit')));
  });

  testWidgets(
    'safety limits that fail to load say so instead of looking calm',
    (tester) async {
      await _pump(tester, _wire(), api: _NoPolicies());
      await tester.pump();
      expect(
        find.text('Safety limits not loaded: nothing is marked.'),
        findsOneWidget,
      );
    },
  );

  // The test font draws every glyph as a square; fit is measured with the
  // Roboto the Flutter SDK ships (as simple_text_scale_test does).
  final fonts =
      '${Platform.environment['FLUTTER_ROOT']}/bin/cache/artifacts/material_fonts';
  final hasFonts = File('$fonts/Roboto-Regular.ttf').existsSync();
  testWidgets(
    'narrow and in Portuguese, the limit is not cut off',
    skip: !hasFonts,
    (tester) async {
      final roboto = FontLoader('Roboto');
      for (final f in ['Roboto-Regular.ttf', 'Roboto-Medium.ttf']) {
        roboto.addFont(
          Future.value(
            ByteData.view(File('$fonts/$f').readAsBytesSync().buffer),
          ),
        );
      }
      await roboto.load();
      await _pump(
        tester,
        _wire(
          readings: {
            'wind_speed_ms': 12.5,
            'wind_gust_ms': 15.0,
            'wind_direction_deg': 315.0,
            'humidity_pct': 90.0,
          },
        ),
        locale: const Locale('pt', 'BR'),
        width: 280,
        font: 'Roboto',
      );
      for (final limit in ['limite 30 km/h', 'limite 85 %']) {
        final p = tester.renderObject<RenderParagraph>(find.text(limit));
        expect(p.didExceedMaxLines, isFalse, reason: limit);
      }
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    'narrow and in Portuguese, a gust past the limit is not cut off',
    skip: !hasFonts,
    (tester) async {
      final roboto = FontLoader('Roboto');
      for (final f in ['Roboto-Regular.ttf', 'Roboto-Medium.ttf']) {
        roboto.addFont(
          Future.value(
            ByteData.view(File('$fonts/$f').readAsBytesSync().buffer),
          ),
        );
      }
      await roboto.load();
      // 22 km/h sustained (under 30), 54 km/h gusts (over): the gust is
      // the amber part of the caption.
      await _pump(
        tester,
        _wire(
          readings: {
            'wind_speed_ms': 6.0,
            'wind_gust_ms': 15.0,
            'wind_direction_deg': 315.0,
          },
        ),
        locale: const Locale('pt', 'BR'),
        width: 280,
        font: 'Roboto',
      );
      final caption = tester.renderObject<RenderParagraph>(
        find.descendant(
          of: _tile('wind'),
          matching: find.textContaining('rajadas 54', findRichText: true),
        ),
      );
      expect(caption.didExceedMaxLines, isFalse);
      expect(
        _said(tester, 'wind'),
        contains('NO'),
        reason: 'the compass point is still read out',
      );
    },
  );

  for (final scale in [1.3, 2.0]) {
    testWidgets('at ${scale}x text nothing overflows', skip: !hasFonts, (
      tester,
    ) async {
      final roboto = FontLoader('Roboto');
      for (final f in ['Roboto-Regular.ttf', 'Roboto-Medium.ttf']) {
        roboto.addFont(
          Future.value(
            ByteData.view(File('$fonts/$f').readAsBytesSync().buffer),
          ),
        );
      }
      await roboto.load();
      tester.platformDispatcher.textScaleFactorTestValue = scale;
      addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
      // The window grows with the text, as the shell sizes it.
      await _pump(
        tester,
        _wire(
          readings: {
            'wind_speed_ms': 5.0,
            'wind_gust_ms': 10.0,
            'wind_direction_deg': 315.0,
            'humidity_pct': 90.0,
            'temperature_c': 8.0,
            'dew_point_c': 7.0,
          },
        ),
        width: 360 * (scale / 1.1).clamp(1, 2) - 32,
        font: 'Roboto',
      );
      expect(tester.takeException(), isNull);
      for (final limit in ['limit 30 km/h', 'limit 85 %', 'margin 2 °C']) {
        final p = tester.renderObject<RenderParagraph>(find.text(limit));
        expect(p.didExceedMaxLines, isFalse, reason: limit);
      }
    });
  }

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
