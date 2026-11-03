import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../services/camera_exposure_api.dart';
import '../services/frames_api.dart';
import '../state/imaging/capture_progress_state.dart';
import '../state/imaging/exposure_activity_state.dart';
import '../state/imaging/exposure_state.dart';
import '../state/imaging/last_frame_state.dart';
import '../state/imaging/live_view_frame_state.dart';
import '../state/imaging/live_view_state.dart';
import '../state/imaging/solve_state.dart';
import '../state/saved_server_state.dart';
import '../util/friendly_error.dart';
import '../l10n/l10n.dart';
import '../state/simple_messages_state.dart';

// Capture actions shared by the Live tab and Simple Mode's Preview, moved out
// of ImagingTab unchanged so both shells drive the daemon the same way.

/// §64 Live View toggle — drives the daemon's short-exposure loop. The
/// boolean controller keeps the UI intent (toggle + any cross-component
/// mirror); the frame notifier owns the start/poll/stop against the daemon,
/// seeded with the current Imaging-tab exposure/gain/binning.
Future<void> toggleLiveView(
  BuildContext context,
  WidgetRef ref,
  bool on,
) async {
  final notify = userNotifier(context);
  final controller = ref.read(liveViewControllerProvider.notifier);
  controller.set(on); // optimistic — reflect the tap immediately
  final lv = ref.read(liveViewFrameProvider.notifier);
  if (on) {
    final p = ref.read(exposureControllerProvider);
    // inMicroseconds / 1e6 is lossless for sub-millisecond live exposures.
    await lv.start(
      exposureSec: p.exposure.inMicroseconds / 1e6,
      gain: p.gain,
      binX: p.bin,
      binY: p.bin,
    );
    // A start() failure flips active back to false — keep the toggle honest
    // (it can't sit stuck "on") AND surface why, since FrameViewer only shows
    // the live error while active.
    if (!context.mounted) return;
    final lvState = ref.read(liveViewFrameProvider);
    // Only an actual error means start failed — a null-error inactive state
    // here means the user toggled OFF while start() was in flight (stop() set
    // idle), which must not raise a spurious "couldn't start" snackbar.
    if (!lvState.active && lvState.error != null) {
      controller.set(false);
      notify(
        "Couldn't start Live View",
        detail: '${lvState.error}',
        error: true,
        snackBarRed: false,
      );
    }
  } else {
    await lv.stop();
  }
}

/// §14e Take One — fire a single exposure on the connected camera. The
/// daemon runs the capture in the background and registers the frame; we
/// just surface accepted/failed to the user.
Future<void> takeOne(BuildContext context, WidgetRef ref) async {
  final notify = userNotifier(context);
  final l10n = context.l10n;
  // Commit any half-typed control edit (the fields commit on focus loss)
  // before reading the exposure params this shot will use. The zero delay
  // lets the focus system deliver the change listeners.
  FocusManager.instance.primaryFocus?.unfocus();
  await Future<void>.delayed(Duration.zero);
  final server = ref.read(activeServerProvider);
  if (server == null) {
    notify('Not connected to a server.', error: true, snackBarRed: false);
    return;
  }
  final params = ref.read(exposureControllerProvider);
  // Notifier handles, captured before any await so the finally-reset and the
  // result update don't go through WidgetRef after a possible unmount.
  final progress = ref.read(captureProgressProvider.notifier);
  final lastFrame = ref.read(lastCapturedFrameIdProvider.notifier);
  final solve = ref.read(solveResultProvider.notifier);
  progress.beginExposing(params.exposure);
  // Identity of this capture cycle — a Cancel (reset) or a newer Take One
  // bumps the generation, so this loop's late complete()/fail() no-op.
  final generation = progress.currentGeneration;
  // Phase 1 — the exposure POST. A failure here means the shot never
  // started; the user should re-shoot.
  final String frameId;
  try {
    frameId = await CameraExposureApi(server).takeOne(params);
  } catch (e) {
    progress.fail(
      friendlyError(l10n, e, action: 'take that exposure'),
      generation: generation,
    );
    return;
  }
  // Phase 2 — the POST returned 202; the capture (expose → download → FITS)
  // runs in the background. Poll the catalog until the frame is registered,
  // or until the daemon's camera.exposure_failed names this frame (#1450 —
  // the storage disk went away before the shutter opened, a device fault):
  // its reason is the user's answer, not a timeout 20 s later. A failure of
  // the poll itself means the exposure was accepted but we couldn't confirm
  // it landed — distinct remedy (retry the preview, don't re-shoot).
  final api = FramesApi(server);
  final deadline = DateTime.now().add(
    params.exposure + const Duration(seconds: 20),
  );
  var landed = false;
  try {
    while (DateTime.now().isBefore(deadline)) {
      // Keep polling even if the user navigated away — the capture runs on
      // the daemon regardless, and the notifier is root-scoped, so it must
      // still reach a terminal state (done/failed) to schedule its own
      // auto-clear. Only the UI side-effects below need the mounted guard.
      // A stale generation, though, means this cycle was cancelled or
      // superseded — its complete()/fail() would no-op anyway, so stop
      // polling instead of hitting the catalog until the full deadline
      // (rapid Cancel → Retry would otherwise stack several dead loops).
      if (!progress.isCurrent(generation)) return;
      if (progress.failureFor(frameId) case final reason?) {
        progress.fail(reason, generation: generation);
        return;
      }
      if (await api.isRegistered(frameId)) {
        landed = true;
        break;
      }
      await Future<void>.delayed(const Duration(milliseconds: 500));
    }
  } catch (e) {
    progress.fail(
      friendlyError(l10n, e, action: 'confirm the frame arrived'),
      generation: generation,
    );
    return;
  }
  if (landed) {
    progress.complete(frameId, generation: generation);
    // A stale cycle (cancelled or superseded while this loop polled) must
    // not repoint the viewer at its frame or clear the new cycle's solve.
    if (!context.mounted || !progress.isCurrent(generation)) return;
    lastFrame.set(frameId);
    // A new frame invalidates any previous solve result shown in the panel.
    solve.clear();
    // Force a re-fetch in case the same id was shown before.
    ref.invalidate(framePreviewProvider(frameId));
  } else {
    progress.fail('Capture timed out.', generation: generation);
  }
  // The notifier owns the terminal-state auto-clear (done ~1.8s, failed
  // ~6s) — nothing to do here.
}

/// Cancel the in-flight capture — abort the exposure on the daemon, then
/// drop the capture state back to idle.
Future<void> cancelCapture(BuildContext context, WidgetRef ref) async {
  final notify = userNotifier(context);
  final progress = ref.read(captureProgressProvider.notifier);
  final server = ref.read(activeServerProvider);
  if (server == null) {
    progress.reset();
    return;
  }
  // Captured before the await, like the other notifier handles.
  final activity = ref.read(exposureActivityProvider.notifier);
  try {
    await CameraExposureApi(server).abort();
    progress.reset();
    // The rail banner would otherwise take over from the card and keep
    // counting the sub we just cancelled until the daemon's failed event.
    activity.endLocally();
  } catch (e) {
    // The abort POST failed. A lost response is NOT a successful abort —
    // the exposure may still be running and its frame will land, so do NOT
    // fail the card or bump the generation here: that would orphan the
    // frame (the poll loop's complete() would no-op and lastFrame.set would
    // be skipped). Keep tracking the capture and tell the user the cancel
    // didn't go through; the card resolves with the truth (done or timeout).
    if (!progress.isCapturing) return; // already reset or resolved (a
    // double-tap race where the first abort won, or the capture finished
    // while the abort was in flight) — nothing to warn about.
    if (!context.mounted) return;
    notify(
      friendlyError(context.l10n, e, action: 'abort the capture'),
      error: true,
    );
  }
}
