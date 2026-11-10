import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/semantics.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/screens/simple/simple_guide_panel.dart';
import 'package:openastroara/services/guider_api.dart' show GuiderClient, GuideStartFailed;
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/models/guiding_assistant.dart';
import 'package:openastroara/state/guider/guider_live_state.dart';
import 'package:openastroara/models/guider_calibration_view.dart';
import 'package:openastroara/models/guider_live.dart';
import 'package:openastroara/services/guider_live_api.dart' show GuiderLiveClient;
import 'package:openastroara/screens/simple/simple_shell.dart' show SimpleSegments;
import 'package:openastroara/state/guider/guiding_assistant_state.dart';
import 'package:openastroara/state/imaging/capture_progress_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';
import 'package:openastroara/services/guider_equipment_api.dart' show GuiderEquipmentClient;
import 'package:openastroara/services/profile_api.dart' show ProfileApi;
import 'package:openastroara/state/guider/guider_equipment_state.dart' show guiderEquipmentApiProvider;
import 'package:openastroara/state/profile_management_state.dart' show profileApiProvider;
import 'package:openastroara/screens/simple/simple_device_windows.dart' show SimpleGuideGainSlider;
import 'package:openastroara/screens/simple/simple_preview_panel.dart' show SimplePresetTile;
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_mode_state.dart' show simpleModeActiveProvider;
import 'package:openastroara/widgets/equipment/guider_chip.dart' show guiderStatusLevel;
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/widgets/status_indicator.dart' show StatusLevel;

/// The Queue's run state, as the rig last said it.
class _Run extends SequenceRunStateNotifier {
  _Run(this.run);
  final SequenceRunState run;
  @override
  Future<SequenceRunStateInfo?> build() async => SequenceRunStateInfo(sequenceId: 's', runId: 'r', state: run);
}

class _Guider extends GuiderStatusNotifier {
  _Guider(this.json, {this.lag = false});
  final Map<String, dynamic> json;
  final calls = <String>[];

  /// What the start throws: the daemon's word that it failed (#1664).
  Object? startError;

  /// The rig acts a moment after the ask: the state doesn't change yet.
  final bool lag;

  // No server here: a read keeps what the rig last said.
  @override
  Future<void> refresh([GuiderClient? client]) async {}
  @override
  Future<GuiderStatus?> build() async => GuiderStatus.fromJson(json);
  // As the rig: the routine starts (or stops) and the next read shows it.
  @override
  Future<void> startGuiding() async {
    calls.add('start');
    if (startError != null) throw startError!;
    if (lag) return;
    state = AsyncData(GuiderStatus.fromJson({...json, 'runtime': <String, dynamic>{...json['runtime'] as Map<String, dynamic>, 'state': 'looping'}}));
  }
  @override
  Future<void> stopGuiding() async {
    calls.add('stop');
    if (lag) return;
    state = AsyncData(GuiderStatus.fromJson({...json, 'runtime': <String, dynamic>{...json['runtime'] as Map<String, dynamic>, 'state': 'stopped'}}));
  }
}

Map<String, dynamic> _wire({String state = 'connected', String runtime = 'guiding'}) => {
      'device_id': 'g',
      'name': 'PHD2',
      'state': state,
      'runtime': {
        'state': runtime,
        'rms_total': 0.9,
        'rms_total_arcsec': 0.62,
        'rms_ra_arcsec': 0.41,
        'rms_dec_arcsec': 0.47,
      },
    };

late _Guider _g;

/// The rig's profile store: a PUT answers with what was sent.
class _Profile implements ProfileApi {
  @override
  Future<Phd2Settings> putPhd2Settings(Phd2Settings s) async => s;
  @override
  dynamic noSuchMethod(Invocation i) => throw UnimplementedError('${i.memberName}');
}

/// The guider's equipment API: records every tuning push.
class _GuiderEquipment implements GuiderEquipmentClient {
  final pushes = <bool>[];
  @override
  Future<void> pushProfile({bool tuningOnly = false}) async => pushes.add(tuningOnly);
  @override
  dynamic noSuchMethod(Invocation i) => throw UnimplementedError('${i.memberName}');
}

Future<ProviderContainer> _pump(WidgetTester tester, Map<String, dynamic> wire,
    {Widget child = const SimpleGuidePanel(), bool lag = false, List<Override> rig = const []}) async {
  // Tall enough that the whole panel is on screen, so nothing needs scrolling.
  tester.view.physicalSize = const Size(2400, 3600);
  addTearDown(tester.view.resetPhysicalSize);
  _g = _Guider(wire, lag: lag);
  final c = ProviderContainer(overrides: [guiderStatusProvider.overrideWith(() => _g), ...rig]);
  addTearDown(c.dispose);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: c,
    child: MaterialApp(home: Scaffold(body: Center(child: child))),
  ));
  await tester.pump();
  return c;
}

GuiderLiveState _liveState({String app = 'Stopped', bool lock = false, bool calibrated = true}) => GuiderLiveState(
      live: GuiderLive(appState: app, lockX: lock ? 1 : null, lockY: lock ? 1 : null),
      calibration: GuiderCalibrationView.fromJson({
        'connected': true,
        'data': {'calibrated': calibrated},
      }),
    );

class _Live extends GuiderLiveNotifier {
  _Live([this.initial]);
  final GuiderLiveState? initial;
  @override
  GuiderLiveState build() => initial ?? _liveState();
  @override
  Future<void> refresh() async {}
  @override
  Future<void> clearCalibration() async => _g.calls.add('clear');
  @override
  Future<void> findStar() async {
    _g.calls.add('find');
    state = _liveState(app: 'Selected', lock: true);
  }
}

/// The rig refuses to forget its calibration: the real notifier reads this.
class _SlowLive extends _Live {
  final clear = Completer<void>();
  int clears = 0;
  @override
  Future<void> clearCalibration() {
    clears++;
    return clear.future;
  }
}

class _RefusingLive implements GuiderLiveClient {
  @override
  Future<GuiderLive> getLive() async => GuiderLive.disconnected;
  @override
  Future<GuiderCalibrationView> getCalibration() async => GuiderCalibrationView.disconnected;
  @override
  Future<void> clearCalibration() async => throw Exception('refused');
  @override
  void close() {}
  @override
  dynamic noSuchMethod(Invocation i) => throw UnimplementedError('${i.memberName}');
}

class _Assistant extends GuidingAssistantNotifier {
  _Assistant(this.status);
  final GuidingAssistantStatus status;
  final started = <bool>[];
  final applied = <List<String>>[];
  @override
  GuidingAssistantState build() => GuidingAssistantState(status: status, loaded: true);
  @override
  Future<void> start({double? durationSec, required bool measureBacklash}) async {
    expect(durationSec, isNull, reason: 'no options: the guider picks');
    started.add(measureBacklash);
  }

  @override
  Future<void> apply(List<String> which) async => applied.add(which);

  void fail(String error) => state = state.copyWith(error: error);
  void report(GuidingAssistantStatus status) => state = state.copyWith(status: status);
}

GuidingAssistantStatus _assistant(String state, {Map<String, dynamic>? recs}) =>
    GuidingAssistantStatus.fromJson({'connected': true, 'state': state, 'recommendations': ?recs});

final _idleAssistant = _assistant('idle');

Future<void> _tab(WidgetTester tester, String name) async {
  await tester.tap(find.byKey(Key('simple-guider-tab-$name')));
  await tester.pump();
}

void main() {
  group('the Guider tabs (#1722)', () {
    List<Override> rig(_GuiderEquipment e, {_Assistant? assistant, SequenceRunState? run, GuiderLiveState? live}) => [
          simpleModeActiveProvider.overrideWithValue(true),
          profileApiProvider.overrideWithValue(_Profile()),
          guiderEquipmentApiProvider.overrideWithValue(e),
          guiderLiveProvider.overrideWith(() => _Live(live)),
          guidingAssistantProvider.overrideWith(() => assistant ?? _Assistant(_idleAssistant)),
          if (run != null) sequenceRunStateProvider.overrideWith(() => _Run(run)),
        ];
    FilledButton button(WidgetTester t, String key) => t.widget<FilledButton>(find.byKey(Key(key)));

    testWidgets('Tuning and Tools hold the new controls; the Guide tab opens first', (tester) async {
      await _pump(tester, _wire(runtime: 'stopped'), rig: rig(_GuiderEquipment()));
      expect(find.byKey(const Key('simple-guide-exposure')), findsOneWidget);
      expect(find.byKey(const Key('simple-window-row-RA minimum move')), findsNothing);
      await _tab(tester, 'tuning');
      expect(find.byKey(const Key('simple-window-row-RA minimum move')), findsOneWidget);
      await _tab(tester, 'tools');
      expect(find.byKey(const Key('simple-guider-recalibrate')), findsOneWidget);
      expect(find.byKey(const Key('simple-guider-assistant')), findsOneWidget);
      expect(find.text('Build dark library'), findsNothing);
    });

    testWidgets('a minimum move goes to the Guider live', (tester) async {
      final e = _GuiderEquipment();
      final c = await _pump(tester, _wire(), rig: rig(e));
      final before = c.read(phd2SettingsProvider).minimumMove;
      await _tab(tester, 'tuning');
      await tester.tap(find.byKey(const Key('simple-guider-min-move-ra-up')));
      await tester.pump();
      await tester.pump();
      expect(c.read(phd2SettingsProvider).minimumMove, closeTo(before + 0.05, 1e-9));
      expect(e.pushes, [true]);
    });

    testWidgets('a settle change is saved for the next dither, with no live push', (tester) async {
      final e = _GuiderEquipment();
      final c = await _pump(tester, _wire(), rig: rig(e));
      await _tab(tester, 'tuning');
      await tester.tap(find.byKey(const Key('simple-guider-settle-time-up')));
      await tester.pump();
      await tester.pump();
      expect(c.read(phd2SettingsProvider).settleTimeSec, 11);
      expect(e.pushes, isEmpty);
    });

    testWidgets('Recalibrate now forgets the calibration, then starts the routine', (tester) async {
      await _pump(tester, _wire(runtime: 'stopped'), rig: rig(_GuiderEquipment()));
      await _tab(tester, 'tools');
      await tester.tap(find.byKey(const Key('simple-guider-recalibrate')));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 600));
      expect(_g.calls, ['clear', 'start']);
    });

    testWidgets('a running plan refuses Recalibrate now and the Guiding Assistant', (tester) async {
      await _pump(tester, _wire(runtime: 'stopped'), rig: rig(_GuiderEquipment(), run: SequenceRunState.running));
      await _tab(tester, 'tools');
      await tester.pump(); // the run state's first read
      expect(button(tester, 'simple-guider-recalibrate').onPressed, isNull);
      expect(button(tester, 'simple-guider-assistant').onPressed, isNull);
    });

    testWidgets('guiding refuses Recalibrate now and says to stop first', (tester) async {
      await _pump(tester, _wire(), rig: rig(_GuiderEquipment()));
      await _tab(tester, 'tools');
      expect(button(tester, 'simple-guider-recalibrate').onPressed, isNull);
      expect(find.text('Stop guiding to use these.'), findsOneWidget);
    });

    testWidgets('the Guiding Assistant is one action with no options', (tester) async {
      final a = _Assistant(_idleAssistant);
      await _pump(tester, _wire(runtime: 'stopped'), rig: rig(_GuiderEquipment(), assistant: a));
      await _tab(tester, 'tools');
      await tester.tap(find.byKey(const Key('simple-guider-assistant')));
      await tester.pump();
      expect(_g.calls, ['find'], reason: 'it picks its own star');
      expect(a.started, [true]);
    });

    testWidgets('the tabs are the house segments, 44 pt tall', (tester) async {
      await _pump(tester, _wire(), rig: rig(_GuiderEquipment()));
      expect(find.byType(SimpleSegments<int>), findsOneWidget);
      expect(tester.getSize(find.byKey(const Key('simple-guider-tab-tools'))).height, greaterThanOrEqualTo(44));
    });

    testWidgets('a clear the rig refuses says so and does not start guiding', (tester) async {
      final c = await _pump(tester, _wire(runtime: 'stopped'), rig: [
        simpleModeActiveProvider.overrideWithValue(true),
        guiderLiveApiProvider.overrideWithValue(_RefusingLive()),
        guidingAssistantProvider.overrideWith(() => _Assistant(_idleAssistant)),
      ]);
      await _tab(tester, 'tools');
      await tester.tap(find.byKey(const Key('simple-guider-recalibrate')));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 100));
      expect(_g.calls, isEmpty);
      expect(c.read(simpleMessagesProvider), hasLength(1));
      await tester.pump(const Duration(seconds: 4));
    });

    testWidgets('a main camera frame refuses both tools', (tester) async {
      final c = await _pump(tester, _wire(runtime: 'stopped'), rig: rig(_GuiderEquipment()));
      c.read(captureProgressProvider.notifier).beginExposing(const Duration(seconds: 30));
      await _tab(tester, 'tools');
      expect(button(tester, 'simple-guider-recalibrate').onPressed, isNull);
      expect(button(tester, 'simple-guider-assistant').onPressed, isNull);
      c.read(captureProgressProvider.notifier).reset();
      await tester.pump(const Duration(seconds: 11));
    });

    testWidgets('a start already asked for refuses Recalibrate now', (tester) async {
      final c = await _pump(tester, _wire(runtime: 'stopped'), rig: rig(_GuiderEquipment()));
      c.read(simpleGuiderPendingProvider.notifier).set(true);
      await _tab(tester, 'tools');
      expect(button(tester, 'simple-guider-recalibrate').onPressed, isNull);
    });

    testWidgets('guiding refuses the Guiding Assistant', (tester) async {
      await _pump(tester, _wire(), rig: rig(_GuiderEquipment()));
      await _tab(tester, 'tools');
      expect(button(tester, 'simple-guider-assistant').onPressed, isNull);
    });

    testWidgets('with no calibration the Guiding Assistant says so and does not start', (tester) async {
      final a = _Assistant(_idleAssistant);
      final c = await _pump(tester, _wire(runtime: 'stopped'),
          rig: rig(_GuiderEquipment(), assistant: a, live: _liveState(calibrated: false)));
      await _tab(tester, 'tools');
      await tester.tap(find.byKey(const Key('simple-guider-assistant')));
      await tester.pump();
      expect(a.started, isEmpty);
      expect(c.read(simpleMessagesProvider).single.title, contains('Calibrate'));
    });

    testWidgets('a star already picked goes straight to measuring', (tester) async {
      final a = _Assistant(_idleAssistant);
      await _pump(tester, _wire(runtime: 'stopped'),
          rig: rig(_GuiderEquipment(), assistant: a, live: _liveState(app: 'Selected', lock: true)));
      await _tab(tester, 'tools');
      await tester.tap(find.byKey(const Key('simple-guider-assistant')));
      await tester.pump();
      expect(_g.calls, isEmpty);
      expect(a.started, [true]);
    });

    testWidgets('a Guiding Assistant error shows as a pill, not a caption', (tester) async {
      final a = _Assistant(_idleAssistant);
      final c = await _pump(tester, _wire(runtime: 'stopped'), rig: rig(_GuiderEquipment(), assistant: a));
      await _tab(tester, 'tools');
      a.fail('No star selected');
      await tester.pump();
      expect(c.read(simpleMessagesProvider).single.title, contains('No star selected'));
      expect(find.text('No star selected'), findsNothing);
    });

    testWidgets('a second tap while the calibration is being forgotten does nothing', (tester) async {
      final live = _SlowLive();
      await _pump(tester, _wire(runtime: 'stopped'), rig: [
        simpleModeActiveProvider.overrideWithValue(true),
        guiderLiveProvider.overrideWith(() => live),
        guidingAssistantProvider.overrideWith(() => _Assistant(_idleAssistant)),
      ]);
      await _tab(tester, 'tools');
      await tester.tap(find.byKey(const Key('simple-guider-recalibrate')));
      await tester.pump();
      await tester.tap(find.byKey(const Key('simple-guider-recalibrate')), warnIfMissed: false);
      await tester.pump();
      expect(live.clears, 1);
      expect(_g.calls, isEmpty, reason: 'nothing starts on the old calibration');
      live.clear.complete();
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 600));
      expect(_g.calls, ['start']);
    });

    testWidgets('an old Guiding Assistant failure is not a new pill; a run that fails now is', (tester) async {
      final a = _Assistant(_idleAssistant);
      final c = await _pump(tester, _wire(runtime: 'stopped'), rig: rig(_GuiderEquipment(), assistant: a));
      await _tab(tester, 'tools');
      a.report(GuidingAssistantStatus.fromJson({'connected': true, 'state': 'failed', 'error': 'from last night'}));
      await tester.pump();
      expect(c.read(simpleMessagesProvider), isEmpty, reason: 'the first read found an old result');
      a.report(GuidingAssistantStatus.fromJson({'connected': true, 'state': 'measuring'}));
      await tester.pump();
      a.report(GuidingAssistantStatus.fromJson({'connected': true, 'state': 'failed', 'error': 'star lost'}));
      await tester.pump();
      expect(c.read(simpleMessagesProvider).single.title, contains('star lost'));
    });

    testWidgets('a failed run says why on Tools, whenever Tools opens', (tester) async {
      final a = _Assistant(GuidingAssistantStatus.fromJson({'connected': true, 'state': 'failed', 'error': 'star lost'}));
      await _pump(tester, _wire(runtime: 'stopped'), rig: rig(_GuiderEquipment(), assistant: a));
      await _tab(tester, 'tools');
      expect(find.byKey(const Key('simple-guider-assistant-failed')), findsOneWidget);
      expect(find.textContaining('star lost'), findsOneWidget);
    });

    testWidgets('a long failure reason keeps to two lines, so Tools keeps the Guide height', (tester) async {
      final a = _Assistant(GuidingAssistantStatus.fromJson({'connected': true, 'state': 'failed', 'error': 'star lost ' * 40}));
      await _pump(tester, _wire(runtime: 'stopped'), rig: rig(_GuiderEquipment(), assistant: a));
      await _tab(tester, 'tools');
      final reason = tester.widget<Text>(
          find.descendant(of: find.byKey(const Key('simple-guider-assistant-failed')), matching: find.byType(Text)));
      expect(reason.maxLines, 2);
      expect(reason.overflow, TextOverflow.ellipsis);
    });

    testWidgets('tapping a cut-off failure reason shows it whole, in place', (tester) async {
      final a = _Assistant(GuidingAssistantStatus.fromJson({'connected': true, 'state': 'failed', 'error': 'star lost ' * 40}));
      await _pump(tester, _wire(runtime: 'stopped'), rig: rig(_GuiderEquipment(), assistant: a));
      await _tab(tester, 'tools');
      final row = find.byKey(const Key('simple-guider-assistant-failed'));
      await tester.tap(row);
      await tester.pump();
      expect(tester.widget<Text>(find.descendant(of: row, matching: find.byType(Text))).maxLines, isNull);
      await tester.tap(row);
      await tester.pump();
      expect(tester.widget<Text>(find.descendant(of: row, matching: find.byType(Text))).maxLines, 2);
    });

    testWidgets('each Tuning stepper is read with its full name', (tester) async {
      final semantics = tester.ensureSemantics();
      await _pump(tester, _wire(), rig: rig(_GuiderEquipment()));
      await _tab(tester, 'tuning');
      expect(find.bySemanticsLabel(RegExp(r'^Minimum move, RA$')), findsOneWidget);
      expect(find.bySemanticsLabel(RegExp(r'^Settle after each dither, For$')), findsOneWidget);
      expect(find.bySemanticsLabel('Settle after each dither, For, More'), findsOneWidget,
          reason: 'a button reached on its own still says what it changes');
      semantics.dispose();
    });

    testWidgets('a finished Guiding Assistant applies every suggestion in one tap', (tester) async {
      final a = _Assistant(_assistant('done', recs: {'min_move_ra_px': 0.2, 'min_move_dec_px': 0.3}));
      await _pump(tester, _wire(runtime: 'stopped'), rig: rig(_GuiderEquipment(), assistant: a));
      await _tab(tester, 'tools');
      await tester.tap(find.text('Apply 2 suggestions'));
      await tester.pump();
      expect(a.applied, [['min_move_ra', 'min_move_dec']]);
    });
  });

  test('the guider follows the DESIGN.md status rule: green good (blinking while guiding), blue changing, '
      'amber warning, red error, grey off', () {
    GuiderStatus at(String state, String runtime) =>
        GuiderStatus.fromJson({'device_id': 'g', 'name': 'PHD2', 'state': state, 'runtime': {'state': runtime}});
    expect(guiderStatusLevel(at('connected', 'stopped')), StatusLevel.connected, reason: 'idle: good');
    expect(guiderStatusLevel(at('connected', 'guiding')), StatusLevel.active, reason: 'working: blinking green');
    for (final r in ['looping', 'calibrating', 'settling', 'paused']) {
      expect(guiderStatusLevel(at('connected', r)), StatusLevel.changing, reason: r);
    }
    expect(guiderStatusLevel(at('connected', 'star_lost')), StatusLevel.busy, reason: 'a warning');
    expect(guiderStatusLevel(at('error', 'stopped')), StatusLevel.error);
    expect(guiderStatusLevel(at('disconnected', 'stopped')), StatusLevel.disconnected);
    expect(StatusLevel.changing.color, AraColors.accentInfo);
    expect(StatusLevel.busy.color, AraColors.accentWarning);
  });

  testWidgets('idle and out of step: the reason replaces how to start, before Start is pressed (#1665)', (tester) async {
    final wire = _wire(runtime: 'stopped');
    (wire['runtime'] as Map<String, dynamic>)['out_of_step_reason'] = 'The Guider does not have this profile.';
    await _pump(tester, wire);
    expect(find.text('The Guider does not have this profile.'), findsOneWidget);
    expect(find.textContaining('Tap the shutter to start guiding'), findsNothing);
  });

  testWidgets('the Guide panel: what it is doing and its RMS, in arcseconds', (tester) async {
    await _pump(tester, _wire());
    expect(find.text('Guiding'), findsOneWidget);
    expect(find.text('0.62″'), findsOneWidget);
    expect(find.text('RA 0.41″ · Dec 0.47″'), findsOneWidget);
  });

  testWidgets('idle: Idle, not Not guiding or Stopped (both read as a failure, user 2026-11-04), and how to start',
      (tester) async {
    await _pump(tester, _wire(runtime: 'stopped'));
    expect(find.text('Idle'), findsOneWidget);
    expect(find.text('Not guiding'), findsNothing);
    expect(find.text('Stopped'), findsNothing);
    expect(find.textContaining('Tap the shutter to start guiding'), findsOneWidget);
    expect(find.text('—'), findsOneWidget, reason: 'no RMS while idle');
  });

  testWidgets('no guider: says to connect it', (tester) async {
    await _pump(tester, _wire(state: 'disconnected', runtime: 'stopped'));
    expect(find.text('Guider not connected'), findsOneWidget);
  });

  testWidgets("Guide's shutter starts the guider's routine", (tester) async {
    await _pump(tester, _wire(runtime: 'stopped'), child: const SimpleGuideShutter());
    expect(find.bySemanticsLabel('Start guiding'), findsOneWidget);
    await tester.tap(find.byType(SimpleGuideShutter));
    await tester.pump();
    expect(_g.calls, ['start']);
  });

  // One pump per test: a second container leaves the first's autoDispose
  // run-state read pending as a timer.
  testWidgets("while the guider's routine runs, the square stops it", (tester) async {
    await _pump(tester, _wire(runtime: 'calibrating'), child: const SimpleGuideShutter());
    expect(find.bySemanticsLabel('Stop guiding'), findsOneWidget);
    await tester.tap(find.byType(SimpleGuideShutter));
    await tester.pump();
    expect(_g.calls, ['stop']);
  });

  testWidgets('a lost star is the same colour in the panel as in the top bar and the pill: amber, a warning',
      (tester) async {
    await _pump(tester, _wire(runtime: 'star_lost'));
    final dot = find.descendant(of: find.byKey(const Key('simple-guide-panel')), matching: find.byType(Container)).evaluate()
        .map((e) => (e.widget as Container).decoration)
        .whereType<BoxDecoration>()
        .where((d) => d.shape == BoxShape.circle)
        .single;
    expect(dot.color, guiderStatusLevel(GuiderStatus.fromJson(_wire(runtime: 'star_lost'))).color);
    expect(dot.color, StatusLevel.busy.color, reason: 'DESIGN.md status rule: a lost star still searching is a warning');
  });

  testWidgets('a tap says Starting… at once, before the guider answers (iPad, 2026-11-04: feedback was slow)',
      (tester) async {
    await _pump(tester, _wire(runtime: 'stopped'), lag: true,
        child: const Column(mainAxisSize: MainAxisSize.min, children: [SimpleGuidePanel(), SimpleGuideShutter()]));
    await tester.tap(find.byType(SimpleGuideShutter));
    await tester.pump();
    expect(find.text('Starting…'), findsOneWidget);
    expect(find.bySemanticsLabel('Stop guiding'), findsNothing, reason: 'no second tap while the ask is out');
    await tester.pump(const Duration(seconds: 11)); // the guider never answered: back to what it says
    expect(find.text('Starting…'), findsNothing);
    expect(find.text('Idle'), findsOneWidget);
  });

  testWidgets('a mode change mid-ask: the shutter leaves, and Starting… still clears (review, #1490)',
      (tester) async {
    final c = await _pump(tester, _wire(runtime: 'stopped'), lag: true, child: const SimpleGuideShutter());
    await tester.tap(find.byType(SimpleGuideShutter));
    await tester.pump();
    expect(c.read(simpleGuiderPendingProvider), isTrue);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: c,
      child: const MaterialApp(home: Scaffold(body: SizedBox())),
    ));
    await tester.pump(const Duration(seconds: 11));
    expect(c.read(simpleGuiderPendingProvider), isNull, reason: 'else the shutter stays greyed on Starting…');
  });

  testWidgets("the guide camera's exposure, a tile like Preview's, and its gain slider (user, 2026-11-04)",
      (tester) async {
    final c = await _pump(tester, _wire());
    c.read(phd2SettingsProvider.notifier).setGuideExposureMs(2000);
    await tester.pump();
    final tile = find.byKey(const Key('simple-guide-exposure'));
    expect(find.descendant(of: tile, matching: find.text('2 s')), findsOneWidget);
    final slider = tester.widget<Slider>(find.byKey(const Key('simple-guider-gain')));
    expect((slider.min, slider.max), (0.0, 100.0), reason: "the Camera (Guide) window's slider, one value");
    slider.onChangeEnd!(70);
    await tester.pump();
    expect(c.read(phd2SettingsProvider).guideCameraGain, 70);
  });

  testWidgets("a guide exposure pick and a gain change reach the guider live (tuning only, so guiding goes on); "
      "not with the guider disconnected (review, #1490)", (tester) async {
    final eq = _GuiderEquipment();
    final rig = [profileApiProvider.overrideWithValue(_Profile()), guiderEquipmentApiProvider.overrideWithValue(eq)];
    await _pump(tester, _wire(), rig: rig);
    tester.widget<SimplePresetTile<int>>(find.byKey(const Key('simple-guide-exposure'))).onSelected!(2000);
    await tester.pump();
    await tester.pump();
    expect(eq.pushes, [true], reason: 'the exposure, tuning only');
    tester.widget<Slider>(find.byKey(const Key('simple-guider-gain'))).onChangeEnd!(70);
    await tester.pump();
    await tester.pump();
    expect(eq.pushes, [true, true], reason: 'the gain too');
    final off = _GuiderEquipment();
    await _pump(tester, _wire(state: 'disconnected', runtime: 'stopped'), child: const SimpleGuideGainSlider(),
        rig: [profileApiProvider.overrideWithValue(_Profile()), guiderEquipmentApiProvider.overrideWithValue(off)]);
    tester.widget<Slider>(find.byKey(const Key('simple-guider-gain'))).onChangeEnd!(50);
    await tester.pump();
    await tester.pump();
    expect(off.pushes, isEmpty, reason: 'saved to the profile only: nothing to push to');
  });

  testWidgets("a Stop the guider doesn't act on says so in a pill, not a silent flip back (ALP-5018)",
      (tester) async {
    final c = await _pump(tester, _wire(runtime: 'paused'), lag: true, child: const SimpleGuideShutter(),
        rig: [simpleModeActiveProvider.overrideWithValue(true)]);
    await tester.tap(find.byType(SimpleGuideShutter));
    await tester.pump();
    expect(_g.calls, ['stop']);
    await tester.pump(const Duration(seconds: 11)); // still paused after 10 s of reads
    expect(c.read(simpleMessagesProvider).single.title, "The guider didn't stop: it is still paused. Try Stop again.");
  });

  testWidgets("a start the guider gives up on says why in a pill, and the shutter is Start again (#1664)",
      (tester) async {
    final c = await _pump(tester, _wire(runtime: 'stopped'), lag: true, child: const SimpleGuideShutter(),
        rig: [simpleModeActiveProvider.overrideWithValue(true)]);
    _g.startError = const GuideStartFailed('Stopped capturing');
    await tester.tap(find.byType(SimpleGuideShutter));
    await tester.pump();
    expect(_g.calls, ['start']);
    expect(c.read(simpleMessagesProvider).single.title, "Guiding didn't start: Stopped capturing.");
    expect(find.bySemanticsLabel('Start guiding'), findsOneWidget);
  });

  testWidgets('no guider: the greyed shutter says why when tapped (user, 2026-11-04)', (tester) async {
    final c = await _pump(tester, _wire(state: 'disconnected', runtime: 'stopped'), child: const SimpleGuideShutter());
    await tester.tap(find.byKey(const Key('simple-shutter')));
    await tester.pump();
    expect(_g.calls, isEmpty);
    expect(c.read(simpleMessagesProvider).single.title, 'Connect the guider in Settings › Devices.');
    await tester.pump(const Duration(seconds: 11));
  });

  testWidgets('the main camera busy: Start is off and a tap says why (user, 2026-11-04: '
      'calibration turns the mount under the frame)', (tester) async {
    final c = await _pump(tester, _wire(runtime: 'stopped'), child: const SimpleGuideShutter());
    c.read(captureProgressProvider.notifier).beginExposing(const Duration(seconds: 30));
    await tester.pump();
    await tester.tap(find.byKey(const Key('simple-shutter')));
    await tester.pump();
    expect(_g.calls, isEmpty, reason: 'no start under a frame');
    expect(c.read(simpleMessagesProvider).single.title, 'The main camera is busy: wait for its frame, or stop it.');
    c.read(captureProgressProvider.notifier).reset();
    await tester.pump(const Duration(seconds: 11));
  });

  for (final (run, locked) in [
    (SequenceRunState.paused, false),
    (SequenceRunState.pausedAwaitingUser, false),
    (SequenceRunState.running, true),
    (SequenceRunState.aborting, true),
  ]) {
    testWidgets('a $run Queue ${locked ? 'locks' : 'does not lock'} Start (#1725)', (tester) async {
      final c = await _pump(tester, _wire(runtime: 'stopped'),
          child: const SimpleGuideShutter(), rig: [sequenceRunStateProvider.overrideWith(() => _Run(run))]);
      await tester.pump();
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pump();
      expect(_g.calls, locked ? isEmpty : ['start']);
      expect(c.read(simpleMessagesProvider).map((m) => m.title),
          locked ? ['The main camera is busy: wait for its frame, or stop it.'] : isEmpty);
      await tester.pump(const Duration(seconds: 11));
    });
  }

  testWidgets('a paused Queue with a frame in progress still locks Start (#1725)', (tester) async {
    final c = await _pump(tester, _wire(runtime: 'stopped'),
        child: const SimpleGuideShutter(),
        rig: [sequenceRunStateProvider.overrideWith(() => _Run(SequenceRunState.paused))]);
    c.read(captureProgressProvider.notifier).beginExposing(const Duration(seconds: 30));
    await tester.pump();
    await tester.tap(find.byKey(const Key('simple-shutter')));
    await tester.pump();
    expect(_g.calls, isEmpty);
    c.read(captureProgressProvider.notifier).reset();
    await tester.pump(const Duration(seconds: 11));
  });

  testWidgets('the main camera busy: Stop still works', (tester) async {
    final c = await _pump(tester, _wire(runtime: 'guiding'), child: const SimpleGuideShutter());
    c.read(captureProgressProvider.notifier).beginExposing(const Duration(seconds: 30));
    await tester.pump();
    await tester.tap(find.byKey(const Key('simple-shutter')));
    await tester.pump();
    expect(_g.calls, ['stop']);
    c.read(captureProgressProvider.notifier).reset();
    await tester.pump(const Duration(seconds: 11));
  });

  testWidgets('RA and Dec aggressiveness are sliders, 0-100 % in 5s, kept in the profile and sent to the guider live',
      (tester) async {
    final eq = _GuiderEquipment();
    final rig = [profileApiProvider.overrideWithValue(_Profile()), guiderEquipmentApiProvider.overrideWithValue(eq)];
    final c = await _pump(tester, _wire(), rig: rig);
    final ra = tester.widget<Slider>(find.byKey(const Key('simple-guide-aggressiveness-ra')));
    expect((ra.min, ra.max, ra.divisions), (0.0, 1.0, 20));
    final slider = find.byKey(const Key('simple-guide-aggressiveness-ra'));
    expect(tester.getCenter(find.text('RA')).dy, moreOrLessEquals(tester.getCenter(slider).dy, epsilon: 1), reason: 'one line');
    expect(tester.getCenter(find.text('70 %').first).dy, moreOrLessEquals(tester.getCenter(slider).dy, epsilon: 1));
    ra.onChangeEnd!(0.55);
    await tester.pump();
    await tester.pump();
    expect(c.read(phd2SettingsProvider).raAggressiveness, closeTo(0.55, 1e-9));
    expect(eq.pushes, [true], reason: 'tuning only, so guiding goes on');
    tester.widget<Slider>(find.byKey(const Key('simple-guide-aggressiveness-dec'))).onChangeEnd!(0.4);
    await tester.pump();
    await tester.pump();
    expect(c.read(phd2SettingsProvider).decAggressiveness, closeTo(0.4, 1e-9));
    expect(eq.pushes, [true, true]);
  });

  testWidgets('Dec guide mode is picked in the Guider panel and sent to the guider live', (tester) async {
    final eq = _GuiderEquipment();
    final rig = [profileApiProvider.overrideWithValue(_Profile()), guiderEquipmentApiProvider.overrideWithValue(eq)];
    final c = await _pump(tester, _wire(), rig: rig);
    final tile = tester.widget<SimplePresetTile<String>>(find.byKey(const Key('simple-guide-dec-mode')));
    expect(tile.options, ['auto', 'north', 'south', 'off']);
    tile.onSelected!('north');
    await tester.pump();
    await tester.pump();
    expect(c.read(phd2SettingsProvider).decGuideMode, 'north');
    expect(eq.pushes, [true]);
  });

  testWidgets('the Gain slider is named on the node VoiceOver adjusts, and an increase still saves it', (tester) async {
    final handle = tester.ensureSemantics();
    final eq = _GuiderEquipment();
    final rig = [profileApiProvider.overrideWithValue(_Profile()), guiderEquipmentApiProvider.overrideWithValue(eq)];
    final c = await _pump(tester, _wire(), rig: rig);
    await tester.pump();
    final gain = tester.getSemantics(find.byKey(const Key('simple-guider-gain')));
    expect(gain.getSemanticsData().hasAction(SemanticsAction.increase), isTrue, reason: 'the adjustable node');
    expect(gain.getSemanticsData().label, contains('Gain'));
    final before = c.read(phd2SettingsProvider).guideCameraGain;
    gain.owner!.performAction(gain.id, SemanticsAction.increase);
    await tester.pump();
    await tester.pump();
    expect(c.read(phd2SettingsProvider).guideCameraGain, greaterThan(before));
    expect(eq.pushes, [true]);
    handle.dispose();
  });

  testWidgets('RA and Dec aggressiveness are named on the nodes VoiceOver adjusts', (tester) async {
    final handle = tester.ensureSemantics();
    await _pump(tester, _wire());
    await tester.pump();
    for (final (key, name) in [('ra', 'RA aggressiveness'), ('dec', 'Dec aggressiveness')]) {
      final node = tester.getSemantics(find.byKey(Key('simple-guide-aggressiveness-$key'))).getSemanticsData();
      expect(node.hasAction(SemanticsAction.increase), isTrue, reason: '$key: the adjustable node');
      expect(node.label, contains(name));
    }
    // One stop per slider: the row's own label and value would read it twice.
    for (final label in ['RA', 'Dec']) {
      expect(find.bySemanticsLabel(RegExp('^$label\$')), findsNothing, reason: label);
    }
    handle.dispose();
  });

  testWidgets('the guiding settings sit after Gain and before Dither (user, 2026-11-09)', (tester) async {
    await _pump(tester, _wire());
    final tops = [
      for (final k in [
        'simple-guider-gain',
        'simple-guide-aggressiveness-ra',
        'simple-guide-aggressiveness-dec',
        'simple-guide-dec-mode',
        'simple-window-row-Dither',
      ])
        tester.getTopLeft(find.byKey(Key(k))).dy,
    ];
    expect(tops, orderedEquals([...tops]..sort()));
  });

  testWidgets('Dither every: frames up and down, Off below 1, kept in the profile', (tester) async {
    final c = await _pump(tester, _wire());
    c.read(phd2SettingsProvider.notifier)
      ..setDitherEnabled(true)
      ..setDitherEveryNFrames(2);
    await tester.pump();
    expect(find.text('2 frames'), findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-guider-dither-up')));
    await tester.pump();
    expect(c.read(phd2SettingsProvider).ditherEveryNFrames, 3);
    for (var i = 0; i < 3; i++) {
      await tester.tap(find.byKey(const Key('simple-guider-dither-down')));
      await tester.pump();
    }
    expect(c.read(phd2SettingsProvider).ditherEnabled, isFalse);
    expect(find.text('Off'), findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-guider-dither-up')));
    await tester.pump();
    expect((c.read(phd2SettingsProvider).ditherEnabled, c.read(phd2SettingsProvider).ditherEveryNFrames), (true, 1));
    expect(find.text('Every frame'), findsOneWidget);
  });

  testWidgets('dither distance in pixels, only while dithering', (tester) async {
    final c = await _pump(tester, _wire());
    c.read(phd2SettingsProvider.notifier)
      ..setDitherEnabled(true)
      ..setDitherPixels(5);
    await tester.pump();
    await _tab(tester, 'tuning');
    expect(find.text('5 px'), findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-guider-dither-px-up')));
    await tester.pump();
    expect(c.read(phd2SettingsProvider).ditherPixels, 6);
    c.read(phd2SettingsProvider.notifier).setDitherEnabled(false);
    await tester.pump();
    expect(find.byKey(const Key('simple-guider-dither-px-up')), findsNothing);
  });
}
