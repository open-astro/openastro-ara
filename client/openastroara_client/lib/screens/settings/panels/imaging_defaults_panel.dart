import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../services/profile_api.dart';
import '../../../state/imaging/exposure_state.dart' show FrameKind;
import '../../../state/saved_server_state.dart';
import '../../../state/settings/imaging_defaults_state.dart';
import '../../../widgets/settings/editable_field.dart';

/// §37.9 Imaging Defaults panel. Phase 12h.3b registered all 8 fields in
/// `settings/registry.dart` and wired `helpKey`s on the non-obvious controls;
/// Phase 12h.6b adds the daemon round-trip — values hydrate from the active
/// server on mount and persist back on Save.
class ImagingDefaultsPanel extends ConsumerStatefulWidget {
  const ImagingDefaultsPanel({super.key});

  @override
  ConsumerState<ImagingDefaultsPanel> createState() =>
      _ImagingDefaultsPanelState();
}

class _ImagingDefaultsPanelState extends ConsumerState<ImagingDefaultsPanel> {
  bool _saving = false;
  String? _lastError;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _hydrate());
  }

  Future<void> _hydrate() async {
    final api = _api();
    if (api == null) return; // no active server — keep local defaults
    try {
      await ref.read(imagingDefaultsProvider.notifier).hydrateFromServer(api);
    } catch (e) {
      // Hydration failures are non-fatal — the user can still edit + Save,
      // and a real failure will resurface on Save with a clearer error.
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
      await ref.read(imagingDefaultsProvider.notifier).persistToServer(api);
      if (!mounted) return;
      messenger.showSnackBar(
        const SnackBar(content: Text('Imaging defaults saved to daemon.')),
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
    // convention as the §52.2 Alpaca chooser dialog + §54 help dialog.
    // A dedicated active-server provider is on the §55.1 v0.1.0 roadmap
    // when multi-server switching lands.
    return ProfileApi(servers.last);
  }

  @override
  Widget build(BuildContext context) {
    final d = ref.watch(imagingDefaultsProvider);
    final n = ref.read(imagingDefaultsProvider.notifier);
    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        EditableNumberRow(
          label: 'Default exposure (s)',
          currentValue: d.defaultExposure.inSeconds.toString(),
          getCanonical: () =>
              ref.read(imagingDefaultsProvider).defaultExposure.inSeconds.toString(),
          parse: (s) {
            final i = int.tryParse(s);
            if (i == null) return;
            n.setExposure(Duration(seconds: i));
          },
        ),
        EditableNumberRow(
          label: 'Default gain',
          helpKey: 'imaging.defaults.gain',
          currentValue: d.defaultGain.toString(),
          getCanonical: () =>
              ref.read(imagingDefaultsProvider).defaultGain.toString(),
          parse: (s) {
            final i = int.tryParse(s);
            if (i != null) n.setGain(i);
          },
        ),
        EditableNumberRow(
          label: 'Default offset',
          helpKey: 'imaging.defaults.offset',
          currentValue: d.defaultOffset.toString(),
          getCanonical: () =>
              ref.read(imagingDefaultsProvider).defaultOffset.toString(),
          parse: (s) {
            final i = int.tryParse(s);
            if (i != null) n.setOffset(i);
          },
        ),
        EditableNumberRow(
          label: 'Default bin',
          helpKey: 'imaging.defaults.bin',
          currentValue: d.defaultBin.toString(),
          getCanonical: () =>
              ref.read(imagingDefaultsProvider).defaultBin.toString(),
          parse: (s) {
            final i = int.tryParse(s);
            if (i != null) n.setBin(i);
          },
        ),
        SettingsDropdownRow<FrameKind>(
          label: 'Default frame type',
          value: d.defaultFrameKind,
          items: const {
            FrameKind.light: 'Light',
            FrameKind.dark: 'Dark',
            FrameKind.bias: 'Bias',
            FrameKind.flat: 'Flat',
          },
          onChanged: (v) {
            if (v != null) n.setFrameKind(v);
          },
        ),
        EditableNumberRow(
          label: 'Cooling target temperature (°C)',
          currentValue: d.coolerTargetC.toString(),
          getCanonical: () =>
              ref.read(imagingDefaultsProvider).coolerTargetC.toString(),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) n.setCoolerTargetC(v);
          },
        ),
        EditableNumberRow(
          label: 'Cooler ramp rate (°C/min)',
          helpKey: 'imaging.defaults.cooler_ramp_c_per_min',
          currentValue: d.coolerRampRatePerMin.toString(),
          getCanonical: () =>
              ref.read(imagingDefaultsProvider).coolerRampRatePerMin.toString(),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) n.setCoolerRampRate(v);
          },
        ),
        SettingsSwitchRow(
          label: 'Warm-up cooler at session end',
          helpKey: 'imaging.defaults.warmup_at_session_end',
          value: d.warmupAtSessionEnd,
          onChanged: n.setWarmupAtSessionEnd,
        ),
        const SizedBox(height: 24),
        if (_lastError != null) ...[
          Text(
            _lastError!,
            style: TextStyle(color: Theme.of(context).colorScheme.error),
          ),
          const SizedBox(height: 12),
        ],
        Row(
          mainAxisAlignment: MainAxisAlignment.end,
          children: [
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
          ],
        ),
      ],
    );
  }
}
