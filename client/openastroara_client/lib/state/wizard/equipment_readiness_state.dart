import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/equipment_readiness.dart';
import '../../models/profile_draft.dart' show EquipmentSlots;
import '../../models/server.dart';
import '../../services/device_facts_source.dart';
import '../settings/equipment_connection_state.dart';

/// Seam for tests: the readiness notifier builds its [DeviceFactsSource]
/// through this factory, so fakes drop in without touching the notifier.
final deviceFactsSourceFactoryProvider =
    Provider<DeviceFactsSource Function(AraServer)>(
        (_) => AraDeviceFactsSource.new);

/// §76.3 — the Wizard 2.0 "Your equipment" readiness map: per assigned
/// fact-bearing device, connect + read + classify, all device types in
/// parallel (entering the screen fires one [readAll]; a card's Recheck fires
/// [recheck] for just that type).
final wizardEquipmentReadinessProvider = NotifierProvider.autoDispose<
    WizardEquipmentReadiness,
    Map<EquipmentDeviceType, DeviceReadiness>>(WizardEquipmentReadiness.new);

class WizardEquipmentReadiness
    extends Notifier<Map<EquipmentDeviceType, DeviceReadiness>> {
  @override
  Map<EquipmentDeviceType, DeviceReadiness> build() => const {};

  /// Monotonic run id — a readAll started after a profile-switch or re-entry
  /// invalidates the in-flight one's writes (same generation idiom as the
  /// server's polar-align loop).
  int _run = 0;

  /// Per-type recheck generations (review r1): two rechecks on the SAME card
  /// must resolve by START order, not completion order — a double-tap whose
  /// first (stale) request finishes last must not overwrite the newer result.
  /// Kept per-type (not folded into [_run]) so a recheck never cancels an
  /// in-flight readAll's OTHER cards. Cleared by readAll, whose own landing
  /// for a type defers to any recheck started after it.
  final Map<EquipmentDeviceType, int> _typeRun = {};

  /// Read every assigned fact-bearing device in parallel. Cards flip to
  /// [ReadinessState.reading] immediately, then land individually as their
  /// device finishes — no barrier, the fastest card goes green first.
  Future<void> readAll(AraServer server, EquipmentSlots slots) async {
    final run = ++_run;
    _typeRun.clear();
    final assigned = <EquipmentDeviceType, String>{
      if (slots.cameraDeviceId != null)
        EquipmentDeviceType.camera: slots.cameraDeviceId!,
      if (slots.mountDeviceId != null)
        EquipmentDeviceType.mount: slots.mountDeviceId!,
      if (slots.filterWheelDeviceId != null)
        EquipmentDeviceType.filterWheel: slots.filterWheelDeviceId!,
      if (slots.focuserDeviceId != null)
        EquipmentDeviceType.focuser: slots.focuserDeviceId!,
      if (slots.rotatorDeviceId != null)
        EquipmentDeviceType.rotator: slots.rotatorDeviceId!,
    };
    state = {
      for (final t in assigned.keys)
        t: DeviceReadiness(
            type: t, label: _genericLabel(t), state: ReadinessState.reading),
    };
    if (assigned.isEmpty) return;
    final source = ref.read(deviceFactsSourceFactoryProvider)(server);
    try {
      await Future.wait(assigned.entries.map((e) async {
        final result = await _readOne(source, e.key, e.value);
        // A recheck started after this readAll owns the card now (it holds a
        // _typeRun entry) — defer to it.
        if (_run == run && !_typeRun.containsKey(e.key)) {
          state = {...state, e.key: result};
        }
      }));
    } finally {
      source.close();
    }
  }

  /// Re-run one device (the card's Recheck button after an AlpacaBridge fix).
  Future<void> recheck(
      AraServer server, EquipmentDeviceType type, String assignedId) async {
    final run = _run; // a full readAll supersedes any in-flight recheck
    final typeRun = (_typeRun[type] ?? 0) + 1; // newer recheck wins the card
    _typeRun[type] = typeRun;
    state = {
      ...state,
      type: (state[type] ??
              DeviceReadiness(
                  type: type,
                  label: _genericLabel(type),
                  state: ReadinessState.reading))
          .copyWith(state: ReadinessState.reading),
    };
    final source = ref.read(deviceFactsSourceFactoryProvider)(server);
    try {
      final result = await _readOne(source, type, assignedId);
      if (_run == run && _typeRun[type] == typeRun) {
        state = {...state, type: result};
      }
    } finally {
      source.close();
    }
  }

  Future<DeviceReadiness> _readOne(DeviceFactsSource source,
      EquipmentDeviceType type, String assignedId) async {
    try {
      final device = await source.resolve(type, assignedId);
      if (device == null) {
        return DeviceReadiness(
          type: type,
          label: _genericLabel(type),
          state: ReadinessState.unreachable,
          gaps: const [
            DeviceGap('Device', FactNeed.required,
                'The assigned device is not on the bridge right now. Check power + USB, then Recheck.'),
          ],
        );
      }
      final label = device.name.isNotEmpty ? device.name : _genericLabel(type);
      final setupUri = alpacaDeviceSetupUri(device);
      if (!await source.connect(device)) {
        return DeviceReadiness(
          type: type,
          label: label,
          state: ReadinessState.unreachable,
          setupUri: setupUri,
          gaps: const [
            DeviceGap('Connection', FactNeed.required,
                'The device never finished connecting. Check it in AlpacaBridge, then Recheck.'),
          ],
        );
      }
      switch (type) {
        case EquipmentDeviceType.camera:
          return ReadinessRules.camera(
              label, setupUri, await source.cameraGeometry());
        case EquipmentDeviceType.mount:
          return ReadinessRules.mount(label, setupUri,
              await source.telescopeOptics(), await source.mountProps());
        case EquipmentDeviceType.filterWheel:
          return ReadinessRules.filterWheel(
              label, setupUri, await source.filterWheelSlots());
        case EquipmentDeviceType.focuser:
          return ReadinessRules.focuser(
              label, setupUri, await source.focuserProps());
        case EquipmentDeviceType.rotator:
          return ReadinessRules.rotator(
              label, setupUri, await source.rotatorProps());
        // Fact-free types never enter the readiness map (readAll only seeds
        // the five above).
        default:
          return DeviceReadiness(
              type: type, label: label, state: ReadinessState.ready);
      }
      // Deliberately broad (review r2): an Error escaping a fact read would
      // otherwise reject out of readAll's Future.wait after the other cards
      // landed, leaving this card stuck on "reading" forever. Any throwable
      // maps to the retryable unreachable card instead.
      // ignore: avoid_catches_without_on_clauses
    } catch (e) {
      return DeviceReadiness(
        type: type,
        label: _genericLabel(type),
        state: ReadinessState.unreachable,
        gaps: [
          DeviceGap('Connection', FactNeed.required,
              'Reading the device failed (${describeReadinessError(e)}). Recheck to retry.'),
        ],
      );
    }
  }

  static String _genericLabel(EquipmentDeviceType t) {
    switch (t) {
      case EquipmentDeviceType.camera:
        return 'Camera';
      case EquipmentDeviceType.mount:
        return 'Mount';
      case EquipmentDeviceType.filterWheel:
        return 'Filter wheel';
      case EquipmentDeviceType.focuser:
        return 'Focuser';
      case EquipmentDeviceType.rotator:
        return 'Rotator';
      case EquipmentDeviceType.guider:
        return 'Guider';
      case EquipmentDeviceType.flatPanel:
        return 'Flat panel';
      case EquipmentDeviceType.dome:
        return 'Dome';
      case EquipmentDeviceType.weather:
        return 'Weather';
      case EquipmentDeviceType.safetyMonitor:
        return 'Safety monitor';
      case EquipmentDeviceType.switchDevice:
        return 'Switch';
    }
  }
}

/// Short user-facing gist of a read failure — never a raw DioException dump
/// (request URL / internal addresses). Mirrors describeEquipmentError but kept
/// local so this state file doesn't import the API layer for one string.
String describeReadinessError(Object e) {
  final s = e.toString();
  // DioException.toString() leads with "DioException [<type>]: <message>".
  final m = RegExp(r'^DioException \[[^\]]*\]:?\s*(.*)$').firstMatch(s);
  if (m != null) {
    final gist = m.group(1) ?? '';
    return gist.split('\n').first.trim().isEmpty
        ? 'network error'
        : gist.split('\n').first.trim();
  }
  return s.replaceFirst('Exception: ', '').split('\n').first;
}
