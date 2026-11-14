import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/l10n.dart';
import 'package:openastroara/screens/wizard/wizard_shell.dart';
import 'package:openastroara/state/wizard_state.dart';

// The per-screen field-validation cases that lived here died with the retired
// §37 capture-setup screens (§76 — those settings default and move to
// Options). What remains is the CONTRACT: screens publish validity, the
// controller resets it on navigation, and the shell gates Next on it.

// The test font draws every glyph as a square, far wider than a real face,
// so footer fit is measured with the Roboto the Flutter SDK ships.
final _fonts =
    '${Platform.environment['FLUTTER_ROOT']}/bin/cache/artifacts/material_fonts';

Future<void> _loadFonts() async {
  ByteData bytes(String f) => ByteData.view(
    Uint8List.fromList(File('$_fonts/$f').readAsBytesSync()).buffer,
  );
  final roboto = FontLoader('Roboto');
  for (final f in ['Roboto-Regular.ttf', 'Roboto-Medium.ttf']) {
    roboto.addFont(Future.value(bytes(f)));
  }
  await roboto.load();
  await (FontLoader(
    'MaterialIcons',
  )..addFont(Future.value(bytes('MaterialIcons-Regular.otf')))).load();
}

/// Pumps the wizard on step 1 in a [size] window (1 px per logical px).
Future<void> _pumpShell(
  WidgetTester tester, {
  required Size size,
  Locale locale = const Locale('en'),
}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final container = ProviderContainer();
  addTearDown(container.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
        locale: locale,
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: const WizardShell(),
      ),
    ),
  );
  await tester.pump();
}

void main() {
  final hasFonts = File('$_fonts/Roboto-Regular.ttf').existsSync();
  setUpAll(() async {
    if (hasFonts) await _loadFonts();
  });

  test('controller resets validity to true on navigation', () {
    final container = ProviderContainer();
    addTearDown(container.dispose);
    final controller = container.read(wizardControllerProvider.notifier);

    container.read(wizardStepValidProvider.notifier).setValid(false);
    expect(container.read(wizardStepValidProvider), isFalse);

    controller.next();
    expect(container.read(wizardStepValidProvider), isTrue,
        reason: 'a fresh screen starts valid');

    container.read(wizardStepValidProvider.notifier).setValid(false);
    controller.back();
    expect(container.read(wizardStepValidProvider), isTrue);
  });

  testWidgets('shell disables Next + Skip while the step is invalid',
      (tester) async {
    final container = ProviderContainer();
    addTearDown(container.dispose);
    // Step 1 (Welcome) publishes no validation of its own — drive the provider
    // directly to exercise the shell's gate.
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: WizardShell()),
    ));
    await tester.pump();

    FilledButton nextButton() =>
        tester.widget<FilledButton>(find.widgetWithText(FilledButton, 'Next'));
    TextButton skipButton() => tester.widget<TextButton>(
        find.widgetWithText(TextButton, 'Skip — use defaults'));
    expect(nextButton().onPressed, isNotNull, reason: 'enabled initially');

    container.read(wizardStepValidProvider.notifier).setValid(false);
    await tester.pump();
    expect(nextButton().onPressed, isNull, reason: 'disabled while invalid');
    expect(skipButton().onPressed, isNull,
        reason: 'a skippable gate is no gate');

    container.read(wizardStepValidProvider.notifier).setValid(true);
    await tester.pump();
    expect(nextButton().onPressed, isNotNull, reason: 're-enabled when fixed');
  });

  testWidgets('the wizard shell is in Brazilian Portuguese', (tester) async {
    final container = ProviderContainer();
    addTearDown(container.dispose);
    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: container,
        child: const MaterialApp(
          locale: Locale('pt', 'BR'),
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: WizardShell(),
        ),
      ),
    );
    await tester.pump();
    expect(find.text('Avançar'), findsOneWidget);
    expect(find.text('Salvar e sair'), findsOneWidget);
    expect(find.text('Next'), findsNothing);
  });

  for (final locale in const [Locale('en'), Locale('pt', 'BR')]) {
    testWidgets('the wizard footer keeps Next on a 390 px phone in $locale', (
      tester,
    ) async {
      await _pumpShell(tester, size: const Size(390, 844), locale: locale);
      expect(tester.takeException(), isNull);
      final next = tester.getRect(find.byType(FilledButton));
      expect(next.left, greaterThanOrEqualTo(0));
      expect(next.right, lessThanOrEqualTo(390));
    });

    testWidgets(
      'the wizard footer keeps the Skip text readable at 390 px in $locale',
      (tester) async {
        expect(hasFonts, isTrue, reason: 'Roboto not found under $_fonts');
        await _pumpShell(tester, size: const Size(390, 844), locale: locale);
        final skip = lookupAppLocalizations(locale).wizardSkipDefaults;
        expect(
          tester.getSize(find.text(skip)).width,
          greaterThanOrEqualTo(100),
        );
      },
      // A dev box without the SDK fonts skips; CI must run it.
      skip: !hasFonts && !Platform.environment.containsKey('CI'),
    );
  }

  testWidgets('the wizard footer keeps Next on the right edge on desktop', (
    tester,
  ) async {
    await _pumpShell(tester, size: const Size(1280, 800));
    expect(tester.takeException(), isNull);
    // 1280 minus the footer's 16 px right padding.
    expect(tester.getRect(find.byType(FilledButton)).right, 1264);
    expect(find.text('Welcome'), findsOneWidget, reason: 'footer stage label');
  });
}
