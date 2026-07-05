import 'dart:math' as math;

import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/imaging_run_body.dart';
import '../../services/sequence_api.dart';
import '../app_shell_state.dart';
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
/// create/validation failure — callers own their error surface (SnackBar).
Future<ImagingRunResult?> createImagingRun(
  WidgetRef ref, {
  required double raDeg,
  required double decDeg,
  required String targetName,
  double? remainingDarkHours,
}) async {
  final api = ref.read(sequenceApiProvider);
  if (api == null) return null;

  final defaults = ref.read(imagingDefaultsProvider);
  final af = ref.read(autofocusSettingsProvider);
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

  final selectedId = ref.read(selectedSequenceIdProvider);
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
    if (await _tryAppendTarget(ref, api, selectedId, block)) {
      // The list's ModifiedUtc ordering changed; selection already points at
      // the sequence, so just bring the Run tab forward onto the grown plan.
      ref.invalidate(sequenceListProvider);
      ref.read(selectedTabIndexProvider.notifier).select(kRunTabIndex);
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
  ref.invalidate(sequenceListProvider);
  ref.read(selectedSequenceIdProvider.notifier).select(id);
  ref.read(selectedTabIndexProvider.notifier).select(kRunTabIndex);
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
  final id = ref.read(selectedSequenceIdProvider);
  if (id == null) return RemoveTargetOutcome.notFound;

  final runState = await api.getRunState(id);
  if (runState?.state?.isActive ?? false) {
    return RemoveTargetOutcome.runningBlocked;
  }

  final editor = ref.read(sequenceEditorProvider);
  final baseBody = (editor != null && editor.id == id)
      ? editor.body
      : (await api.getSequenceDetail(id)).body;
  final newBody = removeTargetFromRunBody(baseBody, targetName);
  if (newBody == null) return RemoveTargetOutcome.notFound;

  final detail = await api.updateSequence(id, body: newBody);
  // Re-sync the editor (not-dirty, at the saved body) when it holds this
  // sequence, so the Run tab drops the target without a re-select.
  if (ref.read(sequenceEditorProvider)?.id == id) {
    ref.read(sequenceEditorProvider.notifier).load(detail);
  }
  ref.invalidate(sequenceListProvider);
  return RemoveTargetOutcome.removed;
}

/// Append [targetBlock] to sequence [id] on the daemon. True on success; false
/// means "fall back to creating a fresh run" — the sequence is actively
/// running/pausing (the executor works from its own loaded copy, so a file
/// edit would silently not join the live run), its root isn't a container, or
/// any round-trip failed (a create is about to retry the same transport
/// anyway, and ITS failure is the one surfaced to the user).
///
/// The base body is the editor's working copy when this sequence is the one
/// loaded there — "Add to Sequence" targets what the user SEES, so any unsaved
/// edits are deliberately persisted along with the new target rather than
/// silently dropped by patching over them from the daemon's older copy.
Future<bool> _tryAppendTarget(
  WidgetRef ref,
  SequenceClient api,
  String id,
  Map<String, dynamic> targetBlock,
) async {
  try {
    final runState = await api.getRunState(id);
    if (runState?.state?.isActive ?? false) return false;

    final editor = ref.read(sequenceEditorProvider);
    final baseBody = (editor != null && editor.id == id)
        ? editor.body
        : (await api.getSequenceDetail(id)).body;
    final newBody = appendTargetToRunBody(baseBody, targetBlock);
    final detail = await api.updateSequence(id, body: newBody);
    // Re-sync the editor (not-dirty, at the saved body) when it holds this
    // sequence, so the Run tab shows the appended target without a re-select.
    if (ref.read(sequenceEditorProvider)?.id == id) {
      ref.read(sequenceEditorProvider.notifier).load(detail);
    }
    return true;
  } on ArgumentError {
    return false; // non-container root (e.g. an exotic import) — create fresh
  } catch (e) {
    debugPrint('[planning] append-to-sequence failed, creating a new run: $e');
    return false;
  }
}
