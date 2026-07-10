import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/ws_event.dart';
import '../../widgets/status_indicator.dart';
import '../ws/ws_providers.dart';

/// §42 `equipment.*` fault WS event-type tokens (mirrors `WsEventCatalog` on
/// the server). Routing is by these strings.
abstract final class FaultWsEvents {
  static const fault = 'equipment.fault';
  static const actionTaken = 'equipment.fault_action_taken';

  /// The §60.9 connect alias (`EquipmentEventPublisher`) — folded as the heal
  /// signal: a device that reports connected again clears its standing fault,
  /// covering the manual-reconnect path where no `recovered` action fires
  /// (e.g. after the §42.3 ladder gave up).
  static const connected = 'equipment.connected';
}

/// One device's currently-standing fault, folded from the live
/// `equipment.fault` stream.
class ActiveFault {
  /// Lowercase server `DeviceType` token (`camera`, `telescope`, …).
  final String deviceType;
  final String? deviceName;

  /// Fault-kind wire token (`disconnected`, `tracking_lost`, `stall_timeout`,
  /// `value_mismatch`, `op_error`, `cooling_drift`).
  final String kind;
  final String? details;

  /// Event arrival time — drives the advisory TTL prune.
  final DateTime at;

  /// Chip overlay severity: [StatusLevel.error] for the recovery-tracked kinds
  /// (disconnected / tracking_lost — cleared by `recovered` or a reconnect),
  /// [StatusLevel.busy] (amber) for the advisory one-shot kinds (TTL-pruned).
  final StatusLevel level;

  const ActiveFault({
    required this.deviceType,
    required this.deviceName,
    required this.kind,
    required this.details,
    required this.at,
    required this.level,
  });
}

/// Fault-kind wire token → chip overlay severity. Recovery-tracked kinds run a
/// §42.3 episode and get a definite clear signal, so they hold red; everything
/// else (including an unknown future kind) is a one-shot advisory held amber
/// until the TTL prune.
StatusLevel faultKindLevel(String kind) => switch (kind) {
      'disconnected' || 'tracking_lost' => StatusLevel.error,
      _ => StatusLevel.busy,
    };

/// The set of devices with a standing fault, keyed by the lowercase
/// `DeviceType` token. Reference identity (no value `==`) — change is
/// signalled by the accumulator returning a fresh snapshot, mirroring
/// `DiagnosticsSnapshot`.
class ActiveFaultsSnapshot {
  final Map<String, ActiveFault> byDeviceType;
  const ActiveFaultsSnapshot(
      {this.byDeviceType = const <String, ActiveFault>{}});

  /// Worst standing-fault severity across [deviceTypes], or null when none of
  /// them has a fault. A chip covering two wire tokens (FLAT =
  /// covercalibrator + flatdevice) passes both.
  StatusLevel? worstFor(Set<String> deviceTypes) {
    StatusLevel? worst;
    for (final type in deviceTypes) {
      final fault = byDeviceType[type];
      if (fault == null) continue;
      if (worst == null || statusLevelRank(fault.level) > statusLevelRank(worst)) {
        worst = fault.level;
      }
    }
    return worst;
  }
}

/// Severity ordering shared by the fault blend: error > busy > info >
/// connected > disconnected.
int statusLevelRank(StatusLevel level) => switch (level) {
      StatusLevel.error => 4,
      StatusLevel.busy => 3,
      StatusLevel.info => 2,
      StatusLevel.connected => 1,
      StatusLevel.disconnected => 0,
    };

/// Blends a chip's connection-derived dot with the device's standing-fault
/// overlay: worst-of, so a red fault shows through a green "connected" dot and
/// an advisory turns a healthy chip amber — but a fault can never make a chip
/// look HEALTHIER than its connection state. Pure → unit-testable.
StatusLevel blendFaultLevel(StatusLevel base, StatusLevel? fault) =>
    fault != null && statusLevelRank(fault) > statusLevelRank(base)
        ? fault
        : base;

/// Folds the live `equipment.fault` / `equipment.fault_action_taken` /
/// `equipment.connected` stream into the standing-fault set. Pure — no
/// Riverpod, no I/O, no wall clock (callers pass `now` into [prune]) — so the
/// reduction is unit-testable in isolation, mirroring `DiagnosticsAccumulator`.
class ActiveFaultsAccumulator {
  ActiveFaultsAccumulator({
    this.advisoryTtl = const Duration(minutes: 10),
    this.maxDevices = 32,
  });

  /// How long a one-shot advisory fault (no clear signal exists for it) holds
  /// its chip amber before the periodic prune drops it.
  final Duration advisoryTtl;

  /// Defence against a misbehaving server flooding fabricated device_type
  /// tokens — the real vocabulary is the 12 `DeviceType` values.
  final int maxDevices;

  final Map<String, ActiveFault> _active = <String, ActiveFault>{};

  /// Fold one event and return the new snapshot, or `null` when the event was
  /// not one this accumulator folds or was a no-op — the null lets the caller
  /// skip a state write ([ActiveFaultsSnapshot] has reference identity).
  ActiveFaultsSnapshot? apply(WsEvent event) {
    final changed = switch (event.type) {
      FaultWsEvents.fault => _applyFault(event),
      FaultWsEvents.actionTaken => _applyAction(event),
      FaultWsEvents.connected => _applyConnected(event),
      _ => false,
    };
    return changed ? snapshot : null;
  }

  ActiveFaultsSnapshot get snapshot => ActiveFaultsSnapshot(
      byDeviceType: Map<String, ActiveFault>.unmodifiable(_active));

  bool _applyFault(WsEvent event) {
    final p = event.payload;
    final deviceType = _string(p['device_type']);
    final kind = _string(p['kind']);
    if (deviceType == null || kind == null) return false;
    final level = faultKindLevel(kind);
    final existing = _active[deviceType];
    // Worst-standing wins: an advisory arriving while a recovery-tracked red
    // fault is mid-episode must not soften the chip — the red one still owns
    // the clear signal. Equal severity → the newer fault replaces (fresher
    // details + timestamp).
    if (existing != null &&
        statusLevelRank(existing.level) > statusLevelRank(level)) {
      return false;
    }
    if (existing == null && _active.length >= maxDevices) {
      return false; // cap defence — unreachable under a conformant server
    }
    _active[deviceType] = ActiveFault(
      deviceType: deviceType,
      deviceName: _string(p['device_name']),
      kind: kind,
      details: _string(p['details']),
      at: event.ts,
      level: level,
    );
    return true;
  }

  bool _applyAction(WsEvent event) {
    // Only `recovered` is a clear; the in-flight actions (sequence_paused,
    // reconnecting) and the terminal `gave_up` all leave the device faulted.
    if (_string(event.payload['action']) != 'recovered') return false;
    final deviceType = _string(event.payload['device_type']);
    return deviceType != null && _active.remove(deviceType) != null;
  }

  /// A fresh successful connect is the heal for a standing red fault whose
  /// episode never recovered (manual user reconnect after `gave_up`). Advisory
  /// faults are left to their TTL — reconnecting a switch doesn't retract a
  /// port read-back mismatch.
  bool _applyConnected(WsEvent event) {
    final deviceType = _string(event.payload['device_type']);
    if (deviceType == null) return false;
    final existing = _active[deviceType];
    if (existing == null || existing.level != StatusLevel.error) return false;
    _active.remove(deviceType);
    return true;
  }

  /// Drop advisory faults older than [advisoryTtl]. Returns the new snapshot
  /// when anything was dropped, else null. The notifier drives this on a
  /// periodic timer; `now` is a parameter so tests control the clock.
  ActiveFaultsSnapshot? prune(DateTime now) {
    final cutoff = now.subtract(advisoryTtl);
    final before = _active.length;
    _active.removeWhere((_, fault) =>
        fault.level != StatusLevel.error && fault.at.isBefore(cutoff));
    return _active.length == before ? null : snapshot;
  }

  static String? _string(dynamic value) =>
      value is String && value.isNotEmpty ? value : null;
}

/// Live standing-fault set for the active server, driven by the `equipment.*`
/// fault WS events. A fresh accumulator is built per active stream (a server
/// switch resets the set); when no server is saved the set is empty.
class ActiveFaultsNotifier extends Notifier<ActiveFaultsSnapshot> {
  /// Test seam — how often the advisory-TTL prune runs.
  static const pruneInterval = Duration(minutes: 1);

  @override
  ActiveFaultsSnapshot build() {
    final stream = ref.watch(wsEventStreamProvider);
    if (stream == null) {
      return const ActiveFaultsSnapshot();
    }
    final acc = ActiveFaultsAccumulator();
    // Safe to assign state from the listener: wsEventsProvider delivers
    // asynchronously (next microtask), never synchronously inside this build().
    ref.listen(wsEventsProvider, (prev, next) {
      final event = next.asData?.value;
      if (event == null || !event.type.startsWith('equipment.')) return;
      final folded = acc.apply(event);
      if (folded != null) state = folded;
    });
    // Advisory faults have no clear signal — age them out so a one-shot
    // mismatch from hours ago doesn't hold a chip amber all night.
    final timer = Timer.periodic(pruneInterval, (_) {
      final pruned = acc.prune(DateTime.now());
      if (pruned != null) state = pruned;
    });
    ref.onDispose(timer.cancel);
    return acc.snapshot;
  }
}

/// §42 standing faults for the active server. Intentionally **not**
/// autoDispose (mirrors `diagnosticsStateProvider`): the fault overlay must
/// keep folding while no fault-aware widget is mounted, so a chip lighting up
/// red is never contingent on which tab was open when the fault fired.
final activeFaultsProvider =
    NotifierProvider<ActiveFaultsNotifier, ActiveFaultsSnapshot>(
        ActiveFaultsNotifier.new);
