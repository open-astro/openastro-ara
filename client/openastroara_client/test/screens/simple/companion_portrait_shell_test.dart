import 'dart:io';
import 'dart:math' as math;

import 'package:flutter/foundation.dart' show precisionErrorTolerance;
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
import 'package:openastroara/state/imaging/capture_progress_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/screens/simple/simple_preview_panel.dart'
    show SimpleStatusPill;
import 'package:openastroara/screens/simple/simple_status_card.dart';
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

/// Text from 1x to 2x, in English and Brazilian Portuguese.
const _textSizes = [
  (Locale('en'), 1.0),
  (Locale('en'), 1.5),
  (Locale('en'), 2.0),
  (Locale('pt', 'BR'), 1.0),
  (Locale('pt', 'BR'), 1.5),
  (Locale('pt', 'BR'), 2.0),
];

/// How far apart two targets are, side by side or a row apart: the gap along
/// the axis that parts them (negative where they overlap).
double _gap(Rect a, Rect b) => [
  b.left - a.right,
  a.left - b.right,
  b.top - a.bottom,
  a.top - b.bottom,
].reduce(math.max);

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
    // No overflow: the tiles lie in one column on a phone, and the verdict
    // shows whole.
    expect(find.byKey(const Key('simple-monitor')), findsOneWidget);
    expect(find.byKey(const Key('simple-monitor-column')), findsOneWidget);
    final verdict = tester.renderObject<RenderParagraph>(
      find.descendant(
        of: find.byKey(const Key('simple-monitor-verdict')),
        matching: find.byType(RichText),
      ),
    );
    expect(verdict.didExceedMaxLines, isFalse, reason: 'the verdict is cut');
    final size = tester.getSize(find.byType(EmergencyStopButton));
    expect(
      size.width,
      lessThanOrEqualTo(64),
      reason: 'a phone\'s Stop is 60 px, not the iPad\'s 108 pt',
    );
    expect(size.width, greaterThanOrEqualTo(60));
  });

  for (final size in [_portrait, const Size(360, 780)]) {
    for (final (locale, scale) in _textSizes) {
      testWidgets(
        'opened from the deck at $size in $locale at text ${scale}x, Monitor gives the verdict a line of its own and fits the Stop label',
        skip: !hasFonts,
        (tester) async {
          tester.platformDispatcher.textScaleFactorTestValue = scale;
          addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
          await _pump(tester, size, locale: locale);
          await tapSection(tester, 'monitor');
          await tester.pump(const Duration(milliseconds: 500));
          // No takeException(): an overflow anywhere fails the case.
          expect(find.byKey(const Key('simple-monitor')), findsOneWidget);
          final verdict = tester.renderObject<RenderParagraph>(
            find.descendant(
              of: find.byKey(const Key('simple-monitor-verdict')),
              matching: find.byType(RichText),
            ),
          );
          expect(
            verdict.didExceedMaxLines,
            isFalse,
            reason: 'the verdict is cut',
          );
          expect(
            verdict.size.width,
            greaterThanOrEqualTo(size.width / 2),
            reason: 'the verdict is squeezed beside the clock',
          );
          final button = tester.getRect(
            find.descendant(of: stop, matching: find.byType(FilledButton)),
          );
          final label = tester.renderObject<RenderParagraph>(
            find.descendant(
              of: find.descendant(
                of: stop,
                matching: find.text(
                  lookupAppLocalizations(locale).estopCompactStop,
                ),
              ),
              matching: find.byType(RichText),
            ),
          );
          final drawn = MatrixUtils.transformRect(
            label.getTransformTo(null),
            Offset.zero & label.size,
          );
          expect(
            label.didExceedMaxLines,
            isFalse,
            reason: 'the Stop label is cut',
          );
          expect(
            drawn.left >= button.left &&
                drawn.top >= button.top &&
                drawn.right <= button.right &&
                drawn.bottom <= button.bottom,
            isTrue,
            reason: 'the Stop label $drawn lies inside its button $button',
          );
          final end = tester
              .widget<ListView>(find.byKey(const Key('simple-monitor-column')))
              .padding!
              .resolve(TextDirection.ltr)
              .bottom;
          expect(
            end,
            greaterThan(tester.getSize(stop).height),
            reason: 'the column\'s end scrolls clear of Stop',
          );
        },
      );
    }
  }

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

  for (final name in _sections) {
    testWidgets(
      'on $name every panel keeps both edges on the screen, closed and with its panel open',
      (tester) async {
        await _pump(tester, _portrait);
        await tapSection(tester, name);
        Future<void> onScreen(String why) async {
          await tester.pump(const Duration(milliseconds: 500));
          expect(tester.takeException(), isNull, reason: why);
          for (final box
              in find
                  .byType(SimplePanel)
                  .evaluate()
                  .map((e) => e.renderObject! as RenderBox)) {
            final r = box.localToGlobal(Offset.zero) & box.size;
            expect(r.left, greaterThanOrEqualTo(0), reason: '$why: $r');
            expect(r.right, lessThanOrEqualTo(390), reason: '$why: $r');
          }
        }

        await onScreen(name);
        await tester.tap(find.byKey(const Key('simple-settings')));
        await onScreen('$name after the chip');
      },
    );
  }

  testWidgets(
    'on a phone upright an open panel hides the shutter hint rather than cut through it',
    (tester) async {
      await _pump(tester, _portrait);
      final hint = find.text('Tap the shutter to take a frame');
      expect(hint, findsOneWidget, reason: 'nothing open: the hint shows');
      // P.A.'s short panel opens across the middle of the screen.
      await tapSection(tester, 'polarAlign');
      await tester.pump(const Duration(milliseconds: 500));
      expect(hint, findsNothing, reason: 'the hint shows half under the panel');
      await tester.tap(find.byKey(const Key('simple-settings')));
      await tester.pump(const Duration(milliseconds: 500));
      expect(
        hint,
        findsOneWidget,
        reason: 'the panel closed: the hint is back',
      );
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
      // Still reads as STOP: the word is drawn, in a colour of its own.
      final word = find.descendant(of: stop, matching: find.text('STOP'));
      expect(word, findsOneWidget);
      final ink = tester
          .renderObject<RenderParagraph>(
            find.descendant(of: word, matching: find.byType(RichText)),
          )
          .text
          .style!
          .color!;
      final fill = tester
          .widget<Material>(
            find.descendant(
              of: find.descendant(
                of: stop,
                matching: find.byType(FilledButton),
              ),
              matching: find.byType(Material),
            ),
          )
          .color!;
      expect(ink.a, greaterThan(0.5), reason: 'the word is not see-through');
      expect(ink, isNot(fill), reason: 'the word differs from the fill');
      final g = await tester.startGesture(tester.getCenter(stop));
      await tester.pump(const Duration(milliseconds: 300));
      expect(find.text('HOLD'), findsNothing);
      await g.up();
      await tester.pump();
    },
  );

  for (final (locale, scale) in _textSizes) {
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
          // Drawn at its size: no label is shrunk to fit its button.
          final drawn = MatrixUtils.transformRect(
            p.getTransformTo(null),
            Offset.zero & p.size,
          );
          expect(
            drawn.height,
            greaterThanOrEqualTo(p.textScaler.scale(p.text.style!.fontSize!)),
            reason: '"${p.text.toPlainText()}" is shrunk',
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

  // 430 px wide: one row; 412, 390 and 360: two.
  for (final size in [
    _portrait,
    const Size(360, 780),
    const Size(412, 915),
    const Size(430, 932),
  ]) {
    for (final locale in [const Locale('en'), const Locale('pt', 'BR')]) {
      testWidgets(
        'at $size in $locale the deck keeps 8 px between Sky Map, Info, Library, the shutter, the chip and STOP',
        (tester) async {
          await _pump(tester, size, locale: locale);
          Rect at(String key) => tester.getRect(find.byKey(Key(key)));
          for (final (what, a, b) in [
            ('Sky Map to Info', at('simple-sky'), at('simple-info')),
            ('Info to Library', at('simple-info'), at('simple-library')),
            (
              'Library to the shutter',
              at('simple-library'),
              at('simple-shutter'),
            ),
            (
              'the shutter to the chip',
              at('simple-shutter'),
              at('simple-settings'),
            ),
            ('the chip to STOP', at('simple-settings'), tester.getRect(stop)),
          ]) {
            // A third of the split row is not a whole pixel.
            expect(
              _gap(a, b),
              greaterThanOrEqualTo(8 - precisionErrorTolerance),
              reason: '$what: $a, $b',
            );
          }
        },
      );
    }
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

  testWidgets(
    'on a phone the camera pill is a line of the status card, with the messages, not a pill of its own',
    (tester) async {
      final c = await _pump(tester, _portrait);
      c
          .read(captureProgressProvider.notifier)
          .beginExposing(const Duration(seconds: 5));
      c
          .read(simpleMessagesProvider.notifier)
          .show(SimpleMessageKind.warning, 'Check the cable');
      await tester.pump();
      final card = find.byType(SimpleStatusCard);
      expect(card, findsOneWidget);
      expect(
        find.descendant(of: card, matching: find.byType(SimpleStatusPill)),
        findsOneWidget,
      );
      expect(
        find.descendant(
          of: card,
          matching: find.textContaining('Taking a frame'),
        ),
        findsOneWidget,
      );
      expect(
        find.descendant(of: card, matching: find.text('Check the cable')),
        findsOneWidget,
        reason: 'the message is in the same card',
      );
      expect(find.byType(SimpleStatusPill), findsOneWidget);
      c.read(captureProgressProvider.notifier).reset();
      await tester.pump(const Duration(seconds: 1));
    },
  );
}
