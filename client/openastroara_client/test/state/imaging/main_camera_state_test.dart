import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/autofocus_run.dart';
import 'package:openastroara/models/bahtinov_focus.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/state/focus/autofocus_live_state.dart';
import 'package:openastroara/state/focus/bahtinov_focus_state.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/state/imaging/capture_progress_state.dart';
import 'package:openastroara/state/imaging/live_view_state.dart';
import 'package:openastroara/state/imaging/main_camera_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';

class _Run extends SequenceRunStateNotifier {
  _Run(this.state0);
  final SequenceRunState? state0;
  @override
  Future<SequenceRunStateInfo?> build() async =>
      state0 == null ? null : SequenceRunStateInfo(runId: 'r1', state: state0!);
}

class _Autofocus extends AutofocusLiveNotifier {
  _Autofocus(this.running);
  final bool running;
  @override
  AutofocusLive build() =>
      AutofocusLive(run: running ? const AutofocusRun(state: AutofocusRunStates.running) : AutofocusRun.idle);
}

class _Bahtinov extends BahtinovFocusNotifier {
  _Bahtinov(this.active);
  final bool active;
  @override
  BahtinovFocusLive build() => BahtinovFocusLive(status: BahtinovFocusStatus(active: active));
}

class _Polar extends PolarAlignLiveNotifier {
  _Polar(this.active);
  final bool active;
  @override
  PolarAlignLive build() => PolarAlignLive(phase: active ? PolarAlignStates.adjusting : PolarAlignStates.idle);
}

/// Whether something this app shows explains the main camera's exposure.
Future<bool> _explained({
  bool capturing = false,
  bool live = false,
  SequenceRunState? run,
  bool autofocus = false,
  bool readout = false,
  bool polar = false,
}) async {
  final c = ProviderContainer(overrides: [
    sequenceRunStateProvider.overrideWith(() => _Run(run)),
    autofocusLiveProvider.overrideWith(() => _Autofocus(autofocus)),
    bahtinovFocusProvider.overrideWith(() => _Bahtinov(readout)),
    polarAlignLiveProvider.overrideWith(() => _Polar(polar)),
  ]);
  addTearDown(c.dispose);
  final sub = c.listen(sequenceRunStateProvider, (_, _) {});
  addTearDown(sub.close);
  await c.read(sequenceRunStateProvider.future);
  if (capturing) c.read(captureProgressProvider.notifier).beginExposing(const Duration(seconds: 30));
  if (live) c.read(liveViewControllerProvider.notifier).set(true);
  return c.read(mainCameraExplainedProvider);
}

/// (taking frames, busy) with a shot [capturing] or not, Live View [live] or
/// not, and the run in [run] (null: no run).
Future<(bool, bool)> _read({bool capturing = false, bool live = false, SequenceRunState? run}) async {
  final c = ProviderContainer(overrides: [sequenceRunStateProvider.overrideWith(() => _Run(run))]);
  addTearDown(c.dispose);
  final sub = c.listen(sequenceRunStateProvider, (_, _) {});
  addTearDown(sub.close);
  await c.read(sequenceRunStateProvider.future);
  if (capturing) c.read(captureProgressProvider.notifier).beginExposing(const Duration(seconds: 30));
  if (live) c.read(liveViewControllerProvider.notifier).set(true);
  return (c.read(mainCameraTakingFramesProvider), c.read(mainCameraBusyProvider));
}

void main() {
  test('nothing on: neither taking frames nor busy', () async {
    expect(await _read(), (false, false));
    expect(await _read(run: SequenceRunState.completed), (false, false), reason: 'a finished run is idle');
  });

  test('a shot, Live View or a running plan: both', () async {
    expect(await _read(capturing: true), (true, true));
    expect(await _read(live: true), (true, true));
    expect(await _read(run: SequenceRunState.running), (true, true));
    expect(await _read(run: SequenceRunState.starting), (true, true));
  });

  test('a paused Queue holds the camera but takes no frames (#1725)', () async {
    expect(await _read(run: SequenceRunState.paused), (false, true));
    expect(await _read(run: SequenceRunState.pausedAwaitingUser), (false, true));
  });

  test('a shot during a paused Queue is still taking frames', () async {
    expect(await _read(capturing: true, run: SequenceRunState.paused), (true, true));
  });

  test('who explains the main camera exposing: the truth table (#1755)', () async {
    expect(await _explained(), isFalse, reason: 'nothing');
    expect(await _explained(capturing: true), isTrue, reason: 'own shot');
    expect(await _explained(live: true), isTrue, reason: 'Live View');
    expect(await _explained(run: SequenceRunState.running), isTrue, reason: 'plan active');
    expect(await _explained(run: SequenceRunState.paused), isFalse, reason: 'a paused plan takes no frames');
    expect(await _explained(run: SequenceRunState.pausedAwaitingUser), isFalse, reason: 'a paused plan takes no frames');
    expect(await _explained(autofocus: true), isTrue, reason: 'autofocus');
    expect(await _explained(readout: true), isTrue, reason: 'Bahtinov readout');
    expect(await _explained(polar: true), isTrue, reason: 'Polar Align Routine');
  });
}
