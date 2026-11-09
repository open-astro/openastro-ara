import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_advanced_settings.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/guider_advanced_api.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/widgets/imaging/guider_advanced_dialog.dart';

class _Fake implements GuiderAdvancedClient {
  GuiderAdvancedSettings profile;
  bool failProfileRead = false;
  final GuiderAdvancedView guider;
  Map<String, dynamic>? putBody;
  int pushes = 0;
  int multiStarApplies = 0;
  Object? applyError;
  _Fake(this.profile, this.guider);
  @override
  Future<GuiderAdvancedSettings> getProfile() async {
    if (failProfileRead) throw Exception('profile read timed out');
    return profile;
  }

  @override
  Future<GuiderAdvancedSettings> putProfile(GuiderAdvancedSettings s) async {
    putBody = s.toJson();
    profile = s;
    return s;
  }

  @override
  Future<GuiderAdvancedView> getGuider() async => guider;
  @override
  Future<void> pushTuning() async => pushes++;
  @override
  Future<void> applyMultiStar() async {
    if (applyError != null) throw applyError!;
    multiStarApplies++;
  }

  @override
  void close() {}
}

GuiderAdvancedView _guider() => GuiderAdvancedView.fromJson({
  'connected': true,
  'current': {
    'min_star_snr': 6.0,
    'multi_star_enabled': true,
    'ra_algorithm': 'Hysteresis',
    'max_dec_duration_ms': 2500,
  },
  'ra_algorithms': ['Hysteresis', 'Predictive PEC'],
  'dec_algorithms': ['Resist Switch', 'Lowpass'],
});

Future<_Fake> _pump(
  WidgetTester t, {
  GuiderAdvancedSettings profile = GuiderAdvancedSettings.empty,
  GuiderAdvancedView? guider,
}) async {
  final fake = _Fake(profile, guider ?? _guider());
  t.view.physicalSize = const Size(900, 2400);
  t.view.devicePixelRatio = 1;
  addTearDown(t.view.resetPhysicalSize);
  addTearDown(t.view.resetDevicePixelRatio);
  await t.pumpWidget(
    ProviderScope(
      overrides: [
        activeServerProvider.overrideWithValue(
          const AraServer(hostname: 'localhost', port: 5555),
        ),
        guiderAdvancedApiFactoryProvider.overrideWithValue((_) => fake),
      ],
      child: const MaterialApp(
        home: Scaffold(
          body: SizedBox(
            width: 800,
            height: 2300,
            child: GuiderAdvancedPanel(),
          ),
        ),
      ),
    ),
  );
  await t.pumpAndSettle();
  return fake;
}

void main() {
  test('settings parse only known keys of the right type, and toJson carries every key', () {
    final s = GuiderAdvancedSettings.fromJson({
      'min_star_snr': 7,
      'search_region_px': 15.0,
      'multi_star_enabled': false,
      'noise_reduction': '2x2 Mean',
      'ra_algorithm': '',
      'unknown': 1,
      'max_ra_duration_ms': 'x',
    });
    expect(s['min_star_snr'], 7.0);
    expect(s['search_region_px'], 15);
    expect(s['multi_star_enabled'], false);
    expect(s['noise_reduction'], '2x2 Mean');
    expect(
      s.values.containsKey('ra_algorithm'),
      isFalse,
      reason: 'empty name = unset',
    );
    expect(s.values.containsKey('max_ra_duration_ms'), isFalse);
    final json = s.toJson();
    expect(json.keys, hasLength(GuiderAdvancedField.all.length));
    expect(json['max_dec_duration_ms'], isNull);
    expect(
      s.withValue('min_star_snr', null).values.containsKey('min_star_snr'),
      isFalse,
    );
  });

  test('number fields parse to positive values or explain why not', () {
    final snr = GuiderAdvancedField.all.firstWhere(
      (f) => f.key == 'min_star_snr',
    );
    final ms = GuiderAdvancedField.all.firstWhere(
      (f) => f.key == 'max_ra_duration_ms',
    );
    expect(GuiderAdvancedPanelTestHooks.parse(snr, ''), (null, null));
    expect(GuiderAdvancedPanelTestHooks.parse(snr, '7.5'), (7.5, null));
    expect(GuiderAdvancedPanelTestHooks.parse(snr, '0').$2, isNotNull);
    expect(GuiderAdvancedPanelTestHooks.parse(ms, '1500'), (1500, null));
    expect(
      GuiderAdvancedPanelTestHooks.parse(ms, '1500.5').$2,
      'A whole number',
    );
    expect(snr.format(6.0), '6');
    final maxDec = GuiderAdvancedField.all.firstWhere((f) => f.key == 'max_dec_duration_ms');
    expect(GuiderAdvancedPanelTestHooks.parse(maxDec, '0'), (0, null), reason: '0 = no Dec correction');
    expect(GuiderAdvancedPanelTestHooks.parse(snr, '0').$2, 'A positive number');
    expect(GuiderAdvancedPanelTestHooks.parse(maxDec, '-1').$2, '0 or more');
    expect(GuiderAdvancedField.editText(0.125), '0.125', reason: 'full precision in the editor');
    expect(GuiderAdvancedField.editText(6.0), '6');
    expect(GuiderAdvancedField.editText(1500), '1500');
    final pulse = GuiderAdvancedField.all.firstWhere((f) => f.key == 'backlash_pulse_ms');
    expect(GuiderAdvancedPanelTestHooks.parse(pulse, '10').$2, '20–8000', reason: 'below the guider\'s minimum');
    expect(GuiderAdvancedPanelTestHooks.parse(pulse, '9000').$2, '20–8000');
    expect(GuiderAdvancedPanelTestHooks.parse(pulse, '450'), (450, null));
    expect(GuiderAdvancedPanelTestHooks.parse(maxDec, '1000001').$2, '0–1000000');
    expect(ms.format(1500), '1500 ms');
  });

  testWidgets('shows the guider\'s values and sends only what was set', (
    t,
  ) async {
    final fake = await _pump(t);
    expect(find.text('Guider connected'), findsOneWidget);
    expect(
      find.text("Guider's (On)"),
      findsOneWidget,
      reason: 'multi-star reads the guider',
    );
    // The SNR field hints the guider's value; typing sets the profile's.
    expect(find.text('guider: 6'), findsOneWidget);
    await t.enterText(find.byKey(const Key('ga-min_star_snr')), '9');
    await t.pump();
    await t.tap(find.byKey(const Key('ga-ra_algorithm')));
    await t.pumpAndSettle();
    await t.tap(find.text('Predictive PEC').last);
    await t.pumpAndSettle();
    await t.tap(find.widgetWithText(FilledButton, 'Apply'));
    await t.pumpAndSettle();
    expect(fake.putBody!['min_star_snr'], 9.0);
    expect(fake.putBody!['ra_algorithm'], 'Predictive PEC');
    expect(
      fake.putBody!['multi_star_enabled'],
      isNull,
      reason: 'untouched keeps the guider\'s',
    );
    expect(fake.pushes, 1, reason: 'connected: pushed at once');
    expect(find.byKey(const Key('guider-advanced-status')), findsOneWidget);
  });

  testWidgets('saving multi-star off while connected writes it to the guider', (t) async {
    final fake = await _pump(
      t,
      profile: const GuiderAdvancedSettings({'multi_star_enabled': false}),
    );
    await t.tap(find.widgetWithText(FilledButton, 'Apply'));
    await t.pumpAndSettle();
    expect(fake.putBody!['multi_star_enabled'], false);
    expect(fake.multiStarApplies, 1, reason: 'the guider reports on, the profile says off');
    final status = t.widget<Text>(find.byKey(const Key('guider-advanced-status'))).data!;
    expect(status, isNot(contains('next guider connect')));
  });

  testWidgets('an unset multi-star matches a guider that reports on: no reconnect', (t) async {
    final fake = await _pump(t);
    await t.tap(find.widgetWithText(FilledButton, 'Apply'));
    await t.pumpAndSettle();
    expect(fake.multiStarApplies, 0);
  });

  testWidgets('a refused multi-star write is reported in plain words', (t) async {
    final fake = await _pump(
      t,
      profile: const GuiderAdvancedSettings({'multi_star_enabled': false}),
    );
    fake.applyError = Exception('stop guiding first');
    await t.tap(find.widgetWithText(FilledButton, 'Apply'));
    await t.pumpAndSettle();
    final status = t.widget<Text>(find.byKey(const Key('guider-advanced-status'))).data!;
    expect(status, startsWith('Saved, but multi-star was not applied'));
    expect(status, contains('stop guiding first'));
  });

  test('cross-field rules match the server: delay trio and min <= max', () {
    String? p(Map<String, Object?> v) =>
        GuiderAdvancedPanelTestHooks.crossFieldProblem(
          GuiderAdvancedSettings(v),
        );
    expect(p({}), isNull);
    expect(p({'variable_delay_enabled': true}), contains('all three'));
    expect(
      p({'variable_delay_enabled': true, 'variable_delay_short_s': 1.0}),
      contains('all three'),
    );
    expect(
      p({
        'variable_delay_enabled': false,
        'variable_delay_short_s': 1.0,
        'variable_delay_long_s': 5.0,
      }),
      isNull,
    );
    expect(p({'min_star_hfd': 9.0, 'max_star_hfd': 3.0}), contains('Star HFD'));
    expect(
      p({'auto_exposure_min_ms': 4000, 'auto_exposure_max_ms': 1000}),
      contains('Auto exposure'),
    );
    expect(p({'backlash_enabled': true}), contains('pulse width'));
    expect(p({'backlash_enabled': true, 'backlash_pulse_ms': 450}), isNull);
    expect(p({'backlash_enabled': false}), isNull);
    expect(
      p({'backlash_floor_ms': 300, 'backlash_ceiling_ms': 100}),
      contains('Backlash'),
    );
  });

  testWidgets('a half-set variable delay blocks Apply with a reason', (
    t,
  ) async {
    final fake = await _pump(t);
    await t.enterText(find.byKey(const Key('ga-variable_delay_short_s')), '2');
    await t.pump();
    expect(
      find.byKey(const Key('guider-advanced-cross-field')),
      findsOneWidget,
    );
    expect(
      t
          .widget<FilledButton>(find.widgetWithText(FilledButton, 'Apply'))
          .onPressed,
      isNull,
    );
    expect(fake.putBody, isNull);
  });

  testWidgets(
    'a failed profile load disables Apply so it cannot wipe the saved block',
    (t) async {
      // Apply PUTs the whole section; from an empty draft it would erase the
      // values the load failed to bring back.
      final fake = _Fake(GuiderAdvancedSettings.empty, _guider())
        ..failProfileRead = true;
      t.view.physicalSize = const Size(900, 2400);
      t.view.devicePixelRatio = 1;
      addTearDown(t.view.resetPhysicalSize);
      addTearDown(t.view.resetDevicePixelRatio);
      await t.pumpWidget(
        ProviderScope(
          overrides: [
            activeServerProvider.overrideWithValue(
              const AraServer(hostname: 'localhost', port: 5555),
            ),
            guiderAdvancedApiFactoryProvider.overrideWithValue((_) => fake),
          ],
          child: const MaterialApp(
            home: Scaffold(
              body: SizedBox(
                width: 800,
                height: 2300,
                child: GuiderAdvancedPanel(),
              ),
            ),
          ),
        ),
      );
      await t.pumpAndSettle();
      expect(
        find.byKey(const Key('guider-advanced-status')),
        findsOneWidget,
        reason: 'the load error is shown',
      );
      await t.enterText(find.byKey(const Key('ga-min_star_snr')), '9');
      await t.pump();
      expect(
        t
            .widget<FilledButton>(find.widgetWithText(FilledButton, 'Apply'))
            .onPressed,
        isNull,
      );
      expect(
        t
            .widget<TextButton>(find.widgetWithText(TextButton, "All guider's"))
            .onPressed,
        isNull,
      );
      expect(fake.putBody, isNull);
    },
  );

  testWidgets('an invalid number blocks Apply', (t) async {
    final fake = await _pump(t);
    await t.enterText(find.byKey(const Key('ga-max_dec_duration_ms')), '-3');
    await t.pump();
    expect(find.text('0 or more'), findsOneWidget);
    expect(
      t
          .widget<FilledButton>(find.widgetWithText(FilledButton, 'Apply'))
          .onPressed,
      isNull,
    );
    expect(fake.putBody, isNull);
  });

  group('#1748', () {
    GuiderAdvancedField field(String key) =>
        GuiderAdvancedField.all.singleWhere((f) => f.key == key);

    test('mass change is entered as a percentage and stored as a fraction', () {
      final f = field('mass_change_threshold');
      expect(GuiderAdvancedPanelTestHooks.parse(f, '35'), (0.35, null));
      expect(f.format(0.35), '35 %');
      expect(GuiderAdvancedPanelTestHooks.parse(f, '101').$2, isNotNull);
    });

    test('variable delays are whole seconds and allow 0', () {
      final f = field('variable_delay_short_s');
      expect(GuiderAdvancedPanelTestHooks.parse(f, '0'), (0, null));
      expect(GuiderAdvancedPanelTestHooks.parse(f, '1.5').$2, 'A whole number');
      expect(GuiderAdvancedPanelTestHooks.parse(f, '11').$2, isNotNull);
    });

    test('equal star HFDs and a pulse outside floor to ceiling are refused', () {
      expect(
        GuiderAdvancedPanelTestHooks.crossFieldProblem(
          const GuiderAdvancedSettings({'min_star_hfd': 3.0, 'max_star_hfd': 3.0}),
        ),
        contains('Star HFD'),
      );
      expect(
        GuiderAdvancedPanelTestHooks.crossFieldProblem(
          const GuiderAdvancedSettings({'backlash_pulse_ms': 100, 'backlash_floor_ms': 200}),
        ),
        contains('Backlash'),
      );
    });

    testWidgets('a saved mass change shows as a percentage', (t) async {
      await _pump(t, profile: const GuiderAdvancedSettings({'mass_change_threshold': 0.35}));
      expect(
        t.widget<TextField>(find.byKey(const Key('ga-mass_change_threshold'))).controller!.text,
        '35',
      );
    });

    testWidgets('what the push did not land is listed with its reason', (t) async {
      await _pump(
        t,
        guider: GuiderAdvancedView.fromJson({
          'connected': true,
          'not_applied': [
            {'setting': 'RA aggressiveness', 'reason': 'The current guide algorithm has no aggressiveness setting.'},
          ],
        }),
      );
      expect(find.byKey(const Key('guider-not-applied')), findsOneWidget);
      expect(find.textContaining('RA aggressiveness: The current'), findsOneWidget);
      expect(
        find.descendant(of: find.byType(ListView), matching: find.byKey(const Key('guider-not-applied'))),
        findsOneWidget,
        reason: 'a long list scrolls with the settings',
      );
      expect(find.descendant(of: find.byKey(const Key('guider-not-applied')), matching: find.byIcon(Icons.warning_amber_rounded)), findsOneWidget);
    });

    testWidgets('nothing to report shows no list', (t) async {
      await _pump(t);
      expect(find.byKey(const Key('guider-not-applied')), findsNothing);
    });

    testWidgets('a saved algorithm the guider does not offer is marked', (t) async {
      await _pump(t, profile: const GuiderAdvancedSettings({'ra_algorithm': 'ZFilter'}));
      expect(find.textContaining('ZFilter (not offered)'), findsWidgets);
    });

    final notLanded = GuiderAdvancedView.fromJson({
      'connected': true,
      'not_applied': [
        {'setting': 'Search region', 'reason': 'Must be a whole number from 7 to 50. Not sent.'},
      ],
    });

    testWidgets('the list is announced when it appears', (t) async {
      await _pump(t, guider: notLanded);
      final node = t.getSemantics(find.byKey(const Key('guider-not-applied')));
      expect(node.flagsCollection.isLiveRegion, isTrue);
    });

    testWidgets('Apply does not read as a clean success when a setting did not land', (t) async {
      await _pump(t, guider: notLanded);
      await t.tap(find.widgetWithText(FilledButton, 'Apply'));
      await t.pumpAndSettle();
      expect(t.widget<Text>(find.byKey(const Key('guider-advanced-status'))).data, contains('did not apply'));
    });

    testWidgets('a cross-field problem reads in the error text colour', (t) async {
      await _pump(t, profile: const GuiderAdvancedSettings({'min_star_hfd': 3.0, 'max_star_hfd': 3.0}));
      expect(t.widget<Text>(find.byKey(const Key('guider-advanced-cross-field'))).style?.color, AraColors.errorText);
    });

    test('a range error fits a narrow field', () {
      expect(GuiderAdvancedPanelTestHooks.parse(field('dither_scale_factor'), '101').$2, 'Up to 100');
      expect(GuiderAdvancedPanelTestHooks.parse(field('variable_delay_long_s'), '121').$2, '0–120');
    });

    testWidgets('the Apply result is announced from anywhere in the settings', (t) async {
      await _pump(t);
      await t.tap(find.widgetWithText(FilledButton, 'Apply'));
      await t.pumpAndSettle();
      expect(t.getSemantics(find.byKey(const Key('guider-advanced-status'))).flagsCollection.isLiveRegion, isTrue);
    });

    testWidgets('a field error reads in the error text colour', (t) async {
      await _pump(t);
      await t.enterText(find.byKey(const Key('ga-dither_scale_factor')), '101');
      await t.pump();
      final field = t.widget<TextField>(find.byKey(const Key('ga-dither_scale_factor')));
      expect(field.decoration?.errorStyle?.color, AraColors.errorText);
    });
  });
}
