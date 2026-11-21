import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/screens/settings/panels/equipment_focuser_panel.dart';
import 'package:openastroara/screens/settings/panels/imaging_autofocus_panel.dart';
import 'package:openastroara/screens/settings/settings_shell.dart';
import 'package:openastroara/services/autofocus_api.dart';
import 'package:openastroara/state/equipment/focuser_state.dart'
    show autofocusApiProvider;
import 'package:openastroara/state/focus/autofocus_step_size_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/advanced_settings_state.dart';
import 'package:openastroara/state/settings/settings_nav.dart';
import 'package:openastroara/state/simple_mode_state.dart';

// #2036: the Advanced switch belongs to Simple Mode's Settings. Advanced Mode
// has no switch and behaves as if it were on, whatever the saved value says.
class _Advanced extends AdvancedSettings {
  _Advanced(this.on);
  final bool on;
  @override
  Future<bool> build() async => on;
}

Future<void> _pump(
  WidgetTester tester,
  Widget home, {
  required bool simple,
  bool saved = false,
  List<Override> extra = const [],
}) async {
  tester.view.physicalSize = const Size(1600, 1600);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final prior = FlutterError.onError;
  FlutterError.onError = (d) {
    if (!d.exceptionAsString().contains('overflowed')) prior?.call(d);
  };
  addTearDown(() => FlutterError.onError = prior);
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        simpleModeActiveProvider.overrideWithValue(simple),
        advancedSettingsProvider.overrideWith(() => _Advanced(saved)),
        ...extra,
      ],
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        builder: (context, child) => MediaQuery(
          data: MediaQuery.of(context)
              .copyWith(textScaler: const TextScaler.linear(0.5)),
          child: child!,
        ),
        home: Scaffold(body: home),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

class _Shell extends ConsumerWidget {
  const _Shell();
  @override
  Widget build(BuildContext context, WidgetRef ref) {
    // The About panel needs no rig.
    Future.microtask(
      () => ref
          .read(selectedSettingsPanelProvider.notifier)
          .select('app.changelog'),
    );
    return const SettingsShell();
  }
}

void main() {
  final toggle = find.byKey(const Key('settings-advanced-toggle'));

  testWidgets('Advanced Mode Settings has no Advanced switch (#2036)', (
    tester,
  ) async {
    await _pump(tester, const _Shell(), simple: false);
    expect(toggle, findsNothing);
  });

  testWidgets('Simple Mode Settings keeps the Advanced switch (#2036)', (
    tester,
  ) async {
    await _pump(tester, const _Shell(), simple: true);
    expect(toggle, findsOneWidget);
  });

  testWidgets(
    'Advanced Mode shows every Autofocus tier though the saved switch is off (#2036)',
    (tester) async {
      await _pump(
        tester,
        const ImagingAutofocusPanel(),
        simple: false,
        extra: [
          activeServerProvider.overrideWithValue(null),
          autofocusApiProvider.overrideWithValue(null),
        ],
      );
      expect(find.text('Binning'), findsOneWidget);
      expect(find.text('Number of steps (3..31)'), findsOneWidget);
    },
  );

  testWidgets(
    'Advanced Mode shows the derived step size though the saved switch is off (#2036)',
    (tester) async {
      await _pump(
        tester,
        const EquipmentFocuserPanel(),
        simple: false,
        extra: [
          autofocusStepSizeProvider.overrideWith(
            (ref) async =>
                const AutofocusStepSize(stepSize: 31, source: 'measured'),
          ),
        ],
      );
      expect(
        find.byKey(const Key('focuser-derived-step-size')),
        findsOneWidget,
      );
    },
  );
}
