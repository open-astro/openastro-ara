import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_advanced_settings.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/guider_advanced_api.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/widgets/imaging/guider_advanced_dialog.dart';

class _Fake implements GuiderAdvancedClient {
  GuiderAdvancedSettings profile;
  bool failProfileRead = false;
  final GuiderAdvancedView guider;
  Map<String, dynamic>? putBody;
  int pushes = 0;
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
}) async {
  final fake = _Fake(profile, _guider());
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
    expect(GuiderAdvancedPanelTestHooks.parse(maxDec, '-1').$2, 'Zero or a positive number');
    expect(GuiderAdvancedField.editText(0.125), '0.125', reason: 'full precision in the editor');
    expect(GuiderAdvancedField.editText(6.0), '6');
    expect(GuiderAdvancedField.editText(1500), '1500');
    final pulse = GuiderAdvancedField.all.firstWhere((f) => f.key == 'backlash_pulse_ms');
    expect(GuiderAdvancedPanelTestHooks.parse(pulse, '10').$2, 'The guider takes 20–8000', reason: 'below the guider\'s minimum');
    expect(GuiderAdvancedPanelTestHooks.parse(pulse, '9000').$2, 'The guider takes 20–8000');
    expect(GuiderAdvancedPanelTestHooks.parse(pulse, '450'), (450, null));
    expect(GuiderAdvancedPanelTestHooks.parse(maxDec, '1000001').$2, 'The guider takes 0–1000000');
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
    expect(find.text('Zero or a positive number'), findsOneWidget);
    expect(
      t
          .widget<FilledButton>(find.widgetWithText(FilledButton, 'Apply'))
          .onPressed,
      isNull,
    );
    expect(fake.putBody, isNull);
  });
}
