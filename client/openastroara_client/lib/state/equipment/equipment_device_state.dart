import 'dart:async';

import 'package:flutter/foundation.dart';
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

  /// Cadence of the background liveness poll that keeps a CONNECTED device's
  /// reading current (its state — e.g. a SafetyMonitor's is_safe, weather sensors —
  /// changes autonomously on the device, not just at connect). Slow by design; the
  /// daemon caches device reads, and §60.9 equipment WS events will eventually
  /// replace this poll with push.
  static const Duration livePollInterval = Duration(seconds: 15);

  // Fast, capped poll while a device is mid-connect (drives it to settlement).
  Timer? _settleTimer;
  int _settleTicks = 0;
  // Slow, uncapped poll while a device is connected (keeps the live reading fresh).
  Timer? _liveTimer;
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
    _cancelAllPolls();
    // Tear down both timers when the notifier is disposed OR rebuilt. Riverpod
    // invokes and CLEARS onDispose callbacks on each rebuild before re-running
    // build(), so registering per-build cleans up the prior build's timers without
    // the callback list accumulating across server switches.
    ref.onDispose(_cancelAllPolls);
    // Scope the background liveness poll to when a panel is actually watching:
    // stop it when the last listener leaves (no point polling a device nobody is
    // viewing — overnight that'd be thousands of idle GETs), and on a listener's
    // return do an immediate fresh read which re-arms the poll if still connected.
    // Only the LIVE poll is paused on listener-loss; the settle-poll is left to run
    // (it's brief + capped, and under the default no-keepAlive lifecycle a fully
    // listener-less notifier is disposed — onDispose → _cancelAllPolls tears down
    // both timers). If this provider ever gains keepAlive, also cancel settle here.
    ref.onCancel(_cancelLive);
    ref.onResume(() {
      if (ref.mounted) refresh();
    });
    final api = watchClient();
    if (api == null) return null;
    final status = await api.getStatus();
    // The notifier may have been disposed during the await — don't arm a (briefly)
    // stale timer.
    if (ref.mounted) _syncPolls(status);
    return status;
  }

  /// Connect the chosen device. Returns whether the call was PERFORMED — `false`
  /// means it was dropped (another action in flight, or no active server).
  Future<bool> connect(DiscoveredDevice device) =>
      _act((api) => api.connect(device), pollAfter: true);

  /// Disconnect the device. Returns whether the call was performed (see [connect]).
  Future<bool> disconnect() => _act((api) => api.disconnect());

  /// Run a device-specific control action (e.g. a focuser move, a filter change)
  /// through the same 202-accept + re-read + re-entrancy machinery as
  /// connect/disconnect. Subclasses expose typed wrappers over this. Returns
  /// whether it was performed (`false` if dropped by the re-entrancy guard).
  @protected
  Future<bool> performAction(
          Future<void> Function(EquipmentDeviceClient<T> api) action,
          {bool pollAfter = false}) =>
      _act(action, pollAfter: pollAfter);

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
      // Drop a manual (Retry / settle-poll) read while another read is in flight —
      // either a prior manual one (_refreshing) OR an action's pinned post-202
      // re-read (_acting, which uses the manual=false path and doesn't set
      // _refreshing). Avoids two concurrent getStatus()es where the slower, staler
      // response could land last within the same generation.
      if (_refreshing || _acting) return;
      _refreshing = true;
    }
    final gen = _generation;
    try {
      final api = client ?? readClient();
      // AsyncValue.guard captures the read into data/error rather than throwing;
      // `state` isn't reassigned until it resolves, so the prior status stays
      // visible while the read is in flight (no blank flash).
      final next = await AsyncValue.guard<T?>(() async {
        if (api == null) return null;
        return api.getStatus();
      });
      if (ref.mounted && gen == _generation) {
        state = next;
        // Re-evaluate the polls only on a SUCCESSFUL read. A transient read error
        // (mid-connect or mid-liveness) must NOT cancel the timer — it should keep
        // retrying — so leave whatever is running in place on an error.
        if (next case AsyncData(:final value)) _syncPolls(value);
      }
    } finally {
      if (manual && gen == _generation) _refreshing = false;
    }
  }

  // Pick the right poll for the current state: the fast settle-poll while
  // connecting, the slow liveness-poll while connected, neither otherwise. On a
  // refresh that fails to read, this isn't called (see refresh()) so a transient
  // error leaves the running timer in place rather than stranding the device.
  void _syncPolls(T? status) {
    if (status == null) {
      _cancelAllPolls();
      return;
    }
    // Fast-poll (settle cadence) while connecting OR while connected-but-busy (a
    // focuser moving, a dome slewing) so the panel tracks the device to rest; the
    // slow liveness-poll otherwise; nothing when disconnected/errored.
    if (status.isConnecting || (status.isConnected && status.isBusy)) {
      _cancelLive();
      _armSettle();
    } else if (status.isConnected) {
      _cancelSettle();
      _armLive();
    } else {
      _cancelAllPolls();
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

  // Start the liveness-poll if it isn't already running. Uncapped — it runs as
  // long as the device stays connected, keeping the reading current.
  void _armLive() {
    if (_liveTimer != null) return;
    _liveTimer = Timer.periodic(livePollInterval, (_) {
      if (!ref.mounted) {
        _cancelLive();
        return;
      }
      if (_refreshing || _acting) return; // a read is already in flight
      refresh();
    });
  }

  void _cancelSettle() {
    _settleTimer?.cancel();
    _settleTimer = null;
    _settleTicks = 0;
  }

  void _cancelLive() {
    _liveTimer?.cancel();
    _liveTimer = null;
  }

  void _cancelAllPolls() {
    _cancelSettle();
    _cancelLive();
  }
}
