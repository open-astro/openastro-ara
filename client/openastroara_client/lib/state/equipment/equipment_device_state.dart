import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/discovered_device.dart';
import '../../models/equipment_device_status.dart';
import '../../services/equipment_device_api.dart';

/// Shared connect / disconnect / refresh / settle engine for the single-instance
/// equipment devices (everything except the multi-instance Switch). A subclass
/// binds it to one device type by supplying its API-client provider via
/// [watchClient] (build-time, subscribes) and [readClient] (action-time, doesn't).
///
/// State is the device's live status, or `null` when none is connected (the
/// daemon's status GET 404s). Connect/disconnect are 202-Accepted + background, so
/// the post-action read shows `connecting`; the engine then polls every
/// [settleInterval] until the device settles (connected / error / disconnected),
/// so a card reaches its real state without the user re-opening the panel. This is
/// the device-agnostic equivalent of the Switch list's poll, lifted to one place.
abstract class EquipmentDeviceNotifier<T extends EquipmentDeviceStatus>
    extends AsyncNotifier<T?> {
  /// Cadence of the mid-connect settle-poll. Matches the Switch panel's poll.
  static const Duration settleInterval = Duration(milliseconds: 1500);

  /// Upper bound on settle-poll ticks (~60 s at [settleInterval]). A device
  /// wedged in `connecting` — or a daemon that never settles it — must not keep
  /// the timer (and its network reads) alive for the whole session. After the cap
  /// the poll stops; the card holds its last state and the user can refresh.
  static const int maxSettlePolls = 40;

  Timer? _settleTimer;
  int _settleTicks = 0;
  // True while a manual (timer/user) refresh is in flight — a second is dropped so
  // a slow getStatus can't stack overlapping reads. Action-driven refreshes pass a
  // pinned client and bypass this guard (they must always re-read after the 202).
  bool _refreshing = false;
  // True while a connect/disconnect is in flight (re-entrancy guard).
  bool _acting = false;
  // Bumped on every build() (active-server change). A refresh captures the gen at
  // its start and only writes if it still matches, so an in-flight read can't land
  // stale data over a new server's result.
  int _generation = 0;

  /// The API client bound to the **active** server, read with `ref.watch` so a
  /// server change rebuilds the notifier. Returns `null` when no server is active.
  EquipmentDeviceClient<T>? watchClient();

  /// The same client, read with `ref.read` (for actions / refresh — must not
  /// subscribe, or an action would re-trigger build()).
  EquipmentDeviceClient<T>? readClient();

  @override
  Future<T?> build() async {
    // Reset the in-flight guards on a server change: a stale guard from the old
    // server would otherwise lock out actions on the new one.
    _refreshing = false;
    _acting = false;
    _generation++;
    _cancelSettle();
    ref.onDispose(_cancelSettle);
    final api = watchClient();
    if (api == null) return null;
    final status = await api.getStatus();
    _syncSettle(status);
    return status;
  }

  /// Connect the chosen device. Returns whether the call was PERFORMED — `false`
  /// means it was dropped (another action in flight, or no active server).
  Future<bool> connect(DiscoveredDevice device) =>
      _act((api) => api.connect(device), pollAfter: true);

  /// Disconnect the device. Returns whether the call was performed (see [connect]).
  Future<bool> disconnect() => _act((api) => api.disconnect());

  // Run a 202-Accepted action then re-read against the SAME client (a mid-action
  // server switch must not redirect the follow-up read). A failed action is
  // RETHROWN to the caller (the UI surfaces it) rather than wiping the last-read
  // status. Returns true if performed, false if dropped (re-entrancy / no API).
  // [pollAfter] arms the settle-poll on success (connect expects a
  // connecting→connected transition) BEFORE the re-read, so a transient failure of
  // that first re-read can't leave the device un-polled — the poll reads it to
  // settlement on its own.
  Future<bool> _act(
      Future<void> Function(EquipmentDeviceClient<T> api) action,
      {bool pollAfter = false}) async {
    if (_acting) return false;
    final api = readClient();
    if (api == null) return false;
    _acting = true;
    final gen = _generation;
    try {
      await action(api);
      if (ref.mounted) {
        if (pollAfter) _armSettle();
        await refresh(api);
      }
      return true;
    } finally {
      // Clear the guard only if no server change happened mid-action — build()
      // already reset it for the new generation, and a fresh action there may have
      // set it true again; an unconditional clear would wipe that newer guard.
      if (gen == _generation) _acting = false;
    }
  }

  /// Re-read the device status. [client] pins the read to a specific client (used
  /// by the actions so a mid-action server switch can't redirect it); when omitted
  /// it reads the current active client and is serialized by [_refreshing].
  Future<void> refresh([EquipmentDeviceClient<T>? client]) async {
    if (!ref.mounted) return;
    final manual = client == null;
    if (manual) {
      if (_refreshing) return;
      _refreshing = true;
    }
    final gen = _generation;
    try {
      final api = client ?? readClient();
      // Keep the prior status visible while reloading (don't flash blank).
      final next = await AsyncValue.guard<T?>(() async {
        if (api == null) return null;
        return api.getStatus();
      });
      if (ref.mounted && gen == _generation) {
        state = next;
        // Re-evaluate the settle-poll only on a SUCCESSFUL read. A transient read
        // error mid-connect must NOT cancel the poll (it should keep retrying until
        // the device actually settles), so leave the timer running on error.
        if (next case AsyncData(:final value)) _syncSettle(value);
      }
    } finally {
      if (manual && gen == _generation) _refreshing = false;
    }
  }

  // Arm the settle-poll while the device is mid-connect; cancel it once the device
  // settles (or disconnects). On a refresh that fails to read, the poll is left
  // running (see refresh()) so a transient error doesn't strand a connecting device.
  void _syncSettle(T? status) {
    if (status?.isConnecting ?? false) {
      _armSettle();
    } else {
      _cancelSettle();
    }
  }

  // Start the bounded settle-poll if it isn't already running. Idempotent — a
  // no-op when a timer is already polling, so repeated arms keep the SAME timer +
  // tick budget rather than restarting (or stacking) it.
  void _armSettle() {
    if (_settleTimer != null) return;
    _settleTicks = 0;
    _settleTimer = Timer.periodic(settleInterval, (_) {
      if (!ref.mounted) {
        _cancelSettle();
        return;
      }
      // Skip WITHOUT spending a tick when a read is already in flight — either a
      // manual refresh (user hit Retry) or an action's post-202 re-read (_acting).
      // This keeps the budget honest AND avoids a second concurrent GET racing the
      // re-read that _armSettle() was started alongside.
      if (_refreshing || _acting) return;
      // Bound the poll so a device stuck in `connecting` can't keep firing reads
      // for the whole session (e.g. the network dropped after the connect 202).
      if (_settleTicks >= maxSettlePolls) {
        _cancelSettle();
        return;
      }
      _settleTicks++;
      refresh();
    });
  }

  void _cancelSettle() {
    _settleTimer?.cancel();
    _settleTimer = null;
    _settleTicks = 0;
  }
}
