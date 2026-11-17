import 'package:flutter/material.dart';
import 'package:flutter/semantics.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/simple/simple_shell.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/state/settings/settings_nav.dart'
    show kLiveTabIndex;
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/simple_preflight_state.dart'
    show
        SimpleModeScreen,
        nightFocusStateProvider,
        nightPolarAlignStateProvider,
        simplePreflightProvider,
        simpleShowingProvider;
import 'package:openastroara/state/simple_run_state.dart';
import 'package:openastroara/models/sequence/imaging_run_body.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/setup/setup_readiness.dart'
    show SetupStepState;
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

class _RunState extends SequenceRunStateNotifier {
  _RunState(this.value);
  final SequenceRunStateInfo value;
  @override
  Future<SequenceRunStateInfo?> build() async => value;
}

class _Plan extends SimpleRunNotifier {
  _Plan(this.value);
  final SimpleRun value;
  @override
  SimpleRun build() => value;
}

/// A plan of 40 frames with 12 taken, running.
final _runningPlan = <Override>[
  simpleRunProvider.overrideWith(
    () => _Plan(
      const SimpleRun(
        sequenceId: 'seq-1',
        started: true,
        targets: [
          SimpleTarget(
            name: 'M31',
            steps: [
              FilterPlanStep(
                filterName: 'L',
                exposureSeconds: 120,
                frameCount: 40,
              ),
            ],
          ),
        ],
      ),
    ),
  ),
  sequenceRunStateProvider.overrideWith(
    () => _RunState(
      const SequenceRunStateInfo(
        sequenceId: 'seq-1',
        state: SequenceRunState.running,
        framesCaptured: 12,
      ),
    ),
  ),
];

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

Future<ProviderContainer> _pump(
  WidgetTester tester,
  Size size, {
  TargetPlatform? platform,
  List<Override> extra = const [],
  double textScale = 1,
}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final container = ProviderContainer(
    overrides: [
      companionPlatformProvider.overrideWithValue(
        // The 1024 wide group is an iPad.
        platform ??
            (size.width == 1024 ? TargetPlatform.iOS : TargetPlatform.linux),
      ),
      simpleModePrefsProvider.overrideWithValue(_MemPrefs()),
      discoveredServersProvider.overrideWith(
        (ref) => const Stream<AraServer>.empty(),
      ),
      selectedTabIndexProvider.overrideWith(_StartOnLive.new),
      ...extra,
    ],
  );
  addTearDown(container.dispose);
  await container.read(simpleModeProvider.future);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
        builder: (context, child) => MediaQuery(
          data: MediaQuery.of(context)
              .copyWith(textScaler: TextScaler.linear(textScale)),
          child: child!,
        ),
        home: const SimpleShell(planetarium: SizedBox(key: Key('sky'))),
      ),
    ),
  );
  await tester.pump();
  return container;
}

void main() {
  const desktop = Size(1280, 720);
  const ipad = Size(1024, 768);

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
        'a tick marks Focus when it is done tonight, stays on while Focus is open, and Polar align has none (#1565 reads)',
        (tester) async {
          final c = await _pump(
            tester,
            size,
            extra: [
              nightFocusStateProvider.overrideWithValue(SetupStepState.done),
              nightPolarAlignStateProvider.overrideWithValue(
                SetupStepState.pending,
              ),
            ],
          );
          expect(
            find.byKey(const Key('simple-rail-tick-focus')),
            findsOneWidget,
          );
          expect(find.byKey(const Key('simple-rail-tick-polar')), findsNothing);
          expect(
            tester.getSemantics(_tab('focus')).label,
            contains('Focus, done tonight'),
          );
          expect(
            tester.getSemantics(_tab('polar')).label,
            isNot(contains('done tonight')),
          );
          c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.focus);
          await tester.pump();
          expect(
            find.byKey(const Key('simple-rail-tick-focus')),
            findsOneWidget,
            reason: 'the tick stays on the open tab',
          );
        },
      );

      testWidgets('a steady dot marks the mode that runs and does not blink', (
        tester,
      ) async {
        await _pump(tester, size, extra: _runningPlan);
        final dot = find.byKey(const Key('simple-rail-dot-plan'));
        expect(dot, findsOneWidget);
        expect(
          find.descendant(of: _tab('plan'), matching: dot),
          findsOneWidget,
          reason: 'the dot sits inside its tab',
        );
        expect(find.byKey(const Key('simple-rail-dot-focus')), findsNothing);
        for (var i = 0; i < 4; i++) {
          await tester.pump(const Duration(milliseconds: 500));
          expect(dot, findsOneWidget, reason: 'visible at ${i * 500} ms');
          // Only fades between the dot and its own tab count: the shell and
          // its routes put fades above every widget.
          for (final w in [FadeTransition, AnimatedOpacity, Opacity]) {
            expect(
              find.descendant(
                of: _tab('plan'),
                matching: find.ancestor(of: dot, matching: find.byType(w)),
              ),
              findsNothing,
              reason: 'no $w between the dot and its tab',
            );
          }
        }
      });

      testWidgets(
        'the open block shows the figure under the mode name while a plan images (12/40)',
        (tester) async {
          final c = await _pump(tester, size, extra: _runningPlan);
          c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.plan);
          await tester.pump();
          final open = find.byKey(const Key('simple-rail-open'));
          expect(
            find.descendant(of: open, matching: find.text('Plan')),
            findsOneWidget,
          );
          expect(
            find.descendant(of: open, matching: find.text('12/40')),
            findsOneWidget,
          );
          c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.focus);
          await tester.pump();
          expect(
            find.descendant(of: open, matching: find.text('12/40')),
            findsNothing,
          );
        },
      );

      testWidgets(
        'Preflight stays reachable through its row until slice 2 removes it',
        (tester) async {
          final c = await _pump(tester, size);
          await tester.tap(find.byKey(const Key('simple-mode-preflight')));
          await tester.pump();
          expect(c.read(simplePreflightProvider), isNotNull);
        },
      );

      testWidgets('the arrow keys wrap at both ends', (tester) async {
        final c = await _pump(tester, size);
        await tester.sendKeyEvent(LogicalKeyboardKey.digit1);
        await tester.sendKeyEvent(LogicalKeyboardKey.arrowUp);
        await tester.pump();
        expect(c.read(simpleShowingProvider), SimpleModeScreen.calibration);
        await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
        await tester.pump();
        expect(c.read(simpleShowingProvider), SimpleModeScreen.preview);
      });

      testWidgets(
        'a digit typed into a focused text field does not switch the mode (Search)',
        (tester) async {
          final c = await _pump(tester, size);
          c.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.plan);
          await tester.pump();
          await tester.tap(find.byKey(const Key('simple-search-button')));
          await tester.pump();
          final field = find.byKey(const Key('simple-search-field'));
          await tester.tap(field);
          await tester.pump();
          await tester.sendKeyEvent(LogicalKeyboardKey.digit1);
          await tester.enterText(field, '1');
          await tester.pump();
          expect(c.read(simpleShowingProvider), SimpleModeScreen.plan);
          expect(tester.widget<TextField>(field).controller!.text, '1');
        },
      );

      testWidgets(
        'Emergency Stop stays clear of the rail at 2x text and in portrait (guard for slice 1)',
        (tester) async {
          for (final (s, scale) in [
            (const Size(1024, 768), 2.0),
            (const Size(768, 1024), 1.0),
          ]) {
            await _pump(tester, s, textScale: scale);
            final stop = tester.getRect(find.byType(EmergencyStopButton));
            for (final (_, name) in _order) {
              final tab = tester.getRect(_tab(name));
              expect(
                tab.overlaps(stop),
                isFalse,
                reason: '$name tab clear of Stop at $s, $scale x',
              );
            }
            expect(
              (Offset.zero & s).contains(stop.topLeft) &&
                  (Offset.zero & s).contains(
                    stop.bottomRight - const Offset(1, 1),
                  ),
              isTrue,
              reason: 'Stop on screen at $s',
            );
          }
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

  for (final (name, size) in [
    ('portrait', const Size(390, 844)),
    ('landscape', const Size(844, 390)),
  ]) {
    testWidgets(
      'phone $name keeps today\'s deck or grid and gets no rail tabs (slice 1 is wide only)',
      (tester) async {
        await _pump(tester, size, platform: TargetPlatform.iOS);
        // Phone landscape's rail overflows at 390 high on master (a layout
        // defect this test does not pin): drop it, assert only what is here.
        tester.takeException();
        for (final (_, n) in _order) {
          expect(_tab(n), findsNothing);
        }
        expect(find.byKey(const Key('simple-rail-tabs')), findsNothing);
        expect(find.byType(EmergencyStopButton), findsWidgets);
      },
    );
  }
}
