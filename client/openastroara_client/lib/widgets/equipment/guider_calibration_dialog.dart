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
  bool _refreshing = false;

  @override
  void initState() {
    super.initState();
    // The provider isn't autoDispose, so a re-open would otherwise show cached
    // data silently — pull fresh status when the dialog mounts.
    Future.microtask(_refresh);
  }

  Future<void> _refresh() async {
    setState(() => _refreshing = true);
    try {
      await ref.read(guiderCalibrationProvider.notifier).refresh();
    } finally {
      if (mounted) setState(() => _refreshing = false);
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
    final locked = busy || _refreshing;

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
              Text(
                'Could not reach the guider. Check the connection and try again.',
                style: TextStyle(color: Theme.of(context).colorScheme.error),
              )
            else if (response == null || !response.connected || status == null)
              const Text('Connect the guider to manage calibration.')
            else
              _CalibrationBody(status: status, locked: locked),
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: locked ? null : _refresh,
          child: _refreshing
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

class _CalibrationBody extends ConsumerWidget {
  final CalibrationStatus status;
  final bool locked;
  const _CalibrationBody({required this.status, required this.locked});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final notifier = ref.read(guiderCalibrationProvider.notifier);
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
          // disabling, bouncing the switch back).
          enabled: status.autoLoadDarks,
          onBuild: locked ? null : () => notifier.buildDarkLibrary(),
          onToggle: (locked || !status.darkLibraryExists)
              ? null
              : (v) => notifier.setDarkLibraryEnabled(v),
        ),
        const SizedBox(height: 12),
        _Artifact(
          label: 'Defect map',
          exists: status.defectMapExists,
          loaded: status.defectMapLoaded,
          detail: null,
          enabled: status.autoLoadDefectMap,
          onBuild: locked ? null : () => notifier.buildDefectMap(),
          onToggle: (locked || !status.defectMapExists)
              ? null
              : (v) => notifier.setDefectMapEnabled(v),
        ),
      ],
    );
  }

  String? _darkDetail(CalibrationStatus s) {
    if (!s.darkLibraryLoaded || s.darkCountLoaded == null) return null;
    final n = s.darkCountLoaded;
    final lo = s.darkMinExposureSecondsLoaded;
    final hi = s.darkMaxExposureSecondsLoaded;
    if (lo != null && hi != null) {
      return '$n darks · ${lo.toStringAsFixed(1)}–${hi.toStringAsFixed(1)} s';
    }
    return '$n darks';
  }
}

class _Artifact extends StatelessWidget {
  final String label;
  final bool exists;
  final bool loaded;
  final String? detail;
  final bool enabled;
  final VoidCallback? onBuild;
  final ValueChanged<bool>? onToggle;

  const _Artifact({
    required this.label,
    required this.exists,
    required this.loaded,
    required this.detail,
    required this.enabled,
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
              child: Switch(value: enabled, onChanged: onToggle),
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
