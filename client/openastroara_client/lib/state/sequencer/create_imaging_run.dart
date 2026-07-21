import 'dart:math' as math;

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/draft_sequence.dart';
import '../../models/sequence/imaging_run_body.dart';
import '../../models/sequence/sequence_summary.dart';
import '../../services/profile_api.dart';
import '../../services/sequence_api.dart';
import '../../theme/ara_colors.dart';
import '../../widgets/sequencer/target_plan_dialog.dart';
import '../app_shell_state.dart';
import '../saved_server_state.dart';
import '../settings/autofocus_settings_state.dart';
import '../settings/filter_set_state.dart';
import '../settings/imaging_defaults_state.dart';
import '../settings/phd2_settings_state.dart';
import '../settings/settings_nav.dart';
import 'draft_sequences_state.dart';
import 'sequence_editor_state.dart';
import 'sequence_list_state.dart';

/// What [createImagingRun] did with the target: [appended] tells the caller's
/// SnackBar whether the target joined the already-open sequence or got a
/// brand-new run of its own.
class ImagingRunResult {
  final String sequenceId;
  final bool appended;

  /// True when no server was connected and the run was saved as a LOCAL draft
  /// (§2 offline planning) instead of being created on the daemon.
  final bool draft;

  /// True when the user cancelled out of the plan chooser — nothing was
  /// created and no feedback should be shown.
  final bool cancelled;
  const ImagingRunResult(this.sequenceId,
      {required this.appended, this.draft = false, this.cancelled = false});

  static const ImagingRunResult userCancelled =
      ImagingRunResult('', appended: false, cancelled: true);
}

/// §36 — the ONE feedback surface for [createImagingRun]'s outcome. The three
/// planning call sites (target action bar, Tonight's Sky row, framing overlay)
/// each used to rebuild these SnackBars and had already drifted: only the
/// framing overlay said anything on the no-server case, and only some styled
/// errors. Callers gate on their own `mounted` before invoking.
void showImagingRunFeedback(
  ScaffoldMessengerState messenger, {
  required String targetName,
  ImagingRunResult? result,
  bool failed = false,
}) {
  if (result?.cancelled ?? false) return;
  if (failed) {
    messenger.showSnackBar(
      const SnackBar(
        content: Text(
          "Couldn't create the run. Check the connection and try again.",
        ),
        backgroundColor: AraColors.accentError,
      ),
    );
  } else if (result == null) {
    messenger.showSnackBar(
      const SnackBar(
        content: Text('Connect to a server before creating a run.'),
        backgroundColor: AraColors.accentError,
      ),
    );
  } else if (result.draft) {
    messenger.showSnackBar(
      SnackBar(
        content: Text('Saved "$targetName" as an offline draft — push it to '
            'the server from Load sequence once connected.'),
      ),
    );
  } else {
    messenger.showSnackBar(
      SnackBar(
        content: Text(
          result.appended
              ? 'Added "$targetName" to the open sequence.'
              : 'Created an imaging run for "$targetName".',
        ),
      ),
    );
  }
}

/// §36 Planning → one-tap "set up a real run": put the target into a complete
/// imaging sequence built from the user's configured Imaging Defaults +
/// Autofocus settings, then select it and jump to the Run tab.
///
/// With a sequence already selected (and not actively running), the target is
/// APPENDED to it as another target block ([buildTargetBlock] /
/// [appendTargetToRunBody]) — adding M 31 then M 42 builds ONE multi-target
/// night plan instead of a second run that shoves the first off screen. Only
/// when nothing is open (or the open one can't take an append — running,
/// vanished, non-container root) does it create a fresh sequence
/// ([buildImagingRunBody]) on the daemon.
///
/// Returns the result, or null when no server is connected. Throws on
/// create/append failure — callers own their error surface (SnackBar).
///
/// All post-await provider work goes through the [ProviderContainer], not the
/// caller's [WidgetRef]: the calling widget (a Tonight's Sky row, the action
/// bar) can be disposed while a round-trip is in flight, and a disposed
/// WidgetRef throws — which would turn a create/append that SUCCEEDED on the
/// daemon into a reported failure (and, on the append path, into a duplicate
/// fresh run via the fallback).
Future<ImagingRunResult?> createImagingRun(
  WidgetRef ref, {
  required double raDeg,
  required double decDeg,
  required String targetName,
  double? remainingDarkHours,
  // §36/§38 — a framing position angle to carry into the run: the target's
  // slew becomes a Center and Rotate at this angle. Null = plain slew.
  double? positionAngleDeg,
  // Planning-redesign S8: false = stay on the CALLING tab after a create
  // (the Tonight's Sky panel shows its own confirmation card with a
  // "View in Run" action instead of yanking the user away mid-planning).
  bool jumpToRun = true,
}) async {
  final api = ref.read(sequenceApiProvider);
  final container = ProviderScope.containerOf(ref.context, listen: false);

  // §2 offline planning — no server: build the same run body from whatever the
  // settings notifiers currently hold (no daemon to hydrate from) and save it
  // as a LOCAL draft; it pushes to the daemon from the Load dialog later.
  // Append-to-open-draft is a tracked follow-up — offline, each target gets
  // its own draft.
  if (api == null) {
    final choice = await _choosePlan(
      ref,
      container,
      targetName: targetName,
      raDeg: raDeg,
      decDeg: decDeg,
      remainingDarkHours: remainingDarkHours,
    );
    if (choice == null) return ImagingRunResult.userCancelled;
    final defaults = container.read(imagingDefaultsProvider);
    final af = container.read(autofocusSettingsProvider);
    final exposureSeconds = defaults.defaultExposure.inMilliseconds /
        Duration.millisecondsPerSecond;
    final afEvery = af.everyNHours > 0 && exposureSeconds > 0
        ? math.max(1, ((af.everyNHours * 3600.0) / exposureSeconds).ceil())
        : null;
    final body = buildImagingRunBody(
      raDeg: raDeg,
      decDeg: decDeg,
      targetName: targetName,
      exposureSeconds: exposureSeconds,
      gain: defaults.defaultGain,
      offset: defaults.defaultOffset,
      binning: defaults.defaultBin,
      frameCount: defaultFrameCount(exposureSeconds,
          remainingDarkHours: remainingDarkHours),
      coolToC: defaults.coolerTargetC,
      warmAtEnd: defaults.warmupAtSessionEnd,
      autofocusEveryNExposures: afEvery,
      positionAngleDeg: positionAngleDeg,
      filterPlan: choice.filterPlan,
      startGuiding: choice.guide,
      ditherEveryNExposures: _ditherCadence(container, choice),
      manualFilterSwap: choice.manualFilterSwap,
    );
    final draftId = await container
        .read(draftSequencesProvider.notifier)
        .create(targetName, body);
    container.read(selectedSequenceIdProvider.notifier).select(draftId);
    if (jumpToRun) {
      container.read(selectedTabIndexProvider.notifier).select(kRunTabIndex);
    }
    return ImagingRunResult(draftId, appended: false, draft: true);
  }

  // The run is "built from the user's configured Imaging Defaults" — which
  // live on the daemon. The settings notifiers hydrate lazily (when their
  // Settings panels mount), so a fresh session that goes straight to Planning
  // would otherwise bake the CLIENT'S constructor defaults into the run. Pull
  // them fresh here; a failure keeps whatever the notifiers already hold (the
  // same best-effort contract the panels use).
  await _hydratePlanningSettings(container);

  // §Run smart targets — offer the SHO / LRGB / single-filter plan chooser
  // when the filter set gives a real choice. Null = the user cancelled.
  final choice = await _choosePlan(
    ref,
    container,
    targetName: targetName,
    raDeg: raDeg,
    decDeg: decDeg,
    remainingDarkHours: remainingDarkHours,
  );
  if (choice == null) return ImagingRunResult.userCancelled;
  final ditherEvery = _ditherCadence(container, choice);

  final defaults = container.read(imagingDefaultsProvider);
  final af = container.read(autofocusSettingsProvider);
  final exposureSeconds =
      defaults.defaultExposure.inMilliseconds / Duration.millisecondsPerSecond;
  final frameCount = defaultFrameCount(
    exposureSeconds,
    remainingDarkHours: remainingDarkHours,
  );
  // The AF settings express cadence in hours; the daemon's trigger counts
  // exposures. Convert at the planned sub length (≥ 1 so a long sub can't
  // produce a zero cadence, which the daemon would reject).
  final afEveryExposures = af.everyNHours > 0 && exposureSeconds > 0
      ? math.max(1, ((af.everyNHours * 3600.0) / exposureSeconds).ceil())
      : null;

  final selectedId = container.read(selectedSequenceIdProvider);
  // A selected local draft can't take a daemon-side append — fall through to
  // creating a fresh sequence on the daemon instead.
  if (selectedId != null && !isDraftSequenceId(selectedId)) {
    final block = buildTargetBlock(
      raDeg: raDeg,
      decDeg: decDeg,
      targetName: targetName,
      exposureSeconds: exposureSeconds,
      gain: defaults.defaultGain,
      offset: defaults.defaultOffset,
      binning: defaults.defaultBin,
      frameCount: frameCount,
      autofocusEveryNExposures: afEveryExposures,
      positionAngleDeg: positionAngleDeg,
      filterPlan: choice.filterPlan,
      startGuiding: choice.guide,
      ditherEveryNExposures: ditherEvery,
      manualFilterSwap: choice.manualFilterSwap,
    );
    // Only PRE-PATCH problems (running, vanished, non-container root,
    // transport failure while reading) fall back to creating a fresh run. A
    // failed updateSequence rethrows instead: the daemon may have applied the
    // append before the response was lost, and creating then would duplicate
    // the target across two sequences.
    final newBody = await _prepareAppend(container, api, selectedId, block);
    if (newBody != null) {
      final detail = await api.updateSequence(selectedId, body: newBody);
      _syncAfterBodyChange(container, selectedId, detail);
      if (jumpToRun) {
        container.read(selectedTabIndexProvider.notifier).select(kRunTabIndex);
      }
      return ImagingRunResult(selectedId, appended: true);
    }
  }

  final body = buildImagingRunBody(
    raDeg: raDeg,
    decDeg: decDeg,
    targetName: targetName,
    exposureSeconds: exposureSeconds,
    gain: defaults.defaultGain,
    offset: defaults.defaultOffset,
    binning: defaults.defaultBin,
    frameCount: frameCount,
    coolToC: defaults.coolerTargetC,
    warmAtEnd: defaults.warmupAtSessionEnd,
    autofocusEveryNExposures: afEveryExposures,
    positionAngleDeg: positionAngleDeg,
    filterPlan: choice.filterPlan,
    startGuiding: choice.guide,
    ditherEveryNExposures: ditherEvery,
    manualFilterSwap: choice.manualFilterSwap,
  );

  // One key per LOGICAL create: if the request dies in transit the daemon may
  // still have applied it — the degraded draft below carries this same key so
  // its eventual push dedupes on the daemon instead of duplicating the run.
  final createKey = 'run-${DateTime.now().microsecondsSinceEpoch}-'
      '${identityHashCode(body).toRadixString(16)}';
  final String id;
  try {
    id = await api.create(targetName, body, idempotencyKey: createKey);
  } on DioException catch (e) {
    // §2 offline planning — a SAVED server whose daemon is down/unreachable
    // still yields a non-null api, so "offline" surfaces here as a transport
    // failure. Degrade the create to a local draft rather than losing the
    // plan. Only transport-level failures qualify: a daemon that RESPONDED
    // with an error (4xx/5xx) rejected the body, and hiding that behind a
    // draft would just defer the same rejection to push time.
    if (e.response != null) rethrow;
    final draftId = await container
        .read(draftSequencesProvider.notifier)
        .create(targetName, body, pushKey: createKey);
    container.read(selectedSequenceIdProvider.notifier).select(draftId);
    if (jumpToRun) {
      container.read(selectedTabIndexProvider.notifier).select(kRunTabIndex);
    }
    return ImagingRunResult(draftId, appended: false, draft: true);
  }

  // Same post-create choreography as the NINA import flow: refresh the list,
  // select the new sequence (the Run tab listens and loads it), then bring the
  // Run tab forward so "Create Run" lands the user ON the run it created.
  container.invalidate(sequenceListProvider);
  container.read(selectedSequenceIdProvider.notifier).select(id);
  if (jumpToRun) {
    container.read(selectedTabIndexProvider.notifier).select(kRunTabIndex);
  }
  return ImagingRunResult(id, appended: false);
}


/// Best-effort hydration of the planning-relevant settings sections from the
/// active server's profile (imaging defaults + autofocus + the filter set and
/// PHD2 settings the plan chooser reads). No server → no-op; a transport
/// failure keeps the notifiers' current state.
Future<void> _hydratePlanningSettings(ProviderContainer container) async {
  final server = container.read(activeServerProvider);
  if (server == null) return;
  final api = ProfileApi(server);
  try {
    await Future.wait([
      container.read(imagingDefaultsProvider.notifier).hydrateFromServer(api),
      container.read(autofocusSettingsProvider.notifier).hydrateFromServer(api),
      container.read(filterSetProvider.notifier).hydrateFromServer(api),
      container.read(phd2SettingsProvider.notifier).hydrateFromServer(api),
    ]);
  } catch (e) {
    debugPrint('[planning] settings hydration failed (using local values): $e');
  }
}

/// The plan chooser, or a silent basic choice when the filter set offers no
/// real alternative (empty / OSC-only) or the calling widget's context is
/// gone. Null = the user explicitly cancelled — create nothing.
Future<TargetPlanChoice?> _choosePlan(
  WidgetRef ref,
  ProviderContainer container, {
  required String targetName,
  required double raDeg,
  required double decDeg,
  double? remainingDarkHours,
}) async {
  if (!planOptionsAvailable(container.read(filterSetProvider).filters)) {
    return const TargetPlanChoice(guide: false);
  }
  final context = ref.context;
  if (!context.mounted) return const TargetPlanChoice(guide: false);
  return showTargetPlanDialog(
    context,
    targetName: targetName,
    raDeg: raDeg,
    decDeg: decDeg,
    remainingDarkHours: remainingDarkHours,
  );
}

/// The dither cadence for the run's imaging loops: the PHD2 settings' every-N
/// frames when the user chose to guide and dithering is on; null = no dither
/// trigger (also whenever guiding was declined — dithering without a guider
/// nudges nothing).
int? _ditherCadence(ProviderContainer container, TargetPlanChoice choice) {
  if (!choice.guide) return null;
  final phd2 = container.read(phd2SettingsProvider);
  return phd2.ditherEnabled ? math.max(1, phd2.ditherEveryNFrames) : null;
}

/// The append's PRE-PATCH phase: probe the run state, resolve the base body,
/// and graft [targetBlock] onto it. Null = "fall back to creating a fresh
/// run" — the sequence is actively running (a file edit would silently not
/// join the live run), its root isn't a container, or a read failed (a create
/// is about to retry the same transport anyway). Deliberately does NOT cover
/// the PATCH itself — see the call site.
Future<Map<String, dynamic>?> _prepareAppend(
  ProviderContainer container,
  SequenceClient api,
  String id,
  Map<String, dynamic> targetBlock,
) async {
  try {
    final runState = await api.getRunState(id);
    if (runState?.state?.isActive ?? false) return null;
    final baseBody = await _openSequenceBaseBody(container, api, id);
    return appendTargetToRunBody(baseBody, targetBlock);
  } on ArgumentError {
    return null; // non-container root (e.g. an exotic import) — create fresh
  } catch (e) {
    debugPrint('[planning] append-to-sequence failed, creating a new run: $e');
    return null;
  }
}

/// The base body a mutation of sequence [id] starts from: the editor's working
/// copy when the editor holds this sequence — "Add to Sequence"/"Remove"
/// target what the user SEES, so unsaved edits are deliberately persisted
/// along with the change rather than clobbered by the daemon's older copy —
/// else the daemon's saved detail.
Future<Map<String, dynamic>> _openSequenceBaseBody(
  ProviderContainer container,
  SequenceClient api,
  String id,
) async {
  final editor = container.read(sequenceEditorProvider);
  if (editor != null && editor.id == id) return editor.body;
  return (await api.getSequenceDetail(id)).body;
}

/// After a persisted body mutation: re-sync the editor (not-dirty, at the
/// saved body) when it holds this sequence so the Run tab shows the change
/// without a re-select, and refresh the list (ModifiedUtc ordering changed).
void _syncAfterBodyChange(
  ProviderContainer container,
  String id,
  SequenceDetail detail,
) {
  if (container.read(sequenceEditorProvider)?.id == id) {
    container.read(sequenceEditorProvider.notifier).load(detail);
  }
  container.invalidate(sequenceListProvider);
}
