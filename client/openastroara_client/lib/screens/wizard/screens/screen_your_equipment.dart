import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../../models/discovered_device.dart';
import '../../../models/equipment_readiness.dart';
import '../../../models/profile_draft.dart';
import '../../../services/equipment_discovery_api.dart';
import '../wizard_facts_apply.dart' show inferFilterType;
import '../../../state/saved_server_state.dart';
import '../../../state/settings/equipment_connection_state.dart';
import '../../../state/wizard_state.dart';
import '../../../state/wizard/equipment_readiness_state.dart';
import '../../../theme/ara_colors.dart';
import '../wizard_facts_apply.dart';
import '../wizard_form_kit.dart';
import 'screen_equipment_discovery.dart'
    show DeviceChoice, DiscoverySheet, equipmentDiscoveryApiFactoryProvider;

// ── shared parse helpers (same guards as the other wizard screens) ──────────

int? _toInt(String raw) {
  final t = raw.trim();
  return t.isEmpty ? null : int.tryParse(t);
}

void _assignDouble(String raw, void Function(double?) set) {
  final t = raw.trim();
  if (t.isEmpty) {
    set(null);
    return;
  }
  final v = double.tryParse(t);
  if (v != null) set(v);
}

/// The five fact-bearing device types that get a full readiness card
/// (§76.3); everything else is a name-only row in "Other equipment".
const List<EquipmentDeviceType> _factTypes = [
  EquipmentDeviceType.camera,
  EquipmentDeviceType.mount,
  EquipmentDeviceType.filterWheel,
  EquipmentDeviceType.focuser,
  EquipmentDeviceType.rotator,
];

const List<EquipmentDeviceType> _otherTypes = [
  EquipmentDeviceType.dome,
  EquipmentDeviceType.weather,
  EquipmentDeviceType.safetyMonitor,
  EquipmentDeviceType.flatPanel,
];

String _typeLabel(EquipmentDeviceType t) => switch (t) {
      EquipmentDeviceType.camera => 'Camera',
      EquipmentDeviceType.mount => 'Mount (Telescope)',
      EquipmentDeviceType.filterWheel => 'Filter wheel',
      EquipmentDeviceType.focuser => 'Focuser',
      EquipmentDeviceType.rotator => 'Rotator',
      EquipmentDeviceType.dome => 'Dome',
      EquipmentDeviceType.weather => 'Observing conditions',
      EquipmentDeviceType.safetyMonitor => 'Safety monitor',
      EquipmentDeviceType.flatPanel => 'Flat panel',
      EquipmentDeviceType.switchDevice => 'Switch',
      EquipmentDeviceType.guider => 'Guider',
    };

String? _slotGet(EquipmentSlots e, EquipmentDeviceType t) => switch (t) {
      EquipmentDeviceType.camera => e.cameraDeviceId,
      EquipmentDeviceType.mount => e.mountDeviceId,
      EquipmentDeviceType.filterWheel => e.filterWheelDeviceId,
      EquipmentDeviceType.focuser => e.focuserDeviceId,
      EquipmentDeviceType.rotator => e.rotatorDeviceId,
      EquipmentDeviceType.dome => e.domeDeviceId,
      EquipmentDeviceType.weather => e.observingConditionsDeviceId,
      EquipmentDeviceType.safetyMonitor => e.safetyMonitorDeviceId,
      EquipmentDeviceType.flatPanel => e.flatPanelDeviceId,
      _ => null,
    };

void _slotSet(EquipmentSlots e, EquipmentDeviceType t, String? v) {
  switch (t) {
    case EquipmentDeviceType.camera:
      e.cameraDeviceId = v;
    case EquipmentDeviceType.mount:
      e.mountDeviceId = v;
    case EquipmentDeviceType.filterWheel:
      e.filterWheelDeviceId = v;
    case EquipmentDeviceType.focuser:
      e.focuserDeviceId = v;
    case EquipmentDeviceType.rotator:
      e.rotatorDeviceId = v;
    case EquipmentDeviceType.dome:
      e.domeDeviceId = v;
    case EquipmentDeviceType.weather:
      e.observingConditionsDeviceId = v;
    case EquipmentDeviceType.safetyMonitor:
      e.safetyMonitorDeviceId = v;
    case EquipmentDeviceType.flatPanel:
      e.flatPanelDeviceId = v;
    default:
      break;
  }
}

/// §76.2 Screen 3 — "Your equipment": discover, auto-assign, then VERIFY.
/// Facts are read from Alpaca and shown on a card per device; a gap deep-links
/// to the device's Alpaca setup page (AlpacaBridge owns device facts — §76.1)
/// with a Recheck to re-read. Ara-only tunables live in a per-card Details
/// disclosure, defaulted so the happy path never opens it.
class ScreenYourEquipment extends ConsumerStatefulWidget {
  const ScreenYourEquipment({super.key});

  @override
  ConsumerState<ScreenYourEquipment> createState() =>
      _ScreenYourEquipmentState();
}

class _ScreenYourEquipmentState extends ConsumerState<ScreenYourEquipment> {
  late final ProfileDraft _draft;

  /// Names for slot-only assignments (the readiness map covers fact types).
  final Map<EquipmentDeviceType, String> _otherNames = {};
  final Map<String, String> _switchNames = {};

  bool _preparing = false;
  String? _prepareError;

  @override
  void initState() {
    super.initState();
    _draft = ref.read(wizardControllerProvider).draft;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) unawaited(_prepare());
    });
  }

  /// Entry pass: auto-assign any unassigned slot with exactly one discovered
  /// device (first entry only — re-entry respects explicit user clears), then
  /// read every assigned fact-bearing device in parallel.
  Future<void> _prepare() async {
    // The awaitable variant: entry via post-frame can beat the saved-server
    // storage read, and "still loading" must not collapse into "no server".
    final server = await ref.read(activeServerFutureProvider.future);
    if (!mounted) return;
    if (server == null) {
      setState(() =>
          _prepareError = 'No active server — connect to a daemon first.');
      return;
    }
    setState(() {
      _preparing = true;
      _prepareError = null;
    });
    try {
      if (!_draft.equipmentAutoAssigned) {
        final api = ref.read(equipmentDiscoveryApiFactoryProvider)(server);
        try {
          await Future.wait([
            for (final t in [..._factTypes, ..._otherTypes])
              if (_slotGet(_draft.equipment, t) == null) _autoAssign(api, t),
          ]);
        } finally {
          api.close();
        }
        _draft.equipmentAutoAssigned = true;
      }
      if (!mounted) return;
      setState(() {}); // show the assigned slots before the reads land
      // Deliberately re-read on EVERY entry (not just the first): the cards
      // display live hardware truth, and the user's fix-in-AlpacaBridge →
      // come-back loop depends on re-entry re-verifying. Cheap in practice —
      // already-connected devices answer the first ~750 ms status poll; the
      // ~15 s worst case only applies to a device that is actually
      // connecting, during which its card shows the reading spinner.
      await ref
          .read(wizardEquipmentReadinessProvider.notifier)
          .readAll(server, _draft.equipment);
      // Deliberately broad (same never-crash contract as _autoAssign): any
      // throwable lands in the retryable banner, not an unhandled async error.
      // ignore: avoid_catches_without_on_clauses
    } catch (e) {
      if (mounted) {
        setState(() => _prepareError =
            'Discovery failed (${describeReadinessError(e)}). '
            'Check the bridge, then Re-scan.');
      }
    } finally {
      if (mounted) setState(() => _preparing = false);
    }
  }

  /// One slot's auto-assign: a single discovered device claims the slot;
  /// zero or several leave it for the user (ambiguity gets a question, per
  /// §76.2 — never a guess between two cameras). Discovery errors leave the
  /// slot unassigned; the card's Choose path retries interactively.
  Future<void> _autoAssign(
      EquipmentDiscoveryApi api, EquipmentDeviceType type) async {
    try {
      final devices = await api.discover(type);
      if (devices.length == 1) {
        _slotSet(_draft.equipment, type, devices.single.uniqueId);
        if (_otherTypes.contains(type)) {
          _otherNames[type] = devices.single.name;
        }
      }
      // Deliberately broad: this screen's contract is never-crash — any
      // throwable (incl. an Error from a malformed response) leaves the slot
      // unassigned; the row's Choose affordance surfaces errors interactively.
      // ignore: avoid_catches_without_on_clauses
    } catch (_) {}
  }

  Future<void> _choose(EquipmentDeviceType type) async {
    final server = ref.read(activeServerProvider);
    final api =
        server == null ? null : ref.read(equipmentDiscoveryApiFactoryProvider)(server);
    final picked = await showModalBottomSheet<DeviceChoice>(
      context: context,
      backgroundColor: AraColors.bgPanel,
      isScrollControlled: true,
      builder: (_) => DiscoverySheet(
        slotLabel: _typeLabel(type),
        type: type,
        api: api,
      ),
    );
    api?.close();
    if (picked == null || !mounted) return;
    setState(() {
      _slotSet(_draft.equipment, type, picked.device?.uniqueId);
      if (_otherTypes.contains(type)) {
        if (picked.device != null) {
          _otherNames[type] = picked.device!.name;
        } else {
          _otherNames.remove(type);
        }
      }
    });
    // Re-verify the changed slot (fact types only — slot-only types carry no
    // facts to read).
    final id = picked.device?.uniqueId;
    if (_factTypes.contains(type) && server != null) {
      final notifier = ref.read(wizardEquipmentReadinessProvider.notifier);
      if (id != null) {
        unawaited(notifier.recheck(server, type, id));
      } else {
        unawaited(notifier.readAll(server, _draft.equipment));
      }
    }
  }

  Future<void> _addSwitch() async {
    final server = ref.read(activeServerProvider);
    final api =
        server == null ? null : ref.read(equipmentDiscoveryApiFactoryProvider)(server);
    final picked = await showModalBottomSheet<DeviceChoice>(
      context: context,
      backgroundColor: AraColors.bgPanel,
      isScrollControlled: true,
      builder: (_) => DiscoverySheet(
        slotLabel: 'Switch',
        type: EquipmentDeviceType.switchDevice,
        api: api,
      ),
    );
    api?.close();
    if (picked?.device == null || !mounted) return;
    final device = picked!.device!;
    setState(() {
      if (!_draft.equipment.switchDeviceIds.contains(device.uniqueId)) {
        _draft.equipment.switchDeviceIds.add(device.uniqueId);
      }
      _switchNames[device.uniqueId] = device.name;
    });
  }

  void _recheck(EquipmentDeviceType type) {
    final server = ref.read(activeServerProvider);
    final id = _slotGet(_draft.equipment, type);
    if (server == null || id == null) return;
    unawaited(ref
        .read(wizardEquipmentReadinessProvider.notifier)
        .recheck(server, type, id));
  }

  @override
  Widget build(BuildContext context) {
    // Every landed read writes its facts into the draft — the save mappers
    // consume the draft, and inline fallback fields must not fight fresh
    // device truth.
    ref.listen(wizardEquipmentReadinessProvider, (prev, next) {
      for (final entry in next.entries) {
        if (prev?[entry.key] != entry.value &&
            entry.value.state != ReadinessState.reading) {
          applyFactsToDraft(_draft, entry.value);
        }
      }
      setState(() {}); // facts feed card bodies + fallback field visibility
    });
    final readiness = ref.watch(wizardEquipmentReadinessProvider);

    return WizardScreenScaffold(
      step: 3,
      intro: 'ARA read your gear straight from AlpacaBridge — confirm it, '
          'don\'t re-type it. Anything marked ⚠ is fixed at the source: open '
          'the device in AlpacaBridge, correct it, then Recheck.',
      children: [
        if (_prepareError != null) _errorBanner(context, _prepareError!),
        if (_preparing && readiness.isEmpty)
          const Padding(
            padding: EdgeInsets.symmetric(vertical: 24),
            child: Center(child: CircularProgressIndicator()),
          ),
        for (final type in _factTypes)
          _slotGet(_draft.equipment, type) == null
              ? _UnassignedRow(
                  label: _typeLabel(type),
                  onChoose: () => unawaited(_choose(type)),
                )
              : _DeviceCard(
                  readiness: readiness[type] ??
                      DeviceReadiness(
                          type: type,
                          label: _typeLabel(type),
                          state: ReadinessState.reading),
                  draft: _draft,
                  onRecheck: () => _recheck(type),
                  onChange: () => unawaited(_choose(type)),
                ),
        const SizedBox(height: 8),
        const WizardSectionHeader('Other equipment'),
        for (final type in _otherTypes)
          _OtherRow(
            label: _typeLabel(type),
            assigned: _slotGet(_draft.equipment, type) == null
                ? null
                : (_otherNames[type] ?? _slotGet(_draft.equipment, type)!),
            onChoose: () => unawaited(_choose(type)),
          ),
        _switchSection(context),
      ],
    );
  }

  Widget _switchSection(BuildContext context) {
    final ids = _draft.equipment.switchDeviceIds;
    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
        decoration: BoxDecoration(
          color: AraColors.bgPanel,
          borderRadius: BorderRadius.circular(4),
          border: Border.all(color: AraColors.border),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(children: [
              Expanded(
                child: Text(
                  ids.isEmpty
                      ? 'Switches — none (add every switch hub your rig uses)'
                      : 'Switches — ${ids.length} assigned',
                  style: Theme.of(context).textTheme.bodyMedium,
                ),
              ),
              TextButton(
                onPressed: () => unawaited(_addSwitch()),
                child: const Text('Add switch'),
              ),
            ]),
            if (ids.isNotEmpty)
              Wrap(
                spacing: 8,
                runSpacing: 8,
                children: [
                  for (final id in ids)
                    InputChip(
                      label: Text(_switchNames[id] ?? id),
                      onDeleted: () => setState(() {
                        ids.remove(id);
                        _switchNames.remove(id);
                      }),
                    ),
                ],
              ),
          ],
        ),
      ),
    );
  }

  Widget _errorBanner(BuildContext context, String message) => Padding(
        padding: const EdgeInsets.only(bottom: 16),
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
          decoration: BoxDecoration(
            color: AraColors.accentError.withValues(alpha: 0.12),
            borderRadius: BorderRadius.circular(4),
            border: Border.all(color: AraColors.accentError),
          ),
          child: Row(children: [
            const Icon(Icons.error_outline,
                size: 18, color: AraColors.accentError),
            const SizedBox(width: 8),
            Expanded(
                child:
                    Text(message, style: Theme.of(context).textTheme.bodySmall)),
            TextButton(
              onPressed: _preparing ? null : () => unawaited(_prepare()),
              child: const Text('Re-scan'),
            ),
          ]),
        ),
      );
}

/// A fact type nothing was auto-assigned to: a quiet row, not an amber card —
/// "no rotator" is a normal rig, not a problem.
class _UnassignedRow extends StatelessWidget {
  const _UnassignedRow({required this.label, required this.onChoose});
  final String label;
  final VoidCallback onChoose;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
        decoration: BoxDecoration(
          color: AraColors.bgPanel,
          borderRadius: BorderRadius.circular(4),
          border: Border.all(color: AraColors.border),
        ),
        child: Row(children: [
          const Icon(Icons.radio_button_unchecked,
              size: 18, color: AraColors.textDisabled),
          const SizedBox(width: 10),
          Expanded(
            child: Text('$label — none',
                style: Theme.of(context)
                    .textTheme
                    .bodyMedium
                    ?.copyWith(color: AraColors.textSecondary)),
          ),
          TextButton(onPressed: onChoose, child: const Text('Choose')),
        ]),
      ),
    );
  }
}

class _OtherRow extends StatelessWidget {
  const _OtherRow(
      {required this.label, required this.assigned, required this.onChoose});
  final String label;
  final String? assigned;
  final VoidCallback onChoose;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
        decoration: BoxDecoration(
          color: AraColors.bgPanel,
          borderRadius: BorderRadius.circular(4),
          border: Border.all(color: AraColors.border),
        ),
        child: Row(children: [
          Icon(
            assigned != null ? Icons.check_circle : Icons.radio_button_unchecked,
            size: 18,
            color: assigned != null
                ? AraColors.accentConnected
                : AraColors.textDisabled,
          ),
          const SizedBox(width: 10),
          Expanded(
            child: Text(
              assigned != null ? '$label — $assigned' : '$label — none',
              style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                    color: assigned != null
                        ? AraColors.textPrimary
                        : AraColors.textSecondary,
                  ),
            ),
          ),
          TextButton(
              onPressed: onChoose,
              child: Text(assigned == null ? 'Choose' : 'Change')),
        ]),
      ),
    );
  }
}

/// One verified device: state icon + real name, its read facts, its gaps with
/// the AlpacaBridge deep link, and a Details disclosure for the Ara-only
/// tunables (§76.1 — values with no Alpaca home).
class _DeviceCard extends ConsumerWidget {
  const _DeviceCard({
    required this.readiness,
    required this.draft,
    required this.onRecheck,
    required this.onChange,
  });

  final DeviceReadiness readiness;
  final ProfileDraft draft;
  final VoidCallback onRecheck;
  final VoidCallback onChange;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final r = readiness;
    final (icon, color) = switch (r.state) {
      ReadinessState.reading => (Icons.sync, AraColors.textSecondary),
      ReadinessState.ready => (Icons.check_circle, AraColors.accentConnected),
      ReadinessState.gaps => (Icons.warning_amber_rounded, AraColors.accentBusy),
      ReadinessState.unreachable => (Icons.error_outline, AraColors.accentError),
    };
    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
        decoration: BoxDecoration(
          color: AraColors.bgPanel,
          borderRadius: BorderRadius.circular(4),
          border: Border.all(
              color: r.hasBlockingGap && r.state != ReadinessState.reading
                  ? color
                  : AraColors.border),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(children: [
              if (r.state == ReadinessState.reading)
                const SizedBox(
                    width: 18,
                    height: 18,
                    child: CircularProgressIndicator(strokeWidth: 2))
              else
                Icon(icon, size: 18, color: color),
              const SizedBox(width: 10),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(r.label,
                        style: Theme.of(context).textTheme.bodyMedium),
                    Text(_typeLabel(r.type),
                        style: Theme.of(context)
                            .textTheme
                            .bodySmall
                            ?.copyWith(color: AraColors.textSecondary)),
                  ],
                ),
              ),
              TextButton(onPressed: onChange, child: const Text('Change')),
            ]),
            if (r.facts.isNotEmpty) ...[
              const SizedBox(height: 6),
              Wrap(
                spacing: 14,
                runSpacing: 4,
                children: [
                  for (final f in r.facts)
                    Text('${f.label}: ${f.value}',
                        style: Theme.of(context)
                            .textTheme
                            .bodySmall
                            ?.copyWith(color: AraColors.textSecondary)),
                ],
              ),
            ],
            for (final gap in r.gaps)
              Padding(
                padding: const EdgeInsets.only(top: 6),
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Icon(
                      gap.need == FactNeed.required
                          ? Icons.warning_amber_rounded
                          : Icons.info_outline,
                      size: 15,
                      color: gap.need == FactNeed.required
                          ? AraColors.accentBusy
                          : AraColors.textDisabled,
                    ),
                    const SizedBox(width: 6),
                    Expanded(
                      child: Text('${gap.label} — ${gap.hint}',
                          style: Theme.of(context)
                              .textTheme
                              .bodySmall
                              ?.copyWith(color: AraColors.textSecondary)),
                    ),
                  ],
                ),
              ),
            if (r.state != ReadinessState.reading) ...[
              const SizedBox(height: 6),
              Wrap(spacing: 8, children: [
                if (r.hasBlockingGap && r.setupUri != null)
                  OutlinedButton.icon(
                    onPressed: () => unawaited(_openBridge(context, r.setupUri!)),
                    icon: const Icon(Icons.open_in_new, size: 15),
                    label: const Text('Open in AlpacaBridge'),
                  ),
                if (r.hasBlockingGap ||
                    r.gaps.isNotEmpty ||
                    r.state == ReadinessState.unreachable)
                  OutlinedButton.icon(
                    onPressed: onRecheck,
                    icon: const Icon(Icons.refresh, size: 15),
                    label: const Text('Recheck'),
                  ),
              ]),
            ],
            _DetailsDisclosure(readiness: r, draft: draft),
          ],
        ),
      ),
    );
  }

  Future<void> _openBridge(BuildContext context, Uri uri) async {
    final messenger = ScaffoldMessenger.of(context);
    final ok = await launchUrl(uri, mode: LaunchMode.externalApplication);
    if (!ok) {
      messenger.showSnackBar(SnackBar(
          content: Text('Couldn\'t open $uri — browse to it manually.')));
    }
  }
}

/// Per-card "Details" expander: the Ara-only tunables the retired per-device
/// screens used to ask about, defaulted so the happy path never opens this,
/// plus inline fallback fields for required facts the device didn't report
/// (the §76.1 escape hatch for non-reporting gear — visually secondary).
class _DetailsDisclosure extends StatelessWidget {
  const _DetailsDisclosure({required this.readiness, required this.draft});
  final DeviceReadiness readiness;
  final ProfileDraft draft;

  bool _gapFor(String label) =>
      readiness.gaps.any((g) => g.label.contains(label));

  @override
  Widget build(BuildContext context) {
    final children = switch (readiness.type) {
      EquipmentDeviceType.camera => _camera(),
      EquipmentDeviceType.mount => _mount(),
      EquipmentDeviceType.filterWheel => _filterWheel(),
      EquipmentDeviceType.focuser => _focuser(),
      EquipmentDeviceType.rotator => _rotator(),
      _ => const <Widget>[],
    };
    if (children.isEmpty) return const SizedBox.shrink();
    // Material(transparency) so the ExpansionTile's ink renders inside the
    // card's DecoratedBox without the invisible-splash assertion.
    return Theme(
      data: Theme.of(context).copyWith(dividerColor: Colors.transparent),
      child: Material(
        type: MaterialType.transparency,
        child: _tile(context, children),
      ),
    );
  }

  Widget _tile(BuildContext context, List<Widget> children) {
    return ExpansionTile(
        tilePadding: EdgeInsets.zero,
        childrenPadding: const EdgeInsets.only(top: 4),
        title: Text('Details',
            style: Theme.of(context)
                .textTheme
                .bodySmall
                ?.copyWith(color: AraColors.textSecondary)),
        children: children);
  }

  List<Widget> _camera() {
    final c = draft.camera;
    return [
      if (_gapFor('Pixel size'))
        WizardTextField(
          label: 'Pixel size (µm) — manual fallback',
          initialValue: c.pixelSizeMicrons?.toString(),
          helperText: 'Only needed because the camera didn\'t report it.',
          keyboardType: const TextInputType.numberWithOptions(decimal: true),
          inputFormatters: WizardInput.unsignedDecimal,
          onChanged: (v) => _assignDouble(v, (d) => c.pixelSizeMicrons = d),
        ),
      WizardTextField(
        label: 'Cooling target (°C)',
        initialValue: c.coolingTargetC?.toString(),
        hint: 'e.g. -10',
        keyboardType:
            const TextInputType.numberWithOptions(decimal: true, signed: true),
        inputFormatters: WizardInput.signedDecimal,
        onChanged: (v) => _assignDouble(v, (d) => c.coolingTargetC = d),
      ),
      WizardTextField(
        label: 'Default gain',
        initialValue: c.defaultGain?.toString(),
        keyboardType: TextInputType.number,
        inputFormatters: WizardInput.unsignedInt,
        onChanged: (v) => c.defaultGain = _toInt(v),
      ),
      WizardTextField(
        label: 'Default offset',
        initialValue: c.defaultOffset?.toString(),
        keyboardType: TextInputType.number,
        inputFormatters: WizardInput.unsignedInt,
        onChanged: (v) => c.defaultOffset = _toInt(v),
      ),
      WizardTextField(
        label: 'Read noise (e⁻)',
        initialValue: c.readNoiseE?.toString(),
        helperText: 'Spec-sheet value — Alpaca doesn\'t report it. Feeds '
            'exposure planning; leave blank to set later in Options.',
        keyboardType: const TextInputType.numberWithOptions(decimal: true),
        inputFormatters: WizardInput.unsignedDecimal,
        onChanged: (v) => _assignDouble(v, (d) => c.readNoiseE = d),
      ),
      WizardTextField(
        label: 'Peak QE (%)',
        initialValue: c.qePeakPct?.toString(),
        keyboardType: const TextInputType.numberWithOptions(decimal: true),
        inputFormatters: WizardInput.unsignedDecimal,
        onChanged: (v) => _assignDouble(v, (d) => c.qePeakPct = d),
      ),
    ];
  }

  List<Widget> _mount() {
    final t = draft.telescope;
    final m = draft.mount;
    return [
      if (_gapFor('Focal length'))
        WizardTextField(
          label: 'Focal length (mm) — manual fallback',
          initialValue: t.focalLengthMm?.toString(),
          helperText: 'Only needed because the driver didn\'t report it.',
          keyboardType: const TextInputType.numberWithOptions(decimal: true),
          inputFormatters: WizardInput.unsignedDecimal,
          onChanged: (v) => _assignDouble(v, (d) => t.focalLengthMm = d),
        ),
      if (_gapFor('Aperture'))
        WizardTextField(
          label: 'Aperture (mm) — manual fallback',
          initialValue: t.apertureMm?.toString(),
          keyboardType: const TextInputType.numberWithOptions(decimal: true),
          inputFormatters: WizardInput.unsignedDecimal,
          onChanged: (v) => _assignDouble(v, (d) => t.apertureMm = d),
        ),
      _StatefulDropdown<MeridianFlipBehavior>(
        label: 'Meridian flip',
        value: m.meridianFlip,
        entries: const [
          DropdownMenuEntry(value: MeridianFlipBehavior.auto, label: 'Auto'),
          DropdownMenuEntry(
              value: MeridianFlipBehavior.prompt, label: 'Prompt me'),
          DropdownMenuEntry(value: MeridianFlipBehavior.never, label: 'Never'),
        ],
        onChanged: (v) => m.meridianFlip = v ?? MeridianFlipBehavior.auto,
      ),
      _StatefulDropdown<ParkPositionMode>(
        label: 'Park position',
        value: m.parkMode,
        entries: const [
          DropdownMenuEntry(
              value: ParkPositionMode.syncCurrent, label: 'Sync current'),
          DropdownMenuEntry(
              value: ParkPositionMode.defineManually, label: 'Define manually'),
        ],
        onChanged: (v) => m.parkMode = v ?? ParkPositionMode.syncCurrent,
      ),
      WizardTextField(
        label: 'Settle time after slew (s)',
        initialValue: m.settleTimeAfterSlew?.inSeconds.toString(),
        keyboardType: TextInputType.number,
        inputFormatters: WizardInput.unsignedInt,
        onChanged: (v) {
          final s = _toInt(v);
          m.settleTimeAfterSlew = s == null ? null : Duration(seconds: s);
        },
      ),
    ];
  }

  List<Widget> _filterWheel() {
    // The manual escape hatch for a wheel whose driver reports no slot names
    // (review r1) — same secondary posture as the other fallback fields. A
    // comma-separated list keeps it one field; types are inferred like the
    // Alpaca-read path so both entry routes agree.
    if (!_gapFor('Filter names')) return const [];
    return [
      WizardTextField(
        label: 'Filter names — manual fallback',
        initialValue: draft.filterWheel.filters
            .map((f) => f.name)
            .whereType<String>()
            .join(', '),
        hint: 'L, R, G, B, Hα, OIII, SII',
        helperText: 'Only needed because the wheel didn\'t report its slots. '
            'Comma-separated, in slot order.',
        onChanged: (v) {
          final names = v
              .split(',')
              .map((n) => n.trim())
              .where((n) => n.isNotEmpty)
              .toList();
          draft.filterWheel.filters
            ..clear()
            ..addAll(names.map((n) => FilterDef()
              ..name = n
              ..type = inferFilterType(n)));
        },
      ),
    ];
  }

  List<Widget> _focuser() {
    final f = draft.focuser;
    return [
      if (_gapFor('Step size'))
        WizardTextField(
          label: 'Step size (µm/step) — optional',
          initialValue: f.stepSizeMicrons?.toString(),
          helperText: 'Refines autofocus seeding; fine to leave blank.',
          keyboardType: const TextInputType.numberWithOptions(decimal: true),
          inputFormatters: WizardInput.unsignedDecimal,
          onChanged: (v) => _assignDouble(v, (d) => f.stepSizeMicrons = d),
        ),
      WizardTextField(
        label: 'Backlash in (steps)',
        initialValue: f.backlashInSteps?.toString(),
        keyboardType: TextInputType.number,
        inputFormatters: WizardInput.unsignedInt,
        onChanged: (v) => f.backlashInSteps = _toInt(v),
      ),
      WizardTextField(
        label: 'Backlash out (steps)',
        initialValue: f.backlashOutSteps?.toString(),
        keyboardType: TextInputType.number,
        inputFormatters: WizardInput.unsignedInt,
        onChanged: (v) => f.backlashOutSteps = _toInt(v),
      ),
    ];
  }

  List<Widget> _rotator() {
    final r = draft.rotator;
    return [
      WizardTextField(
        label: 'Minimum angle (°)',
        initialValue: r.minAngleDeg?.toString(),
        helperText: 'Your mechanical cable-wrap limits — not an Alpaca '
            'concept, so ARA has to ask.',
        keyboardType:
            const TextInputType.numberWithOptions(decimal: true, signed: true),
        inputFormatters: WizardInput.signedDecimal,
        onChanged: (v) => _assignDouble(v, (d) => r.minAngleDeg = d),
      ),
      WizardTextField(
        label: 'Maximum angle (°)',
        initialValue: r.maxAngleDeg?.toString(),
        keyboardType:
            const TextInputType.numberWithOptions(decimal: true, signed: true),
        inputFormatters: WizardInput.signedDecimal,
        onChanged: (v) => _assignDouble(v, (d) => r.maxAngleDeg = d),
      ),
    ];
  }
}

/// A dropdown that owns its selection state (the disclosure widgets are
/// stateless; the draft holds truth but DropdownMenu wants a rebuild source).
class _StatefulDropdown<T> extends StatefulWidget {
  const _StatefulDropdown({
    required this.label,
    required this.value,
    required this.entries,
    required this.onChanged,
  });
  final String label;
  final T value;
  final List<DropdownMenuEntry<T>> entries;
  final ValueChanged<T?> onChanged;

  @override
  State<_StatefulDropdown<T>> createState() => _StatefulDropdownState<T>();
}

class _StatefulDropdownState<T> extends State<_StatefulDropdown<T>> {
  late T _value = widget.value;

  @override
  Widget build(BuildContext context) {
    return WizardDropdown<T>(
      label: widget.label,
      value: _value,
      entries: widget.entries,
      onChanged: (v) {
        setState(() => _value = v ?? _value);
        widget.onChanged(v);
      },
    );
  }
}
