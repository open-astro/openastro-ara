import 'dart:async';
import 'dart:convert';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/calibration_status.dart';
import '../../state/guider/guider_build_activity_state.dart';
import '../../state/guider/guider_calibration_state.dart';

/// Problem-detail `type` tokens the daemon puts on calibration-build 409s so
/// the two user situations read differently (mirrors EquipmentEndpoints).
const kBuildInProgressProblemType =
    'https://openastro.net/errors/calibration-build-in-progress';
const kGuiderNotConnectedProblemType =
    'https://openastro.net/errors/guider-not-connected';

/// A user-actionable message for a failed calibration action. The daemon's
/// build 409s carry a problem `type` distinguishing "another build is running"
/// (wait) from "guider not connected" (connect it); anything else stays the
/// neutral refresh hint — hasError also covers plain transport failures.
String describeCalibrationActionError(Object? error) {
  if (error is DioException) {
    Object? data = error.response?.data;
    // The daemon serves problem details as application/problem+json, which
    // Dio's default transformer does NOT auto-decode (its JSON parse is gated
    // on exactly application/json) — the body arrives as a raw string. Decode
    // it here; a Map still comes through directly if a transformer already did.
    if (data is String && data.isNotEmpty) {
      try {
        data = jsonDecode(data);
      } on FormatException {
        // Not JSON — fall through to the neutral message.
      }
    }
    final type = data is Map ? data['type'] : null;
    if (type == kBuildInProgressProblemType) {
      return 'Another calibration build is already running — wait for it to finish.';
    }
    if (type == kGuiderNotConnectedProblemType) {
      return 'The guider is not connected — connect it and try again.';
    }
    // §63.17: a 422 is the daemon itself refusing the request (e.g. a capture
    // is active during a delete). Its problem `detail` is written for humans —
    // surface it; otherwise a still-actionable generic.
    if (error.response?.statusCode == 422) {
      final detail = data is Map ? data['detail'] : null;
      if (detail is String && detail.isNotEmpty) return detail;
      return 'The guider daemon rejected the request — stop any active capture and try again.';
    }
  }
  return 'The last guider request failed. Tap Refresh to recheck.';
}

/// Opens the §63.6 guider calibration dialog (dark-library / defect-map build +
/// enable controls). Requires a connected guider; the dialog itself shows a
/// "not connected" message when the daemon reports no guider.
Future<void> showGuiderCalibrationDialog(BuildContext context) {
  return showDialog<void>(context: context, builder: (_) => const _CalibrationDialog());
}

class _CalibrationDialog extends ConsumerStatefulWidget {
  const _CalibrationDialog();
  @override
  ConsumerState<_CalibrationDialog> createState() => _CalibrationDialogState();
}

class _CalibrationDialogState extends ConsumerState<_CalibrationDialog> {
  // Starts locked: the dialog refreshes on mount (below), so a cached-data
  // re-open can't expose actionable buttons before the first refresh lands.
  bool _refreshingUi = true;

  @override
  void initState() {
    super.initState();
    // The provider isn't autoDispose, so a re-open would otherwise show cached
    // data silently — pull fresh status when the dialog mounts.
    Future.microtask(_refresh);
  }

  Future<void> _refresh() async {
    if (!mounted) return;
    setState(() => _refreshingUi = true);
    try {
      await ref.read(guiderCalibrationProvider.notifier).refresh();
    } finally {
      if (mounted) setState(() => _refreshingUi = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final async = ref.watch(guiderCalibrationProvider);
    final builds = ref.watch(guiderBuildActivityProvider);
    final invalidated = ref.watch(guiderDarkLibraryInvalidatedProvider);
    final busy = async.isLoading;
    final response = async.asData?.value;
    final status = response?.status;
    // Builds/toggles need a connected guider; lock them while any action or a
    // manual refresh is in flight.
    final locked = busy || _refreshingUi;

    return AlertDialog(
      title: const Text('Guider calibration'),
      content: SizedBox(
        width: 360,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            if (invalidated) ...[
              _InvalidatedBanner(
                onDismiss: () => ref
                    .read(guiderDarkLibraryInvalidatedProvider.notifier)
                    .dismiss(),
              ),
              const SizedBox(height: 12),
            ],
            if (busy && response == null)
              const Padding(
                padding: EdgeInsets.all(8),
                child: Center(child: CircularProgressIndicator()),
              )
            else if (async.hasError)
              // This replaces the last-known status (Riverpod 3's copyWithPrevious
              // is internal); the error is transient and Refresh restores status.
              // The describe helper upgrades the daemon's typed 409s (build busy /
              // not connected) to actionable text; everything else stays neutral.
              Text(
                describeCalibrationActionError(async.error),
                style: TextStyle(color: Theme.of(context).colorScheme.error),
              )
            else if (response == null || !response.connected)
              const Text('Connect the guider to manage calibration.')
            else if (status == null)
              // Connected, but the daemon returned no/malformed calibration status.
              const Text('Calibration status unavailable — tap Refresh.')
            else
              _CalibrationBody(
                status: status,
                locked: locked,
                builds: builds,
                notifier: ref.read(guiderCalibrationProvider.notifier),
              ),
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: locked ? null : _refresh,
          child: _refreshingUi
              ? const SizedBox(width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2))
              : const Text('Refresh'),
        ),
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Close'),
        ),
      ],
    );
  }
}

class _CalibrationBody extends StatelessWidget {
  final CalibrationStatus status;
  final bool locked;
  final Map<CalibrationArtifact, CalibrationBuildActivity> builds;
  // Passed in rather than read here: the parent already holds the stable notifier
  // reference, so this widget needn't read any provider in build() (reading
  // provider *state* in build is the footgun; the notifier itself is stable).
  final GuiderCalibrationNotifier notifier;
  const _CalibrationBody({
    required this.status,
    required this.locked,
    required this.builds,
    required this.notifier,
  });

  @override
  Widget build(BuildContext context) {
    // onBuild/onToggle fire-and-forget via unawaited(). This is safe because
    // GuiderCalibrationNotifier._run wraps every action in try/catch and routes
    // failures to `state = AsyncValue.error(...)` — the returned future never
    // throws past the notifier, and the error renders in this dialog's hasError
    // branch. Keep that contract if _run is ever refactored.
    final darkActivity = builds[CalibrationArtifact.darkLibrary];
    final defectActivity = builds[CalibrationArtifact.defectMap];
    // One camera, one build at a time (the daemon's shared gate): while EITHER
    // artifact is building, both Build buttons stay disabled.
    final anyBuilding = darkActivity?.phase == CalibrationBuildPhase.building ||
        defectActivity?.phase == CalibrationBuildPhase.building;
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _Artifact(
          label: 'Dark library',
          exists: status.darkLibraryExists,
          loaded: status.darkLibraryLoaded,
          detail: _darkDetail(status),
          activity: darkActivity,
          // The switch is the auto-load/enable flag, NOT the transient
          // "loaded in memory right now" state (which can stay true after
          // disabling, bouncing the switch back). Gate on exists so a server
          // reporting auto-load=on for a not-built artifact doesn't show an
          // interactive-looking-but-frozen ON switch.
          switchValue: status.darkLibraryExists && status.autoLoadDarks,
          onBuild: (locked || anyBuilding)
              ? null
              : () => unawaited(_confirmCoverThenBuild(
                  context, 'dark library', () => notifier.buildDarkLibrary())),
          onToggle: (locked || !status.darkLibraryExists)
              ? null
              : (v) => unawaited(notifier.setDarkLibraryEnabled(v)),
          // Delete needs an existing artifact and no build in flight (the
          // daemon 422s a delete under an active capture anyway — don't invite it).
          onDelete: (locked || anyBuilding || !status.darkLibraryExists)
              ? null
              : () => unawaited(_confirmThenDelete(
                  context, 'dark library',
                  () => notifier.deleteCalibrationFiles(defectmap: false))),
        ),
        const SizedBox(height: 12),
        _Artifact(
          label: 'Defect map',
          exists: status.defectMapExists,
          loaded: status.defectMapLoaded,
          detail: null,
          activity: defectActivity,
          switchValue: status.defectMapExists && status.autoLoadDefectMap,
          onBuild: (locked || anyBuilding)
              ? null
              : () => unawaited(_confirmCoverThenBuild(
                  context, 'defect map', () => notifier.buildDefectMap())),
          onToggle: (locked || !status.defectMapExists)
              ? null
              : (v) => unawaited(notifier.setDefectMapEnabled(v)),
          onDelete: (locked || anyBuilding || !status.defectMapExists)
              ? null
              : () => unawaited(_confirmThenDelete(
                  context, 'defect map',
                  () => notifier.deleteCalibrationFiles(darks: false))),
        ),
      ],
    );
  }

  /// The §63.6 cover-the-scope gate: both builds capture DARK frames, so light
  /// on the sensor silently poisons the whole library. Confirm before the 202.
  static Future<void> _confirmCoverThenBuild(
      BuildContext context, String what, Future<void> Function() build) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Cover the scope'),
        content: Text(
          'Building the $what captures dark frames — cap the guide scope '
          '(or close the flip-flat) first. Any light reaching the sensor '
          'silently corrupts every frame in the build.',
        ),
        actions: [
          TextButton(onPressed: () => Navigator.of(ctx).pop(false), child: const Text('Cancel')),
          FilledButton(
            onPressed: () => Navigator.of(ctx).pop(true),
            child: const Text("It's covered — build"),
          ),
        ],
      ),
    );
    if (confirmed == true) await build();
  }

  /// §63.17 destructive-action gate: a dark library takes minutes of covered-
  /// scope capture to rebuild, so never delete on a single tap.
  static Future<void> _confirmThenDelete(
      BuildContext context, String what, Future<void> Function() delete) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: Text('Delete the $what?'),
        content: Text(
          'This removes the stored $what from the guider profile. '
          'Rebuilding it needs a covered scope and several minutes of capture.',
        ),
        actions: [
          TextButton(onPressed: () => Navigator.of(ctx).pop(false), child: const Text('Cancel')),
          FilledButton(
            onPressed: () => Navigator.of(ctx).pop(true),
            child: const Text('Delete'),
          ),
        ],
      ),
    );
    if (confirmed == true) await delete();
  }

  String? _darkDetail(CalibrationStatus s) {
    if (!s.darkLibraryLoaded || s.darkCountLoaded == null) return null;
    final n = s.darkCountLoaded!;
    final lo = s.darkMinExposureSecondsLoaded;
    final hi = s.darkMaxExposureSecondsLoaded;
    if (lo != null && hi != null) {
      // Order defensively (a malformed min>max from the daemon shouldn't render
      // a reversed range) and collapse a single exposure to one value.
      final low = lo <= hi ? lo : hi;
      final high = lo <= hi ? hi : lo;
      final range = low == high
          ? '${low.toStringAsFixed(1)} s'
          : '${low.toStringAsFixed(1)}–${high.toStringAsFixed(1)} s';
      return '$n darks · $range';
    }
    return '$n darks';
  }
}

/// §63.17 staleness banner: the daemon saw a guide-camera change while a dark
/// library / defect map exists — the stored darks belong to the old sensor.
/// Advisory and dismissible; a completed rebuild clears it automatically.
class _InvalidatedBanner extends StatelessWidget {
  final VoidCallback onDismiss;
  const _InvalidatedBanner({required this.onDismiss});

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
      decoration: BoxDecoration(
        color: scheme.errorContainer,
        borderRadius: BorderRadius.circular(8),
      ),
      child: Row(
        children: [
          Icon(Icons.warning_amber_rounded, size: 18, color: scheme.onErrorContainer),
          const SizedBox(width: 8),
          Expanded(
            child: Text(
              'Guide camera changed — the dark library may be stale. '
              'Rebuild recommended.',
              style: Theme.of(context)
                  .textTheme
                  .bodySmall
                  ?.copyWith(color: scheme.onErrorContainer),
            ),
          ),
          IconButton(
            tooltip: 'Dismiss',
            iconSize: 16,
            visualDensity: VisualDensity.compact,
            color: scheme.onErrorContainer,
            onPressed: onDismiss,
            icon: const Icon(Icons.close),
          ),
        ],
      ),
    );
  }
}

class _Artifact extends StatelessWidget {
  final String label;
  final bool exists;
  final bool loaded;
  final String? detail;
  // Live build activity from the WS stream (null before any build this session).
  final CalibrationBuildActivity? activity;
  // The on/off value shown by the auto-load Switch — NOT whether the Switch is
  // interactive (that's governed by onToggle being null).
  final bool switchValue;
  final VoidCallback? onBuild;
  final ValueChanged<bool>? onToggle;
  // §63.17 — null disables the Delete button (not built / locked / building).
  final VoidCallback? onDelete;

  const _Artifact({
    required this.label,
    required this.exists,
    required this.loaded,
    required this.detail,
    required this.activity,
    required this.switchValue,
    required this.onBuild,
    required this.onToggle,
    required this.onDelete,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final building = activity?.phase == CalibrationBuildPhase.building;
    final state = !exists
        ? 'Not built'
        : loaded
            ? 'Loaded'
            : 'Built (not loaded)';
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Expanded(child: Text(label, style: theme.textTheme.titleSmall)),
            Semantics(
              label: '$label auto-load',
              child: Switch(value: switchValue, onChanged: onToggle),
            ),
          ],
        ),
        Text(state, style: theme.textTheme.bodySmall),
        if (detail != null) Text(detail!, style: theme.textTheme.labelSmall),
        if (building) ...[
          const SizedBox(height: 4),
          // Indeterminate on purpose: the daemon's build is one blocking RPC
          // with started/complete/failed events only — no percentage exists.
          const LinearProgressIndicator(),
          const SizedBox(height: 4),
          Text('Building — keep the scope covered.', style: theme.textTheme.bodySmall),
        ] else if (activity?.phase == CalibrationBuildPhase.failed)
          Text(
            'Build failed${activity?.error is String ? ': ${activity!.error}' : ''}',
            style: theme.textTheme.bodySmall?.copyWith(color: theme.colorScheme.error),
          ),
        // Wrap, not Row: two labeled buttons can exceed the 360-wide dialog —
        // let the Delete drop to a second line instead of overflowing.
        Wrap(
          spacing: 4,
          crossAxisAlignment: WrapCrossAlignment.center,
          children: [
            TextButton.icon(
              onPressed: onBuild,
              icon: building
                  ? const SizedBox(
                      width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2))
                  : const Icon(Icons.build, size: 16),
              label: Text(building ? 'Building…' : (exists ? 'Rebuild' : 'Build')),
            ),
            Semantics(
              label: 'Delete ${label.toLowerCase()}',
              child: TextButton.icon(
                onPressed: onDelete,
                icon: const Icon(Icons.delete_outline, size: 16),
                label: const Text('Delete'),
              ),
            ),
          ],
        ),
      ],
    );
  }
}
