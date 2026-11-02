import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/simple/simple_shell.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/state/settings/settings_nav.dart';
import 'package:openastroara/state/simple_mode_state.dart';

class _MemPrefs extends SimpleModePrefsService {
  bool value = true;
  @override
  Future<bool> load() async => value;
  @override
  Future<void> save(bool enabled) async => value = enabled;
}

class _StartOnLive extends SelectedTabIndexNotifier {
  @override
  int build() => kLiveTabIndex;
}

// The test font draws every glyph as a square, far wider than a real face,
// so fit is measured with the Roboto the Flutter SDK ships.
final _fonts = '${Platform.environment['FLUTTER_ROOT']}/bin/cache/artifacts/material_fonts';

Future<void> _loadFonts() async {
  ByteData bytes(String f) => ByteData.view(Uint8List.fromList(File('$_fonts/$f').readAsBytesSync()).buffer);
  final roboto = FontLoader('Roboto');
  for (final f in ['Roboto-Regular.ttf', 'Roboto-Medium.ttf', 'Roboto-Bold.ttf']) {
    roboto.addFont(Future.value(bytes(f)));
  }
  await roboto.load();
  await (FontLoader('MaterialIcons')..addFont(Future.value(bytes('MaterialIcons-Regular.otf')))).load();
}

void main() {
  final hasFonts = File('$_fonts/Roboto-Regular.ttf').existsSync();
  setUpAll(() async {
    if (hasFonts) await _loadFonts();
  });

  // iPad landscape and portrait at the user's text sizes up to 2x, and a
  // one-third Split View (or a phone) at the default size.
  final cases = <(double, Size)>[
    for (final s in [1.0, 1.3, 1.6, 2.0]) ...[(s, const Size(1024, 768)), (s, const Size(768, 1024))],
    (1.0, const Size(320, 768)),
  ];
  for (final (scale, size) in cases) {
    for (final open in ['Preview settings', 'Plan']) {
      testWidgets('text ${scale}x at ${size.width.toInt()}x${size.height.toInt()}, $open: nothing overflows',
          skip: !hasFonts, (tester) async {
        tester.view.physicalSize = size;
        tester.view.devicePixelRatio = 1;
        addTearDown(tester.view.reset);
        final c = ProviderContainer(overrides: [
          simpleModePrefsProvider.overrideWithValue(_MemPrefs()),
          selectedTabIndexProvider.overrideWith(_StartOnLive.new),
        ]);
        addTearDown(c.dispose);
        await c.read(simpleModeProvider.future);
        await tester.pumpWidget(UncontrolledProviderScope(
          container: c,
          child: MaterialApp(
            theme: ThemeData(fontFamily: 'Roboto'),
            builder: (context, child) => MediaQuery(
              data: MediaQuery.of(context).copyWith(textScaler: TextScaler.linear(scale)),
              child: child!,
            ),
            home: const SimpleShell(planetarium: SizedBox()),
          ),
        ));
        await tester.pump();
        if (open == 'Plan') {
          await tester.tap(find.byKey(const Key('simple-mode')), warnIfMissed: false);
          await tester.pump();
          await tester.tap(find.text('Plan').first, warnIfMissed: false);
        } else {
          await tester.tap(find.byKey(const Key('simple-settings')), warnIfMissed: false);
        }
        await tester.pump();
        // An overflow fails the test on its own; this names the case.
        expect(tester.takeException(), isNull);
      });
    }
  }
}
