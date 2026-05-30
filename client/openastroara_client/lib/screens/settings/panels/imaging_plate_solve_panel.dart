import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../services/profile_api.dart';
import '../../../state/saved_server_state.dart';
import '../../../state/settings/plate_solve_settings_state.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.10 Plate Solving panel — editable. Phase 12h.6i added the daemon
/// round-trip — values hydrate from the active server on mount and
/// persist back on Save.
class ImagingPlateSolvePanel extends ConsumerStatefulWidget {
  const ImagingPlateSolvePanel({super.key});

  @override
  ConsumerState<ImagingPlateSolvePanel> createState() =>
      _ImagingPlateSolvePanelState();
}

class _ImagingPlateSolvePanelState
    extends ConsumerState<ImagingPlateSolvePanel> {
  bool _saving = false;
  String? _lastError;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _hydrate());
  }

  Future<void> _hydrate() async {
    final api = _api();
    if (api == null) return;
    try {
      await ref
          .read(plateSolveSettingsProvider.notifier)
          .hydrateFromServer(api);
    } catch (e) {
      if (mounted) setState(() => _lastError = 'Could not load saved values: $e');
    }
  }

  Future<void> _save() async {
    setState(() {
      _saving = true;
      _lastError = null;
    });
    final api = _api();
    final messenger = ScaffoldMessenger.of(context);
    if (api == null) {
      setState(() {
        _saving = false;
        _lastError = 'No active server — connect to a daemon first.';
      });
      messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
      return;
    }
    try {
      await ref.read(plateSolveSettingsProvider.notifier).persistToServer(api);
      if (!mounted) return;
      messenger.showSnackBar(
        const SnackBar(content: Text('Plate Solve settings saved to daemon.')),
      );
    } catch (e) {
      if (!mounted) return;
      setState(() => _lastError = 'Save failed: $e');
      messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  ProfileApi? _api() {
    final servers = ref.read(savedServersProvider).maybeWhen(
          data: (list) => list,
          orElse: () => const [],
        );
    if (servers.isEmpty) return null;
    // Most-recently-saved server is the de-facto active one — same
    // convention as §52.2 Alpaca chooser + §54 help dialog.
    return ProfileApi(servers.last);
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(plateSolveSettingsProvider);
    final n = ref.read(plateSolveSettingsProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Solver'),
        SettingsDropdownRow<PlateSolveEngine>(
          label: 'Engine',
          helpKey: 'img.platesolve.engine',
          value: s.engine,
          items: const {
            PlateSolveEngine.astap: 'ASTAP',
            PlateSolveEngine.astrometryNet: 'astrometry.net',
            PlateSolveEngine.platesolve2: 'PlateSolve 2',
          },
          onChanged: (v) {
            if (v != null) n.setEngine(v);
          },
        ),
        EditableTextRow(
          label: 'Path / endpoint',
          currentValue: s.pathOrEndpoint,
          getCanonical: () =>
              ref.read(plateSolveSettingsProvider).pathOrEndpoint,
          parse: n.setPathOrEndpoint,
        ),
        EditableTextRow(
          label: 'Index download path',
          currentValue: s.indexDownloadPath,
          getCanonical: () =>
              ref.read(plateSolveSettingsProvider).indexDownloadPath,
          parse: n.setIndexDownloadPath,
        ),
        const SettingsSectionHeader('Solving parameters'),
        EditableNumberRow(
          label: 'Search radius (°)',
          helpKey: 'img.platesolve.search_radius_deg',
          currentValue: s.searchRadiusDeg.toString(),
          getCanonical: () =>
              ref.read(plateSolveSettingsProvider).searchRadiusDeg.toString(),
          parse: (str) {
            final v = double.tryParse(str);
            if (v != null) n.setSearchRadiusDeg(v);
          },
        ),
        EditableNumberRow(
          label: 'Downsample factor (1..8)',
          helpKey: 'img.platesolve.downsample_factor',
          currentValue: s.downsampleFactor.toString(),
          getCanonical: () => ref
              .read(plateSolveSettingsProvider)
              .downsampleFactor
              .toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setDownsampleFactor(v);
          },
        ),
        EditableNumberRow(
          label: 'Timeout (s)',
          currentValue: s.timeoutSeconds.toString(),
          getCanonical: () =>
              ref.read(plateSolveSettingsProvider).timeoutSeconds.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setTimeoutSeconds(v);
          },
        ),
        SettingsSwitchRow(
          label: 'Use blind solve as fallback',
          helpKey: 'img.platesolve.use_blind_fallback',
          value: s.useBlindFallback,
          onChanged: n.setUseBlindFallback,
          hint: 'if hint-based solve times out',
        ),
        const SettingsSectionHeader('Slew + sync'),
        SettingsSwitchRow(
          label: 'Center after slew',
          value: s.centerAfterSlew,
          onChanged: n.setCenterAfterSlew,
        ),
        SettingsSwitchRow(
          label: 'Sync to coordinates',
          value: s.syncToCoordinates,
          onChanged: n.setSyncToCoordinates,
        ),
        EditableNumberRow(
          label: 'Max iterations',
          helpKey: 'img.platesolve.max_iterations',
          currentValue: s.maxIterations.toString(),
          getCanonical: () =>
              ref.read(plateSolveSettingsProvider).maxIterations.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setMaxIterations(v);
          },
        ),
        EditableNumberRow(
          label: 'Convergence tolerance (″)',
          helpKey: 'img.platesolve.convergence_tolerance_arcsec',
          currentValue: s.convergenceToleranceArcsec.toString(),
          getCanonical: () => ref
              .read(plateSolveSettingsProvider)
              .convergenceToleranceArcsec
              .toString(),
          parse: (str) {
            final v = double.tryParse(str);
            if (v != null) n.setConvergenceToleranceArcsec(v);
          },
        ),
        const SizedBox(height: 24),
        if (_lastError != null) ...[
          Text(
            _lastError!,
            style: TextStyle(color: Theme.of(context).colorScheme.error),
          ),
          const SizedBox(height: 12),
        ],
        Row(mainAxisAlignment: MainAxisAlignment.end, children: [
          FilledButton.icon(
            onPressed: _saving ? null : _save,
            icon: _saving
                ? const SizedBox(
                    width: 14,
                    height: 14,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.save, size: 16),
            label: Text(_saving ? 'Saving…' : 'Save'),
          ),
        ]),
      ],
    );
  }
}
