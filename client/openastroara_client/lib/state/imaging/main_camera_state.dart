import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../focus/autofocus_live_state.dart';
import '../focus/bahtinov_focus_state.dart';
import '../polar_align/polar_align_state.dart';
import '../sequencer/sequence_list_state.dart';
import 'capture_progress_state.dart';
import 'live_view_state.dart';

/// The main camera is taking frames: a shot, Live View or a running plan. A
/// paused Queue is not (the server enters those states only once the run has
/// suspended), so Guiding can start to recover it (#1725).
final mainCameraTakingFramesProvider = Provider.autoDispose<bool>(
  (ref) =>
      ref.watch(captureProgressProvider.select((p) => p.isCapturing)) ||
      ref.watch(liveViewControllerProvider) ||
      ref.watch(
        sequenceRunStateProvider.select((s) {
          final state = s.value?.state;
          return (state?.isActive ?? false) && !state!.isAnyPaused;
        }),
      ),
);

/// The main camera is busy: taking frames, or held by a paused plan that will
/// take it back. Together that is any active run (both paused states count as
/// active), plus a shot or Live View. Routines that need it, or that move the mount under it
/// (autofocus, the Bahtinov readout, polar alignment), wait (user, 2026-11-04).
final mainCameraBusyProvider = Provider.autoDispose<bool>(
  (ref) =>
      ref.watch(mainCameraTakingFramesProvider) ||
      ref.watch(sequenceRunStateProvider.select((s) => s.value?.state?.isAnyPaused ?? false)),
);

/// Something this app shows explains the main camera's exposure, so it needs
/// no camera pill of its own (#1755): its own shot, Live View, a plan taking
/// frames, autofocus, the Bahtinov readout or the Polar Align Routine. The
/// guide camera's loop is another camera. A routine with its own pill joins
/// here.
final mainCameraExplainedProvider = Provider.autoDispose<bool>(
  (ref) =>
      ref.watch(captureProgressProvider.select((p) => p.isActive)) ||
      ref.watch(mainCameraTakingFramesProvider) ||
      ref.watch(autofocusLiveProvider.select((a) => a.run.isRunning)) ||
      ref.watch(bahtinovFocusProvider.select((b) => b.status.active)) ||
      ref.watch(polarAlignLiveProvider.select((p) => p.isActive)),
);
