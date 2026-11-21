import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';
import '../../../util/friendly_error.dart';
import '../../../l10n/l10n.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/guider_equipment_choices.dart';
import '../../../services/profile_api.dart';
import '../../../state/guider/guider_equipment_state.dart';
import '../../../state/guider/guider_state.dart';
import '../../../state/profile_management_state.dart';
import '../../../state/settings/equipment_connection_state.dart';
import '../../../state/settings/loaded_section.dart';
import '../../../state/settings/optics_settings_state.dart';
import '../../../state/settings/phd2_settings_state.dart';
import '../../../util/guide_optics.dart';
import '../../../util/host_port.dart';
import '../../../widgets/equipment/guider_calibration_dialog.dart';
import '../../../widgets/equipment/guider_dialog.dart';
import '../../../widgets/guider/guider_setup_wizard.dart';
import '../../../widgets/profile/profile_import_flow.dart'
    show friendlyDaemonError;
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/auto_connect_row.dart';
import '../../../widgets/settings/section_load_state.dart';
import '../../../widgets/settings/section_panel.dart';
import '../../../widgets/settings/settings_row.dart';
import '../../../widgets/adaptive_controls.dart';

/// The guider Profile picker's entries: Automatic first, naming the profile the
/// guider is on when nothing is pinned, then the guider's own profiles. A pinned
/// profile the guider no longer lists (or a guider not connected) stays visible.
Map<int, String> guiderProfileItems(int pinned, Map<int, String> profiles, String? current) => {
      // Name the guider's profile only when Automatic is what put it there.
      0: pinned != 0 || current == null || current.isEmpty ? 'Automatic' : 'Automatic ($current)',
      // The server maps a guider profile without an id to 0, which would replace Automatic.
      for (final e in profiles.entries)
        if (e.key > 0) e.key: e.value,
      if (pinned != 0 && !profiles.containsKey(pinned)) pinned: 'Profile #$pinned',
    };

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
    with SectionPanel {
  String? _lastError;

  // Every change saves on its own (like the device pages), shortly after the
  // edit so a burst of changes is one PUT, through the section module (ADR
  // 0015): only over the rig's copy, with its tag. A pending save is flushed
  // on leaving.
  bool _syncingDerived = false;
  bool _loading = false; // the panel's own loads are not edits
  Timer? _autoSave;
  Future<void> Function()? _pendingFlush;
  // The settings as last loaded or saved: a write that leaves them unchanged
  // is not a change and saves nothing.
  String? _savedJson;

  // §63.19 — the guide focal length of an OAG is derived from the main
  // optics, so a guider Save needs the optics in from the rig too.
  @override
  List<PanelSection> get sections => [phd2SettingsProvider, opticsSettingsProvider];

  String _json() {
    final v = ref.read(phd2SettingsProvider).value;
    return v == null ? '' : jsonEncode(ProfileApi.phd2SettingsToJson(v));
  }

  // §63.17 guider equipment — transient discovery/apply UI state.
  bool _applying = false;
  String? _equipmentStatus;

  // §63.20 / #1306 — real device names behind every Alpaca endpoint we know
  // of (the daemon's choice strings, the profile's stored host/port, every
  // discovered server), keyed "host:port|type/N". The pickers synthesize a
  // daemon-format `Alpaca <Slot> [host:port/N]` option per device, so a pick
  // always carries its server and the push can retarget the guider. Without
  // this the panel offered only the bare "Alpaca Camera" the daemon lists,
  // which pushes nothing about the server and leaves the guider on
  // localhost:0.
  Map<String, String> get _alpacaNames =>
      ref.watch(guiderAlpacaDevicesProvider).names;

  /// Load the guider settings and the optics from the rig (on open, Retry
  /// and Reload, and after a rig or profile change), then derive an OAG focal
  /// length and read the Alpaca servers the settings name.
  @override
  Future<void> loadSections() async {
    _autoSave?.cancel();
    _pendingFlush = null;
    _loading = true;
    try {
      await super.loadSections();
      // A load the section's own first load superseded lands nothing and
      // fails nothing: load once more (the module's rigCopy).
      for (final section in sections) {
        final now = mounted ? ref.read(section) : null;
        if (now != null && !now.canSave && now.lastFailure == null) {
          await ref.read(section.notifier).rigCopy();
        }
      }
    } finally {
      _loading = false;
    }
    if (!mounted) return;
    final phd2 = ref.read(phd2SettingsProvider).value;
    if (phd2 == null || !ref.read(opticsSettingsProvider).canSave) return;
    _syncDerivedFocalLength();
    _savedJson = _json();
    // Choices already loaded before this panel built: ref.listen only
    // fires on a later change, so resolve their servers here too.
    final loaded = ref.read(guiderEquipmentProvider).value?.choices;
    final endpoints = <String>[
      if (phd2.guiderAlpacaHost.isNotEmpty && phd2.guiderAlpacaPort > 0)
        '${phd2.guiderAlpacaHost}:${phd2.guiderAlpacaPort}',
      ...choiceEndpoints(loaded),
    ];
    // Opening the panel reads the servers again, as it did on its own.
    await ref
        .read(guiderAlpacaDevicesProvider.notifier)
        .resolve(endpoints, fresh: true);
  }

  /// §63.19 — when the setup type is OAG the guide focal length is derived
  /// from the main optics (focal_length_mm × reducer_factor), not
  /// user-entered. There is no cross-section reactive pattern for derived
  /// settings in this client, so the recompute is explicit: on panel load,
  /// on setup-type change, and before every save/apply.
  void _syncDerivedFocalLength() {
    if (ref.read(phd2SettingsProvider).value?.guiderSetupType != 'oag') return;
    // Not loaded (a rig or profile change since the panel loaded it): keep
    // the stored value rather than derive one from no optics.
    final optics = ref.read(opticsSettingsProvider).value;
    if (optics == null) return;
    _syncingDerived = true;
    try {
      ref
          .read(phd2SettingsProvider.notifier)
          .setGuideFocalLength(
            derivedOagGuideFocalLength(
              optics.focalLengthMm,
              optics.reducerFactor,
            ),
          );
    } finally {
      _syncingDerived = false;
    }
  }

  /// An edit schedules a save; a new copy from the rig (a load, a save's
  /// answer, a live-sync reload after the daemon wrote it) is what the rig
  /// holds already, so it saves nothing.
  void _onSectionChanged(
    LoadedSection<Phd2Settings>? before,
    LoadedSection<Phd2Settings> now,
  ) {
    if (!now.canSave || _loading) return;
    if (before?.etag != now.etag || before?.source != now.source) {
      if (!(_autoSave?.isActive ?? false)) _savedJson = _json();
      return;
    }
    _scheduleSave();
  }

  void _scheduleSave() {
    if (_syncingDerived) return;
    if (_json() == _savedJson) {
      _autoSave?.cancel(); // changed back before the save went out
      return;
    }
    final api = _api();
    final notifier = ref.read(phd2SettingsProvider.notifier);
    _pendingFlush = api == null ? null : () => notifier.save(api);
    _autoSave?.cancel();
    _autoSave = Timer(const Duration(milliseconds: 500), () {
      _pendingFlush = null;
      _save();
    });
  }

  /// A Host/Port edit made within the save delay must reach the daemon before
  /// Connect reads the host from it: send the pending save now and wait for it.
  Future<void> _flushPendingSave() async {
    if (!(_autoSave?.isActive ?? false)) return;
    _autoSave!.cancel();
    _pendingFlush = null;
    await _save();
  }

  @override
  void dispose() {
    // Leaving the page right after an edit still saves it.
    if (_autoSave?.isActive ?? false) {
      _autoSave!.cancel();
      _pendingFlush?.call().ignore();
    }
    super.dispose();
  }

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
    // Mark it saved before the PUT: the server's echo replaces the state while
    // the PUT is in flight, and must read as "already saved", not a new edit.
    final previous = _savedJson;
    _savedJson = _json();
    try {
      await ref.read(phd2SettingsProvider.notifier).save(api);
    } catch (e) {
      _savedJson = previous;
      if (!mounted) return;
      setState(() => _lastError = _failure(e, context.l10n.actionSave));
      messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
    }
  }

  /// A refusal by the section module says it in its own words.
  String _failure(Object e, String action) =>
      e is SectionNotLoadedException || e is SectionConflictException
      ? e.toString()
      : friendlyError(context.l10n, e, action: action);

  Future<void> _discoverAlpaca() async {
    setState(() => _equipmentStatus = null);
    try {
      final servers =
          await ref
              .read(guiderAlpacaDevicesProvider.notifier)
              .discover(fresh: false);
      if (!mounted) return;
      setState(() {
        _equipmentStatus = servers.isEmpty
            ? 'No Alpaca servers answered on the guider\'s network.'
            : 'Found ${servers.length} Alpaca '
                  'server${servers.length == 1 ? '' : 's'} — their devices '
                  'are now in the pickers; tap one to fill host/port.';
      });
    } catch (e) {
      if (!mounted) return;
      setState(
        () => _equipmentStatus = friendlyDaemonError(
          e,
          fallback: "Couldn't discover Alpaca devices",
        ),
      );
    }
  }

  /// Save the panel's OpenAstro Guider settings (so the daemon-side profile carries the
  /// current selections), then ask the daemon to re-push them to the guider.
  Future<void> _applyToGuider() async {
    final current = ref.read(phd2SettingsProvider).value;
    if (current == null) return; // not offered until the rig's copy is in
    final api = _api();
    final messenger = ScaffoldMessenger.of(context);
    final unserved = describeUnservedAlpacaSelection(current);
    if (unserved != null) {
      // #1306 — the daemon would refuse this push with the same reason (422);
      // say it here before anything is saved or disconnected.
      setState(() => _equipmentStatus = unserved);
      messenger.showSnackBar(SnackBar(content: Text(unserved)));
      return;
    }
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
    // This save covers any pending auto-save of the same edits.
    _autoSave?.cancel();
    final previous = _savedJson;
    _savedJson = _json();
    var saved = false;
    try {
      await ref.read(phd2SettingsProvider.notifier).save(api);
      saved = true;
      await ref.read(guiderEquipmentProvider.notifier).pushProfile();
      if (!mounted) return;
      messenger.showSnackBar(
        const SnackBar(
          content: Text('Equipment selection pushed to the guider.'),
        ),
      );
    } catch (e) {
      if (!saved) _savedJson = previous;
      if (!mounted) return;
      final msg = e is SectionNotLoadedException || e is SectionConflictException
          ? e.toString()
          : friendlyDaemonError(e, fallback: "Couldn't apply to the guider");
      setState(() => _equipmentStatus = msg);
      messenger.showSnackBar(SnackBar(content: Text(msg)));
    } finally {
      if (mounted) setState(() => _applying = false);
    }
  }

  ProfileApi? _api() => ref.read(profileApiProvider);

  @override
  Widget build(BuildContext context) {
    // Not in from the rig: Connect stays (it reads the host from the rig
    // itself), the settings wait with Retry (ADR 0015).
    if (sectionGate() case final gate?) {
      return ListView(
        padding: const EdgeInsets.all(24),
        children: [
          const SettingsSectionHeader('Connection'),
          GuiderConnectionSection(onBeforeConnect: _flushPendingSave),
          gate,
        ],
      );
    }
    final phd2 = ref.watch(phd2SettingsProvider).value!;
    final phd2N = ref.read(phd2SettingsProvider.notifier);
    ref.listen(phd2SettingsProvider, _onSectionChanged);
    final guiderStatus = ref.watch(guiderStatusProvider).asData?.value;
    Phd2Settings now() => ref.read(phd2SettingsProvider).value!;

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        ?sectionConflict(),
        const SettingsSectionHeader('Connection'),
        GuiderConnectionSection(onBeforeConnect: _flushPendingSave),
        EditableTextRow(
          label: 'Host',
          helpKey: 'eq.guider.host',
          currentValue: phd2.host,
          getCanonical: () => now().host,
          parse: phd2N.setHost,
        ),
        EditableNumberRow(
          label: 'Port',
          helpKey: 'eq.guider.port',
          currentValue: phd2.port.toString(),
          getCanonical: () => now().port.toString(),
          parse: (s) {
            final v = int.tryParse(s);
            if (v != null) phd2N.setPort(v);
          },
        ),
        SettingsDropdownRow<int>(
          key: const ValueKey('guider_profile_picker'),
          label: 'Profile',
          helpKey: 'eq.guider.profile',
          value: phd2.guiderProfileId,
          items: guiderProfileItems(phd2.guiderProfileId, guiderStatus?.profiles ?? const {}, guiderStatus?.currentProfile),
          onChanged: (v) {
            if (v != null) phd2N.setGuiderProfileId(v);
          },
        ),
        AutoConnectRow(
          EquipmentDeviceType.guider,
          hint: 'Off by default — guider connect starts the OpenAstro Guider client',
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
              now().ditherEveryNFrames.toString(),
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
              now().ditherPixels.toString(),
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
              now().settlePixels.toString(),
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
              now().settleTimeSec.toString(),
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
              now().settleTimeoutSec.toString(),
          parse: (s) {
            final v = int.tryParse(s);
            if (v != null) phd2N.setSettleTimeoutSec(v);
          },
        ),
        const SettingsSectionHeader('Calibration'),
        const GuiderCalibrationSection(),
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
              final optics = ref.watch(opticsSettingsProvider).value;
              // Optics not loaded: say so, never derive from defaults.
              if (optics == null) {
                return SectionNotLoadedRows(
                  opticsSettingsProvider,
                  padding: EdgeInsets.zero,
                );
              }
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
                now().guideFocalLength.toString(),
            parse: (s) {
              final v = int.tryParse(s);
              if (v != null) phd2N.setGuideFocalLength(v);
            },
          ),
        if (phd2.guiderSetupType != 'oag' &&
            guideFocalLengthLooksCopiedFromMainScope(
              phd2.guideFocalLength,
              ref.watch(opticsSettingsProvider).value?.focalLengthMm ?? 0,
              ref.watch(opticsSettingsProvider).value?.reducerFactor ?? 1,
            ))
          Padding(
            padding: const EdgeInsets.only(top: 4, bottom: 4),
            child: Text(
              'The guide focal length is the same as the main scope\'s. '
              'If the guide camera has its own scope, enter that scope\'s '
              'focal length: a wrong value stops polar alignment from solving.',
              key: const ValueKey('guider_focal_length_copied'),
              style: Theme.of(context).textTheme.bodySmall!
                  .copyWith(color: Theme.of(context).colorScheme.error),
            ),
          ),
        EditableNumberRow(
          label: 'Guide pixel size (µm)',
          helpKey: 'eq.guider.guide_pixel_size',
          currentValue: phd2.guidePixelSize.toString(),
          getCanonical: () =>
              now().guidePixelSize.toString(),
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
              now().raAggressiveness.toString(),
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
              now().decAggressiveness.toString(),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) phd2N.setDecAggressiveness(v);
          },
        ),
        EditableNumberRow(
          label: 'RA minimum move (px)',
          helpKey: 'eq.guider.minimum_move',
          currentValue: phd2.minimumMove.toString(),
          getCanonical: () =>
              now().minimumMove.toString(),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) phd2N.setMinimumMove(v);
          },
        ),
        EditableNumberRow(
          label: 'Dec minimum move (px)',
          helpKey: 'eq.guider.dec_minimum_move',
          currentValue: phd2.decMinimumMove.toString(),
          getCanonical: () =>
              now().decMinimumMove.toString(),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) phd2N.setDecMinimumMove(v);
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
        // No Save button: every change saves on its own (_scheduleSave).
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
  Map<String, String> _slotItems(
      List<String> choices, String current, String alpacaType,
      {String blankLabel = '(use the guider\'s own setting)'}) {
    final items = <String, String>{'': blankLabel};
    for (final c in choices) {
      items[c] = friendlyAlpacaChoiceLabel(c, alpacaType, _alpacaNames);
    }
    if (current.isNotEmpty) {
      items.putIfAbsent(
          current, () => friendlyAlpacaChoiceLabel(current, alpacaType, _alpacaNames));
    }
    return items;
  }

  /// The one Alpaca server the guider can use: the camera's `[host:port/N]`
  /// when the camera carries one, else the profile's stored host/port. Null
  /// when neither is known yet, in which case every server's devices are
  /// offered for every slot.
  static String? _servingEndpoint(Phd2Settings phd2) {
    final cam = parseAlpacaChoiceEndpoint(phd2.guiderCamera);
    if (cam != null) return '${cam.host}:${cam.port}';
    if (phd2.guiderAlpacaHost.isNotEmpty && phd2.guiderAlpacaPort > 0) {
      return '${phd2.guiderAlpacaHost}:${phd2.guiderAlpacaPort}';
    }
    return null;
  }

  // §63.17 — equipment pickers fed by GET /equipment/guider/choices, daemon-
  // side Alpaca discovery, and the on-demand profile push. A pick saves to the
  // profile on its own; "Apply to guider" saves AND pushes.
  List<Widget> _equipmentSection(BuildContext context) {
    final phd2 = ref.watch(phd2SettingsProvider).value!;
    Phd2Settings now() => ref.read(phd2SettingsProvider).value!;
    final discovery = ref.watch(guiderDiscoveryProvider);
    final phd2N = ref.read(phd2SettingsProvider.notifier);
    final equipment = ref.watch(guiderEquipmentProvider);
    final envelope = equipment.value;
    final connected = envelope?.connected ?? false;
    final choices = envelope?.choices ?? const GuiderEquipmentChoices();
    final refreshing = equipment.isLoading;
    ref.listen(guiderEquipmentProvider, (_, next) {
      final c = next.value?.choices;
      if (c != null) {
        ref.read(guiderAlpacaDevicesProvider.notifier).resolve(choiceEndpoints(c));
      }
    });
    final serving = _servingEndpoint(phd2);
    final cameraOptions =
        mergedAlpacaOptions(choices.cameras, 'camera', 'Camera', _alpacaNames);
    final mountOptions = mergedAlpacaOptions(
        choices.mounts, 'telescope', 'Mount', _alpacaNames,
        onlyEndpoint: serving);
    final auxOptions = mergedAlpacaOptions(
        choices.auxMounts, 'telescope', 'Mount', _alpacaNames,
        onlyEndpoint: serving);
    final rotatorOptions = mergedAlpacaOptions(
        choices.rotators, 'rotator', 'Rotator', _alpacaNames,
        onlyEndpoint: serving);
    final unserved = describeUnservedAlpacaSelection(phd2);
    // A hydrated mount/aux/rotator on a server other than the camera's: the
    // push keeps the camera's server and drops that device number (the
    // wizard's _isCrossServerPick, surfaced here too).
    final crossServer = <String>[];
    if (serving != null) {
      for (final (slot, sel) in [
        ('Guide mount', phd2.guiderMount),
        ('Aux mount', phd2.guiderAuxMount),
        ('Rotator', phd2.guiderRotator),
      ]) {
        if (!isCrossServerPick(serving, sel)) continue;
        final e = parseAlpacaChoiceEndpoint(sel)!;
        crossServer.add(
            '$slot is on ${e.host}:${e.port}, a different Alpaca server than the '
            'guide camera ($serving); the guider uses one server, so pick it '
            'from the camera\'s server.');
      }
    }

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
                    child: AdaptiveProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.refresh, size: 18),
            label: const Text('Refresh choices'),
          ),
          OutlinedButton.icon(
            onPressed: (discovery.running || !connected)
                ? null
                : () => _discoverAlpaca(),
            icon: discovery.running
                ? const SizedBox(
                    width: 16,
                    height: 16,
                    child: AdaptiveProgressIndicator(strokeWidth: 2),
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
      if (discovery.servers != null && discovery.servers!.isNotEmpty)
        Padding(
          padding: const EdgeInsets.only(top: 8),
          child: Wrap(
            spacing: 8,
            runSpacing: 4,
            children: [
              for (final s in discovery.servers!)
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
        key: const ValueKey('guider_pick_camera'),
        label: 'Guide camera',
        helpKey: 'eq.guider.guide_camera',
        value: phd2.guiderCamera,
        // A new guider has no camera of its own, so the blank entry is
        // "nothing picked", not a working default.
        items: _slotItems(cameraOptions, phd2.guiderCamera, 'camera',
            blankLabel: 'None picked'),
        onChanged: (v) {
          if (v == null) return;
          phd2N.applyPick(
              ref.read(guiderAlpacaDevicesProvider.notifier).pick(v));
        },
      ),
      if (phd2.guiderCamera.trim().isEmpty)
        Padding(
          padding: const EdgeInsets.only(top: 4, bottom: 4),
          child: Text(
            kNoGuideCameraWarning,
            key: const ValueKey('guider_no_camera'),
            style: Theme.of(context)
                .textTheme
                .bodySmall!
                .copyWith(color: Theme.of(context).colorScheme.error),
          ),
        ),
      EditableTextRow(
        label: 'Guide camera ID',
        helpKey: 'eq.guider.guide_camera_id',
        currentValue: phd2.guiderCameraId,
        getCanonical: () => now().guiderCameraId,
        parse: phd2N.setGuiderCameraId,
        hint: 'Only needed when two cameras share a name',
      ),
      SettingsDropdownRow<String>(
        key: const ValueKey('guider_pick_mount'),
        label: 'Guide mount',
        helpKey: 'eq.guider.guide_mount',
        value: phd2.guiderMount,
        items: _slotItems(mountOptions, phd2.guiderMount, 'telescope'),
        onChanged: (v) {
          if (v != null) phd2N.setGuiderMount(v);
        },
      ),
      SettingsDropdownRow<String>(
        label: 'Aux mount',
        helpKey: 'eq.guider.aux_mount',
        value: phd2.guiderAuxMount,
        items: _slotItems(auxOptions, phd2.guiderAuxMount, 'telescope'),
        onChanged: (v) {
          if (v != null) phd2N.setGuiderAuxMount(v);
        },
      ),
      // §63.19 — a rotator is only offered on an off-axis guider. PHD2 turns
      // its calibration by that rotator's every move, which is wrong when the
      // guide camera sits on its own scope and never turns (2026-10-16: a
      // 49° Center-and-Rotate spiralled every dither until guiding was stopped).
      if (Phd2SettingsNotifier.usesGuideCameraRotator(phd2.guiderSetupType))
        SettingsDropdownRow<String>(
          key: const ValueKey('guider_pick_rotator'),
          label: 'Rotator',
          helpKey: 'eq.guider.rotator',
          value: phd2.guiderRotator,
          items: _slotItems(rotatorOptions, phd2.guiderRotator, 'rotator'),
          onChanged: (v) {
            if (v != null) phd2N.setGuiderRotator(v);
          },
        )
      else
        const SettingsRow(
          key: ValueKey('guider_rotator_not_used'),
          label: 'Rotator',
          helpKey: 'eq.guider.rotator',
          value: 'Not used',
          hint: 'A separate guide scope does not turn with the camera rotator; '
              'the guider is told "None". Choose Off-axis guider above if it does.',
        ),
      for (final warning in crossServer)
        Padding(
          padding: const EdgeInsets.only(top: 4, bottom: 4),
          child: Text(
            warning,
            key: const ValueKey('guider_cross_server'),
            style: Theme.of(context)
                .textTheme
                .bodySmall!
                .copyWith(color: Theme.of(context).colorScheme.error),
          ),
        ),
      if (unserved != null)
        Padding(
          padding: const EdgeInsets.only(top: 4, bottom: 4),
          child: Text(
            unserved,
            key: const ValueKey('guider_unserved_alpaca'),
            style: Theme.of(context)
                .textTheme
                .bodySmall!
                .copyWith(color: Theme.of(context).colorScheme.error),
          ),
        ),
      EditableTextRow(
        label: 'Alpaca host',
        helpKey: 'eq.guider.alpaca_host',
        currentValue: phd2.guiderAlpacaHost,
        getCanonical: () => now().guiderAlpacaHost,
        parse: phd2N.setGuiderAlpacaHost,
        hint: 'Leave blank to keep the guider\'s setting',
      ),
      EditableNumberRow(
        label: 'Alpaca port',
        helpKey: 'eq.guider.alpaca_port',
        currentValue: phd2.guiderAlpacaPort.toString(),
        getCanonical: () =>
            now().guiderAlpacaPort.toString(),
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
                      child: AdaptiveProgressIndicator(strokeWidth: 2),
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
