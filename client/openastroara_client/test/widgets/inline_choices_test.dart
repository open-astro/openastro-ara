import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/equipment_connection_state.dart';
import 'package:openastroara/widgets/equipment/alpaca_chooser_dialog.dart';
import 'package:openastroara/widgets/inline_choices.dart';

const _viewport = 400.0;

/// A button, a page's worth of space, and the question when [open].
Widget _confirm(bool open, VoidCallback toggle, FocusNode opener) => MaterialApp(
  home: Scaffold(
    body: SingleChildScrollView(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          TextButton(focusNode: opener, onPressed: toggle, child: const Text('Open')),
          const SizedBox(height: 2 * _viewport),
          if (open)
            InlineConfirm(
              id: 'ask',
              title: 'Sure?',
              body: const Text('Really.'),
              confirmLabel: 'Yes',
              cancelLabel: 'No',
              onConfirm: toggle,
              onCancel: toggle,
            ),
        ],
      ),
    ),
  ),
);

Future<void Function()> _pumpConfirm(WidgetTester tester, FocusNode opener) async {
  tester.view.physicalSize = const Size(800, _viewport);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  var open = false;
  late StateSetter set;
  await tester.pumpWidget(
    StatefulBuilder(
      builder: (_, s) {
        set = s;
        return _confirm(open, () => s(() => open = !open), opener);
      },
    ),
  );
  opener.requestFocus();
  await tester.pump();
  return () => set(() => open = !open);
}

void main() {
  testWidgets('an inline confirm below the fold scrolls in, takes the focus and says its title', (tester) async {
    final opener = FocusNode();
    addTearDown(opener.dispose);
    final toggle = await _pumpConfirm(tester, opener);
    toggle();
    await tester.pumpAndSettle();
    expect(tester.getRect(find.byKey(const Key('ask-confirm'))).bottom, lessThanOrEqualTo(_viewport));
    expect(opener.hasFocus, isFalse);
    expect(FocusManager.instance.primaryFocus?.context?.findAncestorWidgetOfExactType<InlineConfirm>(), isNotNull);
    expect(tester.takeAnnouncements().map((a) => a.message), contains('Sure?'));
  });

  testWidgets('answering an inline confirm gives the focus back to what opened it', (tester) async {
    final opener = FocusNode();
    addTearDown(opener.dispose);
    final toggle = await _pumpConfirm(tester, opener);
    toggle();
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('ask-cancel')));
    await tester.pumpAndSettle();
    expect(find.text('Sure?'), findsNothing);
    expect(opener.hasFocus, isTrue);
  });

  Future<void> pumpChooser(WidgetTester tester, {Locale? locale, AraServer? server}) async {
    await tester.pumpWidget(
      ProviderScope(
        overrides: [activeServerProvider.overrideWithValue(server)],
        child: MaterialApp(
          locale: locale,
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: Scaffold(
            body: AlpacaDeviceChooser(
              type: EquipmentDeviceType.camera,
              deviceTypeLabel: 'Camera',
              onClose: () {},
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  testWidgets('the inline device chooser takes the focus and says its title', (tester) async {
    await pumpChooser(tester);
    expect(tester.takeAnnouncements().map((a) => a.message), contains('Choose Camera'));
    expect(FocusManager.instance.primaryFocus?.context?.findAncestorWidgetOfExactType<AlpacaDeviceChooser>(), isNotNull);
  });

  testWidgets('a refused discovery says so in plain words and fits a phone', (tester) async {
    tester.view.physicalSize = const Size(360, 800);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await pumpChooser(tester, server: const AraServer(hostname: 'rig', port: 1));
    expect(tester.takeException(), isNull);
    expect(find.textContaining('validateStatus'), findsNothing);
    expect(find.textContaining("Couldn't find devices"), findsOneWidget);
  });

  testWidgets('the device chooser is in Brazilian Portuguese', (tester) async {
    await pumpChooser(tester, locale: const Locale('pt', 'BR'));
    expect(find.text('Escolher Camera'), findsOneWidget);
    expect(find.text('Falha na busca'), findsOneWidget);
    expect(find.text('Não conectado — conecte-se ao seu setup para salvar.'), findsOneWidget);
  });
}
