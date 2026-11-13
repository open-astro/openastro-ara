import 'dart:io';

import 'package:flutter/material.dart';

import 'package:flutter/services.dart';
import 'package:flutter/rendering.dart' show RenderParagraph;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/simple/simple_focus_panel.dart';
import 'package:openastroara/screens/simple/simple_shell.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/state/rig_setup_state.dart';
import 'package:openastroara/state/server_state.dart'
    show discoveredServersProvider;
import 'package:openastroara/state/settings/settings_nav.dart';
import 'package:openastroara/services/monitor_device_service.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/simple_monitor_state.dart';
import 'package:openastroara/state/sky_atlas/tonight_sky_state.dart'
    show clockProvider;
import 'package:openastroara/state/simple_preflight_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/widgets/emergency_stop_button.dart';

class _Prefs extends SimpleModePrefsService {
  @override
  Future<bool> load() async => true;
  @override
  Future<void> save(bool enabled) async {}
}

/// Monitor's clock without its one-second timer, which would outlive the test.
class _Clock extends MonitorClock {
  @override
  DateTime build() => ref.watch(clockProvider)();
}

class _StartOnLive extends SelectedTabIndexNotifier {
  @override
  int build() => kLiveTabIndex;
}

/// A phone (or, with [phone] false, an iPad-sized desktop) of [size].
Future<ProviderContainer> _pump(
  WidgetTester tester,
  Size size, {
  bool phone = true,
  bool linkUp = true,
  Locale? locale,
}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final container = ProviderContainer(
    overrides: [
      companionPlatformProvider.overrideWithValue(
        phone ? TargetPlatform.iOS : TargetPlatform.linux,
      ),
      simpleModePrefsProvider.overrideWithValue(_Prefs()),
      serverLinkUpProvider.overrideWithValue(linkUp),
      discoveredServersProvider.overrideWith(
        (ref) => const Stream<AraServer>.empty(),
      ),
      rigSetupHydratedProvider.overrideWith((ref) async => false),
      selectedTabIndexProvider.overrideWith(_StartOnLive.new),
      monitorClockProvider.overrideWith(_Clock.new),
      deviceBatteryProvider.overrideWith(
        (ref) => Stream.value((level: 64, charging: false)),
      ),
    ],
  );
  addTearDown(container.dispose);
  await container.read(simpleModeProvider.future);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
        locale: locale,
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: const SimpleShell(planetarium: SizedBox(key: Key('sky'))),
      ),
    ),
  );
  await tester.pump();
  return container;
}

const _sections = ['preview', 'focus', 'polarAlign', 'guide', 'plan'];
const _portrait = Size(390, 844);
const _landscape = Size(844, 390);

// The test font draws every glyph as a square, far wider than a real face,
// so fit is measured with the Roboto the Flutter SDK ships.
final _fonts =
    '${Platform.environment['FLUTTER_ROOT']}/bin/cache/artifacts/material_fonts';

Future<void> _loadFonts() async {
  ByteData bytes(String f) => ByteData.view(
    Uint8List.fromList(File('$_fonts/$f').readAsBytesSync()).buffer,
  );
  final roboto = FontLoader('Roboto');
  for (final f in [
    'Roboto-Regular.ttf',
    'Roboto-Medium.ttf',
    'Roboto-Bold.ttf',
  ]) {
    roboto.addFont(Future.value(bytes(f)));
  }
  await roboto.load();
  await (FontLoader(
    'MaterialIcons',
  )..addFont(Future.value(bytes('MaterialIcons-Regular.otf')))).load();
}

void main() {
  final hasFonts = File('$_fonts/Roboto-Regular.ttf').existsSync();
  setUpAll(() async {
    if (hasFonts) await _loadFonts();
  });
  Finder section(String name) => find.byKey(Key('companion-section-$name'));
  final stop = find.byType(EmergencyStopButton);

  /// The row swipes: bring the word into view, then tap it.
  Future<void> tapSection(WidgetTester tester, String name) async {
    final row = find.byKey(const Key('companion-sections'));
    final screen = tester.view.physicalSize.width;
    if (tester.getCenter(section(name)).dx > screen - 30) {
      await tester.drag(row, Offset(-screen, 0));
      await tester.pump(const Duration(milliseconds: 500));
    }
    await tester.tap(section(name));
    await tester.pump();
  }

  /// STOP is on screen, at least 44 px each way, and a held press starts its hold.
  Future<void> expectHoldable(WidgetTester tester, String why) async {
    expect(stop, findsOneWidget, reason: why);
    final rect = tester.getRect(stop);
    expect(rect.width, greaterThanOrEqualTo(60), reason: why);
    expect(rect.height, greaterThanOrEqualTo(60), reason: why);
    final screen = tester.view.physicalSize;
    expect(
      rect.left >= 0 &&
          rect.top >= 0 &&
          rect.right <= screen.width &&
          rect.bottom <= screen.height,
      isTrue,
      reason: '$why: STOP is on the screen',
    );
    final g = await tester.startGesture(rect.center);
    await tester.pump(const Duration(milliseconds: 300));
    expect(find.text('HOLD'), findsOneWidget, reason: '$why: the hold started');
    await g.up();
    await tester.pump();
  }

  testWidgets('on a phone held upright (390x844) both rails fold into one deck with the six section words, '
      'the shutter, the chip and STOP', (tester) async {
    await _pump(tester, _portrait);
    expect(find.byKey(const Key('companion-deck')), findsOneWidget);
    for (final s in _sections) {
      expect(section(s), findsOneWidget);
    }
    expect(section('monitor'), findsOneWidget);
    expect(find.text('Monitor'), findsOneWidget, reason: 'the sixth word');
    expect(
      find.byKey(const Key('simple-mode')),
      findsNothing,
      reason: 'the right rail is gone',
    );
    expect(
      find.byKey(const Key('simple-stats')),
      findsNothing,
      reason: 'the left rail is gone',
    );
    for (final k in [
      'simple-sky',
      'simple-info',
      'simple-library',
      'simple-shutter',
      'simple-settings',
    ]) {
      expect(find.byKey(Key(k)), findsOneWidget, reason: k);
    }
    // STOP at the bottom right of the deck, the shutter and chip in the middle.
    expect(
      tester.getCenter(stop).dx,
      greaterThan(tester.getCenter(find.byKey(const Key('simple-shutter'))).dx),
    );
    expect(
      tester.getBottomRight(find.byKey(const Key('companion-deck'))).dy,
      lessThanOrEqualTo(844),
    );
  });

  testWidgets(
    'the deck lights the current section and a tap on another one shows it',
    (tester) async {
      final c = await _pump(tester, _portrait);
      expect(c.read(simpleShowingProvider), SimpleModeScreen.preview);
      await tapSection(tester, 'focus');
      expect(c.read(simpleShowingProvider), SimpleModeScreen.focus);
      Color? fill(String name) => tester
          .widget<Material>(
            find
                .descendant(of: section(name), matching: find.byType(Material))
                .first,
          )
          .color;
      expect(
        fill('focus'),
        AraColors.selectionBg,
        reason: 'the current section is lit',
      );
      expect(fill('preview'), Colors.transparent);
    },
  );

  for (final size in [_portrait, const Size(360, 640)]) {
    for (final name in _sections) {
      testWidgets(
        'at $size STOP stays visible and holdable on $name, closed and with its panel open',
        (tester) async {
          await _pump(tester, size);
          await tapSection(tester, name);
          await expectHoldable(tester, name);
          await tester.tap(find.byKey(const Key('simple-settings')));
          await tester.pump();
          await expectHoldable(tester, '$name with its panel open');
        },
      );
    }
  }

  testWidgets('the Monitor word opens the page, whose STOP is a 60 px square', (
    tester,
  ) async {
    await _pump(tester, _portrait);
    await tapSection(tester, 'monitor');
    await tester.pump(const Duration(milliseconds: 500));
    // The page's own two-column grid is wider than a phone (not this slice).
    tester.takeException();
    expect(find.byKey(const Key('simple-monitor')), findsOneWidget);
    final size = tester.getSize(find.byType(EmergencyStopButton));
    expect(
      size.width,
      lessThanOrEqualTo(64),
      reason: 'a phone\'s Stop is 60 px, not the iPad\'s 108 pt',
    );
    expect(size.width, greaterThanOrEqualTo(60));
  });

  testWidgets(
    'an open panel stays above the deck, so the deck and STOP stay clear of it',
    (tester) async {
      await _pump(tester, _portrait);
      await tapSection(tester, 'focus');
      final deck = tester.getRect(find.byKey(const Key('companion-deck')));
      final panel = find.byType(SimpleFocusPanel);
      expect(panel, findsOneWidget, reason: 'the section opens its panel');
      expect(
        tester.getRect(panel).bottom,
        lessThanOrEqualTo(deck.top),
        reason: 'the panel stops above the deck',
      );
      expect(tester.getRect(panel).width, greaterThan(250));
    },
  );

  testWidgets(
    'before any rig has been connected STOP is drawn disabled in its place and starts no hold',
    (tester) async {
      await _pump(tester, _portrait, linkUp: false);
      expect(stop, findsOneWidget);
      expect(
        tester
            .widget<FilledButton>(
              find.descendant(of: stop, matching: find.byType(FilledButton)),
            )
            .onPressed,
        isNull,
      );
      final g = await tester.startGesture(tester.getCenter(stop));
      await tester.pump(const Duration(milliseconds: 300));
      expect(find.text('HOLD'), findsNothing);
      await g.up();
      await tester.pump();
    },
  );

  for (final (locale, scale) in [
    (const Locale('en'), 1.0),
    (const Locale('en'), 1.5),
    (const Locale('en'), 2.0),
    (const Locale('pt', 'BR'), 1.0),
    (const Locale('pt', 'BR'), 1.5),
    (const Locale('pt', 'BR'), 2.0),
  ]) {
    testWidgets(
      'on a phone upright in $locale at text ${scale}x no deck label is cut and every target is 44 px',
      skip: !hasFonts,
      (tester) async {
        tester.platformDispatcher.textScaleFactorTestValue = scale;
        addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
        await _pump(tester, _portrait, locale: locale);
        expect(tester.takeException(), isNull);
        final deck = find.byKey(const Key('companion-deck'));
        for (final p in tester.renderObjectList<RenderParagraph>(
          find.descendant(of: deck, matching: find.byType(RichText)),
        )) {
          expect(
            p.didExceedMaxLines,
            isFalse,
            reason: '"${p.text.toPlainText()}" is cut off',
          );
        }
        for (final k in [
          'simple-sky',
          'simple-info',
          'simple-library',
          'simple-settings',
          'companion-section-preview',
          'companion-section-monitor',
        ]) {
          final r = tester.getRect(find.byKey(Key(k)));
          expect(r.height, greaterThanOrEqualTo(44), reason: '$k height');
          expect(r.width, greaterThanOrEqualTo(44), reason: '$k width');
        }
        expect(
          tester.getRect(stop).right,
          lessThanOrEqualTo(390),
          reason: 'STOP is on the screen',
        );
        expect(tester.getRect(deck).bottom, lessThanOrEqualTo(844));
      },
    );
  }

  testWidgets(
    'a phone on its side (844x390) keeps the narrow layout: the rails, no deck',
    (tester) async {
      await _pump(tester, _landscape);
      // Today's right rail is 2 px too tall for 390 px; the layout is not ours to change here.
      tester.takeException();
      expect(find.byKey(const Key('companion-deck')), findsNothing);
      expect(
        tester.getCenter(find.byKey(const Key('simple-sky'))).dx,
        lessThan(100),
        reason: 'left rail',
      );
      expect(
        tester.getCenter(find.byKey(const Key('simple-mode'))).dx,
        greaterThan(744),
        reason: 'right rail',
      );
      expect(
        tester.getCenter(stop).dx,
        greaterThan(744),
        reason: 'STOP below the right rail',
      );
      expect(tester.getCenter(stop).dy, greaterThan(300));
      expect(find.byKey(const Key('simple-mode')), findsOneWidget);
      expect(find.byKey(const Key('simple-stats')), findsOneWidget);
      expect(find.byKey(const Key('simple-settings')), findsOneWidget);
      expect(stop, findsOneWidget);
    },
  );

  testWidgets(
    'with Companion Mode off (a tablet, a desktop) the rails stay, upright too',
    (tester) async {
      await _pump(tester, const Size(768, 1024), phone: false);
      expect(find.byKey(const Key('companion-deck')), findsNothing);
      expect(find.byKey(const Key('simple-mode')), findsOneWidget);
      expect(find.byKey(const Key('simple-stats')), findsOneWidget);
    },
  );
}
