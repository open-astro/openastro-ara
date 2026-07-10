import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/fault_row.dart';
import '../../models/server.dart';
import '../../models/ws_event.dart';
import '../../services/faults_api.dart';
import '../../widgets/status_indicator.dart';
import '../saved_server_state.dart';
import '../ws/ws_providers.dart';

/// Builds a [FaultsClient] for a server. Overridable in tests.
final faultsApiFactoryProvider =
    Provider<FaultsClient Function(AraServer)>((ref) => FaultsApi.new);

/// [FaultsClient] bound to the active server, or null when none is saved.
final faultsApiProvider = Provider.autoDispose<FaultsClient?>((ref) {
  final server =
      ref.watch(savedServersProvider.select((async) => async.maybeWhen(
            data: (list) => list.isEmpty ? null : list.last,
            orElse: () => null,
          )));
  if (server == null) return null;
  final api = ref.watch(faultsApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

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
    this.staleRedGuard = const Duration(hours: 24),
    this.maxDevices = 32,
  });

  /// How long a one-shot advisory fault (no clear signal exists for it) holds
  /// its chip amber before the periodic prune drops it.
  final Duration advisoryTtl;

  /// [seed] skips unresolved recovery-tracked history rows older than this:
  /// a `gave_up` fault the user fixed by hand stays unresolved in the server's
  /// fault log (no `recovered` ever fires), and seeding it days later would
  /// permanently redden a healthy chip with no clear signal in sight.
  final Duration staleRedGuard;

  /// Defence against a misbehaving server flooding fabricated device_type
  /// tokens — the real vocabulary is the 12 `DeviceType` values.
  final int maxDevices;

  final Map<String, ActiveFault> _active = <String, ActiveFault>{};
  // Device types a LIVE signal (recovered / reconnect) has cleared during this
  // accumulator's life. [seed] skips them: a seed response that was in flight
  // when the clear arrived must not resurrect the fault from stale history
  // (the server resolves the row on `recovered`, but the read raced it).
  final Set<String> _liveCleared = <String>{};

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
    if (deviceType == null) return false;
    _liveCleared.add(deviceType);
    return _active.remove(deviceType) != null;
  }

  /// A fresh successful connect is the heal for a standing red fault whose
  /// episode never recovered (manual user reconnect after `gave_up`). Advisory
  /// faults are left to their TTL — reconnecting a switch doesn't retract a
  /// port read-back mismatch.
  bool _applyConnected(WsEvent event) {
    final deviceType = _string(event.payload['device_type']);
    if (deviceType == null) return false;
    _liveCleared.add(deviceType);
    final existing = _active[deviceType];
    if (existing == null || existing.level != StatusLevel.error) return false;
    _active.remove(deviceType);
    return true;
  }

  /// Fold unresolved history rows (`GET /api/v1/faults?unresolvedOnly=true`)
  /// into the standing set — the launch/reconnect resync the transition-edge
  /// WS events can't provide (each fault broadcasts exactly once, so a fault
  /// that fired while WILMA was closed is otherwise invisible until the next
  /// transition). Live state always wins: a seeded row never downgrades or
  /// replaces an equal-or-worse entry that live events already produced.
  /// Advisory rows older than [advisoryTtl] and recovery-tracked rows older
  /// than [staleRedGuard] are skipped. Returns the new snapshot, or null when
  /// nothing folded.
  ActiveFaultsSnapshot? seed(Iterable<FaultRow> rows, DateTime now) {
    var changed = false;
    for (final row in rows) {
      if (row.resolved) continue; // defence — the caller queries unresolvedOnly
      if (_liveCleared.contains(row.equipmentType)) continue;
      final level = faultKindLevel(row.faultType);
      final age = now.difference(row.detectedUtc);
      if (age > (level == StatusLevel.error ? staleRedGuard : advisoryTtl)) {
        continue;
      }
      final existing = _active[row.equipmentType];
      if (existing != null &&
          statusLevelRank(existing.level) >= statusLevelRank(level)) {
        continue;
      }
      if (existing == null && _active.length >= maxDevices) continue;
      _active[row.equipmentType] = ActiveFault(
        deviceType: row.equipmentType,
        deviceName: row.equipmentName,
        kind: row.faultType,
        details: row.details,
        at: row.detectedUtc,
        level: level,
      );
      changed = true;
    }
    return changed ? snapshot : null;
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

  // The accumulator owned by the CURRENT build — an async seed that resolves
  // after a rebuild (server switch) must not write a stale server's faults
  // into the fresh state.
  ActiveFaultsAccumulator? _acc;
  int _seedGen = 0;

  @override
  ActiveFaultsSnapshot build() {
    final stream = ref.watch(wsEventStreamProvider);
    if (stream == null) {
      _acc = null;
      return const ActiveFaultsSnapshot();
    }
    final acc = ActiveFaultsAccumulator();
    _acc = acc;
    // Safe to assign state from the listener: wsEventsProvider delivers
    // asynchronously (next microtask), never synchronously inside this build().
    ref.listen(wsEventsProvider, (prev, next) {
      final event = next.asData?.value;
      if (event == null || !event.type.startsWith('equipment.')) return;
      final folded = acc.apply(event);
      if (folded != null) state = folded;
    });
    // The fault WS events are transition-edge one-shots — a fault that fired
    // while WILMA was closed (or while the socket was down past the resume
    // window) never re-broadcasts. Seed the standing set from the §42.5 fault
    // log now, and re-seed whenever the server link comes back.
    final api = ref.watch(faultsApiProvider);
    _seed(acc, api);
    ref.listen(serverLinkUpProvider, (prev, next) {
      if (prev == false && next == true) _seed(acc, api);
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

  Future<void> _seed(ActiveFaultsAccumulator acc, FaultsClient? api) async {
    if (api == null) return;
    final gen = ++_seedGen;
    try {
      final page = await api.list(unresolvedOnly: true);
      // Superseded by a newer seed or a rebuild (server switch) — drop it.
      if (gen != _seedGen || !identical(acc, _acc)) return;
      final folded = acc.seed(page.items, DateTime.now());
      if (folded != null) state = folded;
    } on Exception {
      // Best-effort: live events still fold, and the next link-up re-seeds.
      // A failed REST read must never break the WS-driven overlay.
    }
  }
}

/// §42 standing faults for the active server. Intentionally **not**
/// autoDispose (mirrors `diagnosticsStateProvider`): the fault overlay must
/// keep folding while no fault-aware widget is mounted, so a chip lighting up
/// red is never contingent on which tab was open when the fault fired.
final activeFaultsProvider =
    NotifierProvider<ActiveFaultsNotifier, ActiveFaultsSnapshot>(
        ActiveFaultsNotifier.new);
