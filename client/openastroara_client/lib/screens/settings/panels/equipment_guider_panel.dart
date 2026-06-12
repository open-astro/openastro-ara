import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../services/profile_api.dart';
import '../../../state/saved_server_state.dart';
import '../../../state/settings/equipment_connection_state.dart';
import '../../../state/settings/phd2_settings_state.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §63 PHD2 / Guider panel — editable. Phase 12h.6k added the daemon
/// round-trip for the §63 PHD2 fields (host/port/profile + dithering +
/// per-session calibration). The §52.2 auto-connect-on-boot toggle uses
/// `equipmentConnectionProvider` and round-trips with the bulk
/// equipment-connection sub-PR; the §35 meridian-flip re-cal toggle is
/// surfaced read-only here as a reference — edit it in Safety → Policies.
class EquipmentGuiderPanel extends ConsumerStatefulWidget {
  const EquipmentGuiderPanel({super.key});

  @override
  ConsumerState<EquipmentGuiderPanel> createState() =>
      _EquipmentGuiderPanelState();
}

class _EquipmentGuiderPanelState extends ConsumerState<EquipmentGuiderPanel> {
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
      await ref.read(phd2SettingsProvider.notifier).hydrateFromServer(api);
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
      await ref.read(phd2SettingsProvider.notifier).persistToServer(api);
      if (!mounted) return;
      messenger.showSnackBar(
        const SnackBar(content: Text('PHD2 settings saved to daemon.')),
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
    final connection = ref.watch(equipmentConnectionProvider);
    final connN = ref.read(equipmentConnectionProvider.notifier);
    final phd2 = ref.watch(phd2SettingsProvider);
    final phd2N = ref.read(phd2SettingsProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('PHD2 connection'),
        EditableTextRow(
          label: 'Host',
          currentValue: phd2.host,
          getCanonical: () => ref.read(phd2SettingsProvider).host,
          parse: phd2N.setHost,
        ),
        EditableNumberRow(
          label: 'Port',
          currentValue: phd2.port.toString(),
          getCanonical: () => ref.read(phd2SettingsProvider).port.toString(),
          parse: (s) {
            final v = int.tryParse(s);
            if (v != null) phd2N.setPort(v);
          },
        ),
        EditableTextRow(
          label: 'Profile',
          currentValue: phd2.phd2Profile,
          getCanonical: () => ref.read(phd2SettingsProvider).phd2Profile,
          parse: phd2N.setPhd2Profile,
          hint: 'PHD2 equipment profile, not OpenAstroAra profile',
        ),
        SettingsSwitchRow(
          label: 'Auto-connect on boot',
          helpKey: 'eq.auto_connect_on_boot',
          value: connection.autoConnect(EquipmentDeviceType.guider),
          onChanged: (v) => connN.setAutoConnect(EquipmentDeviceType.guider, v),
          hint: 'Off by default — guider connect starts PHD2 client',
        ),
        const SettingsSectionHeader('Dithering'),
        SettingsSwitchRow(
          label: 'Enable dithering',
          value: phd2.ditherEnabled,
          onChanged: phd2N.setDitherEnabled,
        ),
        EditableNumberRow(
          label: 'Dither every N frames',
          currentValue: phd2.ditherEveryNFrames.toString(),
          getCanonical: () =>
              ref.read(phd2SettingsProvider).ditherEveryNFrames.toString(),
          parse: (s) {
            final v = int.tryParse(s);
            if (v != null) phd2N.setDitherEveryNFrames(v);
          },
        ),
        EditableNumberRow(
          label: 'Dither pixels',
          helpKey: 'eq.guider.dither_pixels',
          currentValue: phd2.ditherPixels.toString(),
          getCanonical: () =>
              ref.read(phd2SettingsProvider).ditherPixels.toString(),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) phd2N.setDitherPixels(v);
          },
        ),
        EditableNumberRow(
          label: 'Settle threshold (pixels)',
          helpKey: 'eq.guider.settle_pixels',
          currentValue: phd2.settlePixels.toString(),
          getCanonical: () =>
              ref.read(phd2SettingsProvider).settlePixels.toString(),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) phd2N.setSettlePixels(v);
          },
        ),
        EditableNumberRow(
          label: 'Settle time (s)',
          currentValue: phd2.settleTimeSec.toString(),
          getCanonical: () =>
              ref.read(phd2SettingsProvider).settleTimeSec.toString(),
          parse: (s) {
            final v = int.tryParse(s);
            if (v != null) phd2N.setSettleTimeSec(v);
          },
        ),
        EditableNumberRow(
          label: 'Settle timeout (s)',
          helpKey: 'eq.guider.settle_timeout_sec',
          currentValue: phd2.settleTimeoutSec.toString(),
          getCanonical: () =>
              ref.read(phd2SettingsProvider).settleTimeoutSec.toString(),
          parse: (s) {
            final v = int.tryParse(s);
            if (v != null) phd2N.setSettleTimeoutSec(v);
          },
        ),
        const SettingsSectionHeader('Calibration'),
        SettingsSwitchRow(
          label: 'Force calibration each session',
          helpKey: 'eq.guider.force_calibration_each_session',
          value: phd2.forceCalibrationEachSession,
          onChanged: phd2N.setForceCalibrationEachSession,
        ),
        const SettingsRow(
          label: 'Re-calibrate on meridian flip',
          value: 'Edit in Settings → Safety → Policies',
          hint: '§35 meridian-flip behaviour',
        ),
        const SettingsSectionHeader('Guider engine'),
        EditableNumberRow(
          label: 'Guide focal length (mm)',
          helpKey: 'eq.guider.guide_focal_length',
          currentValue: phd2.guideFocalLength.toString(),
          getCanonical: () =>
              ref.read(phd2SettingsProvider).guideFocalLength.toString(),
          parse: (s) {
            final v = int.tryParse(s);
            if (v != null) phd2N.setGuideFocalLength(v);
          },
        ),
        EditableNumberRow(
          label: 'Guide pixel size (µm)',
          helpKey: 'eq.guider.guide_pixel_size',
          currentValue: phd2.guidePixelSize.toString(),
          getCanonical: () =>
              ref.read(phd2SettingsProvider).guidePixelSize.toString(),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) phd2N.setGuidePixelSize(v);
          },
        ),
        EditableNumberRow(
          label: 'RA aggressiveness (0–1)',
          helpKey: 'eq.guider.ra_aggressiveness',
          currentValue: phd2.raAggressiveness.toString(),
          getCanonical: () =>
              ref.read(phd2SettingsProvider).raAggressiveness.toString(),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) phd2N.setRaAggressiveness(v);
          },
        ),
        EditableNumberRow(
          label: 'Dec aggressiveness (0–1)',
          helpKey: 'eq.guider.dec_aggressiveness',
          currentValue: phd2.decAggressiveness.toString(),
          getCanonical: () =>
              ref.read(phd2SettingsProvider).decAggressiveness.toString(),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) phd2N.setDecAggressiveness(v);
          },
        ),
        EditableNumberRow(
          label: 'Minimum move (px)',
          helpKey: 'eq.guider.minimum_move',
          currentValue: phd2.minimumMove.toString(),
          getCanonical: () =>
              ref.read(phd2SettingsProvider).minimumMove.toString(),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) phd2N.setMinimumMove(v);
          },
        ),
        SettingsDropdownRow<String>(
          label: 'Dec guide mode',
          helpKey: 'eq.guider.dec_guide_mode',
          value: phd2.decGuideMode,
          items: const {
            'auto': 'Auto',
            'north': 'North',
            'south': 'South',
            'off': 'Off',
          },
          onChanged: (v) {
            if (v != null) phd2N.setDecGuideMode(v);
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
