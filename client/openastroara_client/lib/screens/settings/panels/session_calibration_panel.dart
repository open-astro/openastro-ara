import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../services/profile_api.dart';
import '../../../state/saved_server_state.dart';
import '../../../state/settings/safety_policies_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §48 Session → Calibration panel — the sequence-start "capture calibration
/// tonight?" default. The value rides the profile's safety-policies document
/// (same daemon round-trip as Safety → Policies): hydrate on mount, persist
/// on Save.
class SessionCalibrationPanel extends ConsumerStatefulWidget {
  const SessionCalibrationPanel({super.key});

  @override
  ConsumerState<SessionCalibrationPanel> createState() =>
      _SessionCalibrationPanelState();
}

class _SessionCalibrationPanelState
    extends ConsumerState<SessionCalibrationPanel> {
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
      await ref.read(safetyPoliciesProvider.notifier).hydrateFromServer(api);
    } catch (e) {
      if (mounted) {
        setState(() => _lastError = 'Could not load saved values: $e');
      }
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
      await ref.read(safetyPoliciesProvider.notifier).persistToServer(api);
      if (!mounted) return;
      messenger.showSnackBar(
        const SnackBar(content: Text('Calibration preference saved to daemon.')),
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
    final s = ref.watch(safetyPoliciesProvider);
    final n = ref.read(safetyPoliciesProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('End-of-night calibration (§48)'),
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Text(
            'When a sequence completes, Ara can generate matching flats from '
            'that night\'s own session — every filter replaying the night\'s '
            'focus, gain, and offset. "Ask" prompts once at each sequence '
            'start; panel flats start automatically when the run ends (light '
            'your panel when notified); sky flats are generated ready to run '
            'at twilight.',
            style: Theme.of(context)
                .textTheme
                .bodyMedium
                ?.copyWith(color: AraColors.textSecondary),
          ),
        ),
        SettingsDropdownRow<CalibrationCaptureDefault>(
          label: 'Capture calibration after a sequence',
          helpKey: 'session.calibration.capture_default',
          value: s.calibrationCaptureDefault,
          items: const {
            CalibrationCaptureDefault.ask: 'Ask at each sequence start',
            CalibrationCaptureDefault.panelAtEnd: 'Panel flats at end',
            CalibrationCaptureDefault.skyAtTwilight: 'Sky flats at twilight',
            CalibrationCaptureDefault.never: 'Never',
          },
          onChanged: (v) {
            if (v != null) n.setCalibrationCaptureDefault(v);
          },
        ),
        if (_lastError != null)
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 6),
            child: Text(
              _lastError!,
              style: TextStyle(
                color: Theme.of(context).colorScheme.error,
                fontSize: 12,
              ),
            ),
          ),
        const SizedBox(height: 16),
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
