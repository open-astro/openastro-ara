import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/calibration_status.dart';
import '../../state/guider/guider_calibration_state.dart';

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
            if (busy && response == null)
              const Padding(
                padding: EdgeInsets.all(8),
                child: Center(child: CircularProgressIndicator()),
              )
            else if (async.hasError)
              // Neutral — hasError covers both "couldn't reach the guider" and a
              // failed build/toggle, so don't blame the connection specifically.
              // This replaces the last-known status (Riverpod 3's copyWithPrevious
              // is internal); the error is transient and Refresh restores status.
              Text(
                'The last guider request failed. Tap Refresh to recheck.',
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
  // Passed in rather than read here: the parent already holds the stable notifier
  // reference, so this widget needn't read any provider in build() (reading
  // provider *state* in build is the footgun; the notifier itself is stable).
  final GuiderCalibrationNotifier notifier;
  const _CalibrationBody({required this.status, required this.locked, required this.notifier});

  @override
  Widget build(BuildContext context) {
    // onBuild/onToggle fire-and-forget via unawaited(). This is safe because
    // GuiderCalibrationNotifier._run wraps every action in try/catch and routes
    // failures to `state = AsyncValue.error(...)` — the returned future never
    // throws past the notifier, and the error renders in this dialog's hasError
    // branch. Keep that contract if _run is ever refactored.
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _Artifact(
          label: 'Dark library',
          exists: status.darkLibraryExists,
          loaded: status.darkLibraryLoaded,
          detail: _darkDetail(status),
          // The switch is the auto-load/enable flag, NOT the transient
          // "loaded in memory right now" state (which can stay true after
          // disabling, bouncing the switch back). Gate on exists so a server
          // reporting auto-load=on for a not-built artifact doesn't show an
          // interactive-looking-but-frozen ON switch.
          switchValue: status.darkLibraryExists && status.autoLoadDarks,
          onBuild: locked ? null : () => unawaited(notifier.buildDarkLibrary()),
          onToggle: (locked || !status.darkLibraryExists)
              ? null
              : (v) => unawaited(notifier.setDarkLibraryEnabled(v)),
        ),
        const SizedBox(height: 12),
        _Artifact(
          label: 'Defect map',
          exists: status.defectMapExists,
          loaded: status.defectMapLoaded,
          detail: null,
          switchValue: status.defectMapExists && status.autoLoadDefectMap,
          onBuild: locked ? null : () => unawaited(notifier.buildDefectMap()),
          onToggle: (locked || !status.defectMapExists)
              ? null
              : (v) => unawaited(notifier.setDefectMapEnabled(v)),
        ),
      ],
    );
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

class _Artifact extends StatelessWidget {
  final String label;
  final bool exists;
  final bool loaded;
  final String? detail;
  // The on/off value shown by the auto-load Switch — NOT whether the Switch is
  // interactive (that's governed by onToggle being null).
  final bool switchValue;
  final VoidCallback? onBuild;
  final ValueChanged<bool>? onToggle;

  const _Artifact({
    required this.label,
    required this.exists,
    required this.loaded,
    required this.detail,
    required this.switchValue,
    required this.onBuild,
    required this.onToggle,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
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
        Align(
          alignment: Alignment.centerLeft,
          child: TextButton.icon(
            onPressed: onBuild,
            icon: const Icon(Icons.build, size: 16),
            label: Text(exists ? 'Rebuild' : 'Build'),
          ),
        ),
      ],
    );
  }
}
