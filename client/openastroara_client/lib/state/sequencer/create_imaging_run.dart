import 'dart:math' as math;

import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/imaging_run_body.dart';
import '../../models/sequence/sequence_summary.dart';
import '../../services/profile_api.dart';
import '../../services/sequence_api.dart';
import '../app_shell_state.dart';
import '../saved_server_state.dart';
import '../settings/autofocus_settings_state.dart';
import '../settings/imaging_defaults_state.dart';
import 'sequence_editor_state.dart';
import 'sequence_list_state.dart';

/// The Run tab's index in the left rail (Planning / Run / Live / Options).
const int kRunTabIndex = 1;

/// What [createImagingRun] did with the target: [appended] tells the caller's
/// SnackBar whether the target joined the already-open sequence or got a
/// brand-new run of its own.
class ImagingRunResult {
  final String sequenceId;
  final bool appended;
  const ImagingRunResult(this.sequenceId, {required this.appended});
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
}) async {
  final api = ref.read(sequenceApiProvider);
  if (api == null) return null;
  final container = ProviderScope.containerOf(ref.context, listen: false);

  // The run is "built from the user's configured Imaging Defaults" — which
  // live on the daemon. The settings notifiers hydrate lazily (when their
  // Settings panels mount), so a fresh session that goes straight to Planning
  // would otherwise bake the CLIENT'S constructor defaults into the run. Pull
  // them fresh here; a failure keeps whatever the notifiers already hold (the
  // same best-effort contract the panels use).
  await _hydratePlanningSettings(container);

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
  if (selectedId != null) {
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
      container.read(selectedTabIndexProvider.notifier).select(kRunTabIndex);
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
  );

  final id = await api.create(targetName, body);

  // Same post-create choreography as the NINA import flow: refresh the list,
  // select the new sequence (the Run tab listens and loads it), then bring the
  // Run tab forward so "Create Run" lands the user ON the run it created.
  container.invalidate(sequenceListProvider);
  container.read(selectedSequenceIdProvider.notifier).select(id);
  container.read(selectedTabIndexProvider.notifier).select(kRunTabIndex);
  return ImagingRunResult(id, appended: false);
}

/// What [removeTargetFromSequence] found — drives the caller's SnackBar.
enum RemoveTargetOutcome { removed, notFound, runningBlocked, noServer }

/// The undo of "Add to Sequence" for one target: remove [targetName]'s block
/// from the sequence open in the Run tab and persist. Blocked while a run is
/// active (the executor works from its own loaded copy, so the edit would
/// silently not apply to it). Like the append path, the base body is the
/// editor's working copy when it holds this sequence — the removal targets
/// what the user SEES, and any unsaved edits are persisted along with it.
/// Throws on transport failure — callers own their error surface.
Future<RemoveTargetOutcome> removeTargetFromSequence(
  WidgetRef ref, {
  required String targetName,
}) async {
  final api = ref.read(sequenceApiProvider);
  if (api == null) return RemoveTargetOutcome.noServer;
  // Container, not WidgetRef, past the awaits — see createImagingRun.
  final container = ProviderScope.containerOf(ref.context, listen: false);
  final id = container.read(selectedSequenceIdProvider);
  if (id == null) return RemoveTargetOutcome.notFound;

  final runState = await api.getRunState(id);
  if (runState?.state?.isActive ?? false) {
    return RemoveTargetOutcome.runningBlocked;
  }

  final baseBody = await _openSequenceBaseBody(container, api, id);
  final newBody = removeTargetFromRunBody(baseBody, targetName);
  if (newBody == null) return RemoveTargetOutcome.notFound;

  final detail = await api.updateSequence(id, body: newBody);
  _syncAfterBodyChange(container, id, detail);
  return RemoveTargetOutcome.removed;
}

/// Best-effort hydration of the planning-relevant settings sections from the
/// active server's profile (imaging defaults + autofocus). No server → no-op;
/// a transport failure keeps the notifiers' current state.
Future<void> _hydratePlanningSettings(ProviderContainer container) async {
  final servers = container.read(savedServersProvider).maybeWhen(
        data: (list) => list,
        orElse: () => const [],
      );
  if (servers.isEmpty) return;
  // Most-recently-saved server is the de-facto active one — same convention
  // as the settings panels' own hydration.
  final api = ProfileApi(servers.last);
  try {
    await Future.wait([
      container.read(imagingDefaultsProvider.notifier).hydrateFromServer(api),
      container.read(autofocusSettingsProvider.notifier).hydrateFromServer(api),
    ]);
  } catch (e) {
    debugPrint('[planning] settings hydration failed (using local values): $e');
  }
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
