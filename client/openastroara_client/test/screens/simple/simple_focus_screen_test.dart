import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/autofocus_run.dart';
import 'package:openastroara/models/bahtinov_focus.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/screens/simple/simple_focus_panel.dart';
import 'package:openastroara/screens/simple/simple_focus_screen.dart';
import 'package:openastroara/screens/simple/simple_polar_screen.dart';
import 'package:openastroara/state/focus/autofocus_live_state.dart';
import 'package:openastroara/state/focus/bahtinov_focus_state.dart';
import 'package:openastroara/state/focus/guide_focus_state.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';

// A 1x1 PNG.
final _png = Uint8List.fromList(const [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0, 31, 21, 196, 137, 0, 0, 0, 13, 73, 68, 65, 84, 120, 156, 99, 248, 15, 4, 0, 9, 251, 3, 253, 227, 85, 242, 156, 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130]);

class _LiveFrame extends AutofocusLiveNotifier {
  @override
  AutofocusLive build() =>
      AutofocusLive(run: const AutofocusRun(state: AutofocusRunStates.complete, finalHfr: 1.01), frame: _png);
}

/// The daemon's run record as the real notifier folds it.
class _LiveSnapshots extends AutofocusLiveNotifier {
  @override
  AutofocusLive build() => AutofocusLive.idle;
  @override
  Future<void> refresh() async {}
  void snapshot(AutofocusRun run) => state = applyRunSnapshot(state, run);
}

class _BahtinovFrame extends BahtinovFocusNotifier {
  @override
  BahtinovFocusLive build() => BahtinovFocusLive(status: BahtinovFocusStatus.idle, frame: _png, frameSeq: 1);
}

class _BahtinovMeasured extends BahtinovFocusNotifier {
  int finishes = 0;
  @override
  BahtinovFocusLive build() {
    const sample = BahtinovSample(seq: 1, detected: true, offsetPx: 1.0, withinZone: false);
    return const BahtinovFocusLive(
      status: BahtinovFocusStatus(
          active: false, state: BahtinovFocusStates.stopped, exposureSec: 2, seq: 1, latest: sample,
          recent: [sample], zonePx: 0.3, zoneFromOptics: false, hasFrame: false),
    );
  }

  @override
  Future<void> finish() async {
    finishes++;
    state = state.copyWith(finished: true);
  }
}

class _GuideFrame extends GuideFocusNotifier {
  @override
  GuideFocusLive build() => GuideFocusLive(frame: _png, frameSeq: 1);
}

class _PolarRun extends PolarAlignLiveNotifier {
  @override
  PolarAlignLive build() => const PolarAlignLive(phase: PolarAlignStates.adjusting);
  void fail(String why) => state = PolarAlignLive(phase: PolarAlignStates.failed, errorMessage: why);
}

/// Renders [frame] of the Focus Screen (a key when it has none) and calls
/// every Screen's listen, as the shell does, with no shell.
Future<ProviderContainer> _pump(WidgetTester tester, List<Override> overrides,
    {void Function(BuildContext, WidgetRef)? onBuild}) async {
  final c = ProviderContainer(overrides: overrides);
  addTearDown(c.dispose);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: c,
    child: MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: Consumer(builder: (context, ref, _) {
        const SimpleFocusScreen().listen(context, ref);
        const SimplePolarScreen().listen(context, ref);
        onBuild?.call(context, ref);
        return const SimpleFocusScreen().frame(ref) ?? const SizedBox(key: Key('no-frame'));
      }),
    ),
  ));
  await tester.pump();
  return c;
}

void main() {
  testWidgets("Focus: Measured stars On fills the screen with the run's frame", (tester) async {
    final c = await _pump(tester, [
      autofocusLiveProvider.overrideWith(_LiveFrame.new),
      effectiveFocusMethodProvider.overrideWithValue(MainFocusMethod.autofocus),
    ]);
    expect(find.byKey(const Key('no-frame')), findsOneWidget, reason: 'off to start');
    c.read(simpleFocusStarsShownProvider.notifier).set(true);
    await tester.pump();
    expect(find.byKey(const Key('simple-focus-frame')), findsOneWidget);
  });

  testWidgets('Focus on Manual fills the screen with the Bahtinov readout frame', (tester) async {
    await _pump(tester, [
      bahtinovFocusProvider.overrideWith(_BahtinovFrame.new),
      effectiveFocusMethodProvider.overrideWithValue(MainFocusMethod.bahtinov),
    ]);
    expect(find.byKey(const Key('simple-focus-frame')), findsOneWidget);
  });

  testWidgets("Focus on the guide camera fills the screen with the guide camera's frames", (tester) async {
    final c = await _pump(tester, [
      guideFocusProvider.overrideWith(_GuideFrame.new),
      effectiveFocusMethodProvider.overrideWithValue(MainFocusMethod.autofocus),
    ]);
    expect(find.byKey(const Key('no-frame')), findsOneWidget, reason: 'the main telescope to start');
    c.read(simpleFocusScopeProvider.notifier).set(SimpleFocusScope.guide);
    await tester.pump();
    expect(find.byKey(const Key('simple-focus-frame')), findsOneWidget);
  });

  testWidgets('leaving Focus with a Bahtinov measurement not finished asks for the mask in a pill (ALP-4982)',
      (tester) async {
    final b = _BahtinovMeasured();
    late BuildContext ctx;
    late WidgetRef r;
    final c = await _pump(tester, [
      bahtinovFocusProvider.overrideWith(() => b),
      effectiveFocusMethodProvider.overrideWithValue(MainFocusMethod.bahtinov),
    ], onBuild: (context, ref) {
      ctx = context;
      r = ref;
    });
    const SimpleFocusScreen().onLeave(ctx, r);
    final pill = c.read(simpleMessagesProvider).single;
    expect((pill.kind, pill.title), (SimpleMessageKind.question, 'Take the Bahtinov mask off'));
    pill.actions.last.onPressed(); // Mask removed
    expect(b.finishes, 1);
  });

  testWidgets('an autofocus that failed before this launch posts no pill (ALP-4998)', (tester) async {
    final live = _LiveSnapshots();
    final c = await _pump(tester, [autofocusLiveProvider.overrideWith(() => live)]);
    live.snapshot(const AutofocusRun(state: AutofocusRunStates.failed, reason: 'focuser error'));
    await tester.pump();
    expect(c.read(simpleMessagesProvider), isEmpty);
  });

  testWidgets('an autofocus that fails while the app watches says so in a pill', (tester) async {
    final live = _LiveSnapshots();
    final c = await _pump(tester, [autofocusLiveProvider.overrideWith(() => live)]);
    live.snapshot(const AutofocusRun(state: AutofocusRunStates.idle));
    await tester.pump();
    live.snapshot(const AutofocusRun(state: AutofocusRunStates.running));
    await tester.pump();
    live.snapshot(const AutofocusRun(state: AutofocusRunStates.failed, reason: 'focuser error'));
    await tester.pump();
    expect(c.read(simpleMessagesProvider).single.detail, 'Focuser error.');
  });

  testWidgets('a failed polar alignment says so, with why, as a message', (tester) async {
    final polar = _PolarRun();
    final c = await _pump(tester, [polarAlignLiveProvider.overrideWith(() => polar)]);
    polar.fail('no stars found');
    await tester.pump();
    final m = c.read(simpleMessagesProvider).single;
    expect((m.kind, m.title, m.detail), (SimpleMessageKind.error, 'Polar alignment failed', 'no stars found'));
  });
}
