import 'package:flutter/material.dart';
import '../../../util/friendly_error.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/guider_equipment_choices.dart';
import '../../../services/profile_api.dart';
import '../../../state/guider/guider_equipment_state.dart';
import '../../../state/saved_server_state.dart';
import '../../../state/settings/equipment_connection_state.dart';
import '../../../state/settings/optics_settings_state.dart';
import '../../../state/settings/panel_save_registry.dart';
import '../../../state/settings/phd2_settings_state.dart';
import '../../../util/guide_optics.dart';
import '../../../util/host_port.dart';
import '../../../widgets/guider/guider_setup_wizard.dart';
import '../../../widgets/profile/profile_import_flow.dart'
    show friendlyDaemonError;
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §63 OpenAstro Guider panel — editable. Phase 12h.6k added the daemon
/// round-trip for the §63 OpenAstro Guider fields (host/port/profile + dithering +
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

class _EquipmentGuiderPanelState extends ConsumerState<EquipmentGuiderPanel>
    with PanelSaveRegistration {
  String? _lastError;

  // §63.17 guider equipment — transient discovery/apply UI state.
  bool _discovering = false;
  bool _applying = false;
  List<String>? _discoveredServers;
  String? _equipmentStatus;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _hydrate());
  }

  Future<void> _hydrate() async {
    final api = _api();
    if (api == null) return;
    try {
      // §63.19 — optics first: the OAG-derived guide focal length reads the
      // main optics section, which is hydrated by a different panel.
      await ref.read(opticsSettingsProvider.notifier).hydrateFromServer(api);
      await ref.read(phd2SettingsProvider.notifier).hydrateFromServer(api);
      _syncDerivedFocalLength();
    } catch (e) {
      if (mounted) {
        setState(
          () =>
              _lastError = friendlyError(e, action: 'load your saved settings'),
        );
      }
    }
  }

  /// §63.19 — when the setup type is OAG the guide focal length is derived
  /// from the main optics (focal_length_mm × reducer_factor), not
  /// user-entered. There is no cross-section reactive pattern for derived
  /// settings in this client, so the recompute is explicit: on panel load,
  /// on setup-type change, and before every save/apply.
  void _syncDerivedFocalLength() {
    if (ref.read(phd2SettingsProvider).guiderSetupType != 'oag') return;
    final optics = ref.read(opticsSettingsProvider);
    ref
        .read(phd2SettingsProvider.notifier)
        .setGuideFocalLength(
          derivedOagGuideFocalLength(
            optics.focalLengthMm,
            optics.reducerFactor,
          ),
        );
  }

  @override
  Future<void> panelSave() => _save();

  Future<void> _save() async {
    setState(() => _lastError = null);
    final api = _api();
    final messenger = ScaffoldMessenger.of(context);
    if (api == null) {
      setState(
        () => _lastError = 'Not connected — connect to your rig to save this.',
      );
      messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
      return;
    }
    _syncDerivedFocalLength();
    try {
      await ref.read(phd2SettingsProvider.notifier).persistToServer(api);
      if (!mounted) return;
      messenger.showSnackBar(const SnackBar(content: Text('Saved.')));
    } catch (e) {
      if (!mounted) return;
      setState(() => _lastError = friendlyError(e, action: 'save that'));
      messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
    }
  }

  Future<void> _discoverAlpaca() async {
    setState(() {
      _discovering = true;
      _equipmentStatus = null;
      _discoveredServers = null;
    });
    try {
      final servers = await ref
          .read(guiderEquipmentProvider.notifier)
          .discoverAlpaca();
      if (!mounted) return;
      setState(() {
        _discoveredServers = servers;
        _equipmentStatus = servers.isEmpty
            ? 'No Alpaca servers answered on the guider\'s network.'
            : 'Found ${servers.length} Alpaca '
                  'server${servers.length == 1 ? '' : 's'} — tap one to fill '
                  'host/port.';
      });
    } catch (e) {
      if (!mounted) return;
      setState(
        () => _equipmentStatus = friendlyDaemonError(
          e,
          fallback: "Couldn't discover Alpaca devices",
        ),
      );
    } finally {
      if (mounted) setState(() => _discovering = false);
    }
  }

  /// Save the panel's OpenAstro Guider settings (so the daemon-side profile carries the
  /// current selections), then ask the daemon to re-push them to the guider.
  Future<void> _applyToGuider() async {
    final api = _api();
    final messenger = ScaffoldMessenger.of(context);
    if (api == null) {
      messenger.showSnackBar(
        const SnackBar(
          content: Text('Not connected — connect to your rig to save this.'),
        ),
      );
      return;
    }
    setState(() {
      _applying = true;
      _equipmentStatus = null;
    });
    _syncDerivedFocalLength();
    try {
      await ref.read(phd2SettingsProvider.notifier).persistToServer(api);
      await ref.read(guiderEquipmentProvider.notifier).pushProfile();
      if (!mounted) return;
      messenger.showSnackBar(
        const SnackBar(
          content: Text('Equipment selection pushed to the guider.'),
        ),
      );
    } catch (e) {
      if (!mounted) return;
      final msg = friendlyDaemonError(e, fallback: "Couldn't apply to the guider");
      setState(() => _equipmentStatus = msg);
      messenger.showSnackBar(SnackBar(content: Text(msg)));
    } finally {
      if (mounted) setState(() => _applying = false);
    }
  }

  ProfileApi? _api() {
    final server = ref.read(activeServerProvider);
    return server == null ? null : ProfileApi(server);
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
        const SettingsSectionHeader('OpenAstro Guider connection'),
        EditableTextRow(
          label: 'Host',
          helpKey: 'eq.guider.host',
          currentValue: phd2.host,
          getCanonical: () => ref.read(phd2SettingsProvider).host,
          parse: phd2N.setHost,
        ),
        EditableNumberRow(
          label: 'Port',
          helpKey: 'eq.guider.port',
          currentValue: phd2.port.toString(),
          getCanonical: () => ref.read(phd2SettingsProvider).port.toString(),
          parse: (s) {
            final v = int.tryParse(s);
            if (v != null) phd2N.setPort(v);
          },
        ),
        EditableTextRow(
          label: 'Profile',
          helpKey: 'eq.guider.profile',
          currentValue: phd2.phd2Profile,
          getCanonical: () => ref.read(phd2SettingsProvider).phd2Profile,
          parse: phd2N.setPhd2Profile,
          hint: 'OpenAstro Guider equipment profile, not OpenAstroAra profile',
        ),
        SettingsSwitchRow(
          label: 'Auto-connect on boot',
          helpKey: 'eq.auto_connect_on_boot',
          value: connection.autoConnect(EquipmentDeviceType.guider),
          onChanged: (v) => connN.setAutoConnect(EquipmentDeviceType.guider, v),
          hint:
              'Off by default — guider connect starts the OpenAstro Guider client',
        ),
        const SettingsSectionHeader('Dithering'),
        SettingsSwitchRow(
          label: 'Enable dithering',
          value: phd2.ditherEnabled,
          onChanged: phd2N.setDitherEnabled,
        ),
        EditableNumberRow(
          label: 'Dither every N frames',
          helpKey: 'eq.guider.dither_every_n',
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
          helpKey: 'eq.guider.settle_time',
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
          helpKey: 'eq.guider.recal_on_flip',
          value: 'Edit in Settings → Safety → Policies',
          hint: 'What the guider does across a meridian flip',
        ),
        const SettingsSectionHeader('Guider engine'),
        // §63.19 — how the guide camera sees the sky: through its own guide
        // scope (focal length user-entered) or an off-axis guider behind the
        // main optics (focal length derived).
        SettingsDropdownRow<String>(
          label: 'Guide setup',
          helpKey: 'eq.guider.setup_type',
          value: phd2.guiderSetupType,
          items: const {
            'guide_scope': 'Guide scope',
            'oag': 'Off-axis guider (OAG)',
          },
          onChanged: (v) {
            if (v == null) return;
            phd2N.setGuiderSetupType(v);
            _syncDerivedFocalLength();
          },
        ),
        if (phd2.guiderSetupType == 'oag')
          Builder(
            builder: (context) {
              final optics = ref.watch(opticsSettingsProvider);
              final derived = derivedOagGuideFocalLength(
                optics.focalLengthMm,
                optics.reducerFactor,
              );
              return SettingsRow(
                label: 'Guide focal length (mm)',
                value: derived == 0 ? 'unset' : '$derived',
                hint: derived == 0
                    ? 'Derived from main optics — set the telescope focal '
                          'length in Equipment → Optics first'
                    : 'Derived from main optics: '
                          '${_fmtMm(optics.focalLengthMm)} mm × '
                          '${_fmtFactor(optics.reducerFactor)} = $derived mm',
              );
            },
          )
        else
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
        const SettingsSectionHeader('Guider equipment'),
        ..._equipmentSection(context),
        const SizedBox(height: 24),
        if (_lastError != null) ...[
          Text(
            _lastError!,
            style: TextStyle(color: Theme.of(context).colorScheme.error),
          ),
          const SizedBox(height: 12),
        ],
        // Save lives in the settings-shell header (PanelSaveRegistration) —
        // fixed chrome, always visible, no scrolling to find it.
      ],
    );
  }

  /// Trim trailing ".0" noise from the derived-focal-length caption numbers.
  static String _fmtMm(double v) =>
      v == v.roundToDouble() ? v.round().toString() : v.toString();

  static String _fmtFactor(double v) =>
      v == v.roundToDouble() ? '${v.round()}.0' : v.toString();

  /// Dropdown items for a device slot: the daemon's choices plus '' ("daemon
  /// default") plus the current profile value even when the daemon doesn't
  /// list it (disconnected, or a stale selection) — so hydrated state is
  /// always representable and never silently coerced.
  static Map<String, String> _slotItems(List<String> choices, String current) {
    final items = <String, String>{'': '(use the guider\'s own setting)'};
    for (final c in choices) {
      items[c] = c;
    }
    if (current.isNotEmpty) items.putIfAbsent(current, () => current);
    return items;
  }

  // §63.17 — equipment pickers fed by GET /equipment/guider/choices, daemon-
  // side Alpaca discovery, and the on-demand profile push. Save (header) only
  // persists the profile; "Apply to guider" persists AND pushes.
  List<Widget> _equipmentSection(BuildContext context) {
    final phd2 = ref.watch(phd2SettingsProvider);
    final phd2N = ref.read(phd2SettingsProvider.notifier);
    final equipment = ref.watch(guiderEquipmentProvider);
    final envelope = equipment.value;
    final connected = envelope?.connected ?? false;
    final choices = envelope?.choices ?? const GuiderEquipmentChoices();
    final refreshing = equipment.isLoading;

    return [
      Wrap(
        spacing: 12,
        runSpacing: 8,
        children: [
          OutlinedButton.icon(
            onPressed: refreshing
                ? null
                : () => ref.read(guiderEquipmentProvider.notifier).refresh(),
            icon: refreshing
                ? const SizedBox(
                    width: 16,
                    height: 16,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.refresh, size: 18),
            label: const Text('Refresh choices'),
          ),
          OutlinedButton.icon(
            onPressed: (_discovering || !connected)
                ? null
                : () => _discoverAlpaca(),
            icon: _discovering
                ? const SizedBox(
                    width: 16,
                    height: 16,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.travel_explore, size: 18),
            label: const Text('Discover Alpaca'),
          ),
        ],
      ),
      if (!connected)
        Padding(
          padding: const EdgeInsets.only(top: 8),
          child: Text(
            'Guider not connected — connect the guider to load device choices, '
            'discover Alpaca servers, or apply a selection.',
            style: Theme.of(context).textTheme.bodySmall,
          ),
        ),
      if (_discoveredServers != null && _discoveredServers!.isNotEmpty)
        Padding(
          padding: const EdgeInsets.only(top: 8),
          child: Wrap(
            spacing: 8,
            runSpacing: 4,
            children: [
              for (final s in _discoveredServers!)
                ActionChip(
                  label: Text(s),
                  onPressed: () {
                    final parsed = parseHostPort(s);
                    if (parsed.host != null) {
                      phd2N.setGuiderAlpacaHost(parsed.host!);
                    }
                    if (parsed.port != null) {
                      phd2N.setGuiderAlpacaPort(parsed.port!);
                    }
                  },
                ),
            ],
          ),
        ),
      SettingsDropdownRow<String>(
        label: 'Guide camera',
        helpKey: 'eq.guider.guide_camera',
        value: phd2.guiderCamera,
        items: _slotItems(choices.cameras, phd2.guiderCamera),
        onChanged: (v) {
          if (v != null) phd2N.setGuiderCamera(v);
        },
      ),
      EditableTextRow(
        label: 'Guide camera ID',
        helpKey: 'eq.guider.guide_camera_id',
        currentValue: phd2.guiderCameraId,
        getCanonical: () => ref.read(phd2SettingsProvider).guiderCameraId,
        parse: phd2N.setGuiderCameraId,
        hint: 'Only needed when two cameras share a name',
      ),
      SettingsDropdownRow<String>(
        label: 'Guide mount',
        helpKey: 'eq.guider.guide_mount',
        value: phd2.guiderMount,
        items: _slotItems(choices.mounts, phd2.guiderMount),
        onChanged: (v) {
          if (v != null) phd2N.setGuiderMount(v);
        },
      ),
      SettingsDropdownRow<String>(
        label: 'Aux mount',
        helpKey: 'eq.guider.aux_mount',
        value: phd2.guiderAuxMount,
        items: _slotItems(choices.auxMounts, phd2.guiderAuxMount),
        onChanged: (v) {
          if (v != null) phd2N.setGuiderAuxMount(v);
        },
      ),
      SettingsDropdownRow<String>(
        label: 'Rotator',
        helpKey: 'eq.guider.rotator',
        value: phd2.guiderRotator,
        items: _slotItems(choices.rotators, phd2.guiderRotator),
        onChanged: (v) {
          if (v != null) phd2N.setGuiderRotator(v);
        },
      ),
      EditableTextRow(
        label: 'Alpaca host',
        helpKey: 'eq.guider.alpaca_host',
        currentValue: phd2.guiderAlpacaHost,
        getCanonical: () => ref.read(phd2SettingsProvider).guiderAlpacaHost,
        parse: phd2N.setGuiderAlpacaHost,
        hint: 'Leave blank to keep the guider\'s setting',
      ),
      EditableNumberRow(
        label: 'Alpaca port',
        helpKey: 'eq.guider.alpaca_port',
        currentValue: phd2.guiderAlpacaPort.toString(),
        getCanonical: () =>
            ref.read(phd2SettingsProvider).guiderAlpacaPort.toString(),
        parse: (s) {
          final v = int.tryParse(s);
          if (v != null) phd2N.setGuiderAlpacaPort(v);
        },
      ),
      Padding(
        padding: const EdgeInsets.only(top: 8),
        child: Wrap(
          spacing: 8,
          runSpacing: 4,
          crossAxisAlignment: WrapCrossAlignment.center,
          children: [
            FilledButton.icon(
              onPressed: (_applying || !connected)
                  ? null
                  : () => _applyToGuider(),
              icon: _applying
                  ? const SizedBox(
                      width: 16,
                      height: 16,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : const Icon(Icons.send, size: 18),
              label: const Text('Apply to guider'),
            ),
            // The step-by-step alternative to this panel — connection →
            // camera → optics → mount → apply → darks, OpenAstro Guider-wizard style.
            TextButton.icon(
              onPressed: _applying
                  ? null
                  : () => showGuiderSetupWizard(context),
              icon: const Icon(Icons.auto_fix_high, size: 16),
              label: const Text('Setup wizard…'),
            ),
          ],
        ),
      ),
      if (_equipmentStatus != null)
        Padding(
          padding: const EdgeInsets.only(top: 8),
          child: Text(
            _equipmentStatus!,
            style: Theme.of(context).textTheme.bodySmall,
          ),
        ),
    ];
  }
}
