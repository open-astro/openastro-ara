import 'package:flutter/material.dart';
import 'package:flutter/semantics.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/simple/simple_shell.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/state/settings/settings_nav.dart'
    show kLiveTabIndex;
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/simple_preflight_state.dart';
import 'package:openastroara/state/server_state.dart'
    show discoveredServersProvider;
import 'package:openastroara/models/server.dart';
import 'package:openastroara/widgets/emergency_stop_button.dart';

// #1911 slice 1: the right rail is a tab list of the six modes, in the order of
// the night. The shell's own harness lives in simple_shell_test.dart, which
// other open PRs edit; this file keeps a small copy of it.

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

/// The six modes of the rail, top to bottom, with the key each tab carries.
const _order = <(SimpleModeScreen, String)>[
  (SimpleModeScreen.preview, 'preview'),
  (SimpleModeScreen.focus, 'focus'),
  (SimpleModeScreen.polarAlign, 'polar'),
  (SimpleModeScreen.guide, 'guider'),
  (SimpleModeScreen.plan, 'plan'),
  (SimpleModeScreen.calibration, 'calibration'),
];

Finder _tab(String name) => find.byKey(Key('simple-rail-tab-$name'));

Future<ProviderContainer> _pump(WidgetTester tester, Size size) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final container = ProviderContainer(
    overrides: [
      companionPlatformProvider.overrideWithValue(TargetPlatform.linux),
      simpleModePrefsProvider.overrideWithValue(_MemPrefs()),
      discoveredServersProvider.overrideWith(
        (ref) => const Stream<AraServer>.empty(),
      ),
      selectedTabIndexProvider.overrideWith(_StartOnLive.new),
    ],
  );
  addTearDown(container.dispose);
  await container.read(simpleModeProvider.future);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(
        home: SimpleShell(planetarium: SizedBox(key: Key('sky'))),
      ),
    ),
  );
  await tester.pump();
  return container;
}

void main() {
  const desktop = Size(1280, 720);
  const ipad = Size(1024, 720);

  for (final size in [desktop, ipad]) {
    group('rail tabs at ${size.width.toInt()}', () {
      testWidgets(
        'the rail shows the six modes in the order of the night, each captioned',
        (tester) async {
          await _pump(tester, size);
          final dy = <double>[];
          for (final (_, name) in _order) {
            expect(_tab(name), findsOneWidget, reason: 'a tab for $name');
            dy.add(tester.getCenter(_tab(name)).dy);
          }
          expect(
            [...dy]..sort(),
            dy,
            reason: 'Preview, Focus, Polar align, Guider, Plan, Calibration, top to bottom',
          );
          for (final label in [
            'Preview',
            'Focus',
            'Polar align',
            'Guider',
            'Plan',
            'Calibration',
          ]) {
            expect(
              find.descendant(
                of: find.byKey(const Key('simple-rail-tabs')),
                matching: find.text(label),
              ),
              findsWidgets,
              reason: '$label is a caption on the rail',
            );
          }
        },
      );

      testWidgets(
        'one tap on a tab switches the mode, with no mode grid in between',
        (tester) async {
          final c = await _pump(tester, size);
          await tester.tap(_tab('focus'));
          await tester.pump();
          expect(c.read(simpleShowingProvider), SimpleModeScreen.focus);
          expect(find.byKey(const Key('simple-mode-grid')), findsNothing);
          expect(
            find.byKey(const Key('simple-mode-calibration')),
            findsNothing,
            reason: 'the grid tiles are gone',
          );
        },
      );

      testWidgets(
        'the rail is a tab list: the open mode is the one selected tab',
        (tester) async {
          final c = await _pump(tester, size);
          for (final (mode, name) in _order) {
            c.read(simpleShowingProvider.notifier).pick(mode);
            await tester.pump();
            for (final (_, other) in _order) {
              expect(
                tester.getSemantics(_tab(other)),
                isSemantics(
                  role: SemanticsRole.tab,
                  isSelected: other == name,
                  hasSelectedState: true,
                  isFocusable: true,
                  hasEnabledState: true,
                  isEnabled: true,
                  hasTapAction: true,
                  hasFocusAction: true,
                ),
                reason: '$other tab while $name is open',
              );
            }
          }
        },
      );

      testWidgets(
        'keys 1 to 6 pick the mode in the order of the night; the arrow keys move along the tabs',
        (tester) async {
          final c = await _pump(tester, size);
          const digits = [
            LogicalKeyboardKey.digit1,
            LogicalKeyboardKey.digit2,
            LogicalKeyboardKey.digit3,
            LogicalKeyboardKey.digit4,
            LogicalKeyboardKey.digit5,
            LogicalKeyboardKey.digit6,
          ];
          for (var i = 0; i < 6; i++) {
            await tester.sendKeyEvent(digits[i]);
            await tester.pump();
            expect(
              c.read(simpleShowingProvider),
              _order[i].$1,
              reason: 'key ${i + 1}',
            );
          }
          await tester.sendKeyEvent(LogicalKeyboardKey.digit2);
          await tester.pump();
          await tester.tap(_tab('focus'));
          await tester.pump();
          await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
          await tester.pump();
          expect(
            c.read(simpleShowingProvider),
            SimpleModeScreen.polarAlign,
            reason: 'arrow down: next tab',
          );
          await tester.sendKeyEvent(LogicalKeyboardKey.arrowUp);
          await tester.sendKeyEvent(LogicalKeyboardKey.arrowUp);
          await tester.pump();
          expect(
            c.read(simpleShowingProvider),
            SimpleModeScreen.preview,
            reason: 'arrow up twice: Preview',
          );
        },
      );

      testWidgets(
        'Emergency Stop stays on screen while the tabs change (guard for slice 1)',
        (tester) async {
          final c = await _pump(tester, size);
          expect(find.byType(EmergencyStopButton), findsOneWidget);
          c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.plan);
          await tester.pump();
          expect(find.byType(EmergencyStopButton), findsOneWidget);
        },
      );
    });
  }
}
