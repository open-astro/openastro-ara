import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/mount_status.dart';
import '../settings/equipment_connection_state.dart';
import '../../models/server.dart';
import '../../services/equipment_device_api.dart';
import '../saved_server_state.dart';
import 'equipment_device_state.dart';

/// Builds an [EquipmentDeviceClient] for the Mount (telescope) on a server.
/// Overridable in tests so a pure fake can be injected.
final mountApiFactoryProvider =
    Provider<EquipmentDeviceClient<MountStatus> Function(AraServer)>(
      (ref) =>
          (server) => EquipmentDeviceApi<MountStatus>(
            server,
            path: 'telescope',
            fromJson: MountStatus.fromJson,
          ),
    );

/// Mount client bound to the **active** server, or `null` when none is saved.
final mountApiProvider = Provider<EquipmentDeviceClient<MountStatus>?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(mountApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// Live mount status for the active server (or `null` when none is connected),
/// plus tracking / park / abort controls. Connect/disconnect + the
/// liveness/busy poll come from the generic core.
class MountNotifier extends EquipmentDeviceNotifier<MountStatus> {
  @override
  EquipmentDeviceType get deviceType => EquipmentDeviceType.mount;

  @override
  EquipmentDeviceClient<MountStatus>? watchClient() =>
      ref.watch(mountApiProvider);

  @override
  EquipmentDeviceClient<MountStatus>? readClient() =>
      ref.read(mountApiProvider);

  /// Start/stop sidereal tracking.
  Future<bool> setTracking(bool enabled) =>
      performAction((api) => api.command('tracking', {'enabled': enabled}));

  /// The drive rate, by its ASCOM name ("Sidereal", "Lunar", …), one of
  /// [MountCapabilities.trackingRates] (#1649). Tracking on/off is untouched.
  Future<bool> setTrackingRate(String rate) =>
      performAction((api) => api.command('trackingrate', {'rate': rate}));

  /// Park the mount (slew to the park position and stop tracking).
  Future<bool> park() => performAction((api) => api.command('park'));

  /// Release the mount from the parked state.
  Future<bool> unpark() => performAction((api) => api.command('unpark'));

  /// Slew the mount to its home position (homing switch). Gated on canFindHome.
  Future<bool> findHome() => performAction((api) => api.command('home'));

  /// GoTo: slew to the given RA (hours) / Dec (degrees), or sync the pointing
  /// model to them when [sync] is true. Gated on canSlew / canSync. While a
  /// sequence run is in progress the daemon refuses a slew (409
  /// `run_in_progress`, see [isRunInProgressRefusal]) until the caller has asked
  /// the user and repeats with [force].
  ///
  /// A GoTo turns tracking on first when it is off: ASCOM mounts refuse
  /// SlewToCoordinates without it ("requires Tracking to be true", an EQMOD
  /// mount at home on the rig, 2026-11-02), and the daemon's 202 hides that.
  Future<bool> slewTo(
    double raHours,
    double decDegrees, {
    bool sync = false,
    bool force = false,
  }) => performAction((api) async {
    final m = state.value;
    if (!sync &&
        m != null &&
        !m.tracking &&
        (m.capabilities?.canSetTracking ?? false)) {
      await api.command('tracking', {'enabled': true});
    }
    await api.command('slew', {
      'right_ascension_hours': raHours,
      'declination_degrees': decDegrees,
      'sync': sync,
      if (force) 'force': true,
    });
  }, pollAfter: true);

  /// Manual nudge on one axis (0 = primary/RA-Az, 1 = secondary/Dec-Alt) at [rate]
  /// deg/sec; rate 0 stops that axis. The direction pad calls this on press (rate)
  /// and release (0). Deliberately bypasses the [performAction] re-entrancy guard
  /// and runs the client directly so the STOP can never be dropped while a start is
  /// still in flight — a dropped stop would leave the mount running. [abortSlew] is
  /// the backstop. Returns true if dispatched (false only when no active server).
  ///
  /// #1649 — with [leaseMs] the rig stops the axis by itself unless the move is
  /// sent again within it (Simple Mode's pad renews while held); without, the
  /// move runs until a stop (the full UI's pad).
  Future<bool> moveAxis({
    required int axis,
    required double rate,
    int? leaseMs,
  }) async {
    final api = readClient();
    if (api == null) return false;
    await api.command('moveaxis', {
      'axis': axis,
      'rate': rate,
      'lease_ms': ?leaseMs,
    });
    return true;
  }

  /// Abort an in-progress slew (§57 panic stop) — also halts a manual axis
  /// move. Bypasses the [performAction] re-entrancy guard exactly like
  /// [moveAxis] (#837 r3): a STOP must never be silently dropped because
  /// another mount command (or its post-202 refresh) is still in flight.
  /// Returns true if dispatched (false only when no active server); a
  /// transport failure THROWS so the caller can surface it honestly.
  Future<bool> abortSlew() async {
    final api = readClient();
    if (api == null) return false;
    await api.command('abort');
    return true;
  }
}

final mountProvider = AsyncNotifierProvider<MountNotifier, MountStatus?>(
  MountNotifier.new,
);
