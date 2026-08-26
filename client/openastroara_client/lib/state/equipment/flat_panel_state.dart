import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/flat_panel_status.dart';
import '../settings/equipment_connection_state.dart';
import '../../models/server.dart';
import '../../services/equipment_device_api.dart';
import '../saved_server_state.dart';
import 'equipment_device_state.dart';

/// Builds an [EquipmentDeviceClient] for the flat (cover/calibrator) panel on a
/// server. Overridable in tests so a pure fake can be injected.
final flatPanelApiFactoryProvider =
    Provider<EquipmentDeviceClient<FlatPanelStatus> Function(AraServer)>(
      (ref) =>
          (server) => EquipmentDeviceApi<FlatPanelStatus>(
            server,
            path: 'flatdevice',
            fromJson: FlatPanelStatus.fromJson,
          ),
    );

/// Flat-panel client bound to the **active** server, or `null` when none is saved.
final flatPanelApiProvider = Provider<EquipmentDeviceClient<FlatPanelStatus>?>((
  ref,
) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(flatPanelApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// Live flat-panel status for the active server (or `null` when none is
/// connected), plus the cover/light [apply] control. Connect/disconnect + the
/// liveness/busy poll come from the generic core.
class FlatPanelNotifier extends EquipmentDeviceNotifier<FlatPanelStatus> {
  @override
  EquipmentDeviceType get deviceType => EquipmentDeviceType.flatPanel;

  @override
  EquipmentDeviceClient<FlatPanelStatus>? watchClient() =>
      ref.watch(flatPanelApiProvider);

  @override
  EquipmentDeviceClient<FlatPanelStatus>? readClient() =>
      ref.read(flatPanelApiProvider);

  /// Drive the cover and/or the calibrator light (`POST .../flatdevice/apply`).
  /// Every field is optional and only the ones passed are changed, matching the
  /// daemon's `FlatPanelRequestDto`. 202-accepted; the re-read reflects the new
  /// state (the cover takes seconds to move, so it lands via the poll).
  ///
  /// [brightness] alone re-levels the light (0 turns it off); [lightOn] `true`
  /// with a brightness turns on at that level.
  Future<bool> apply({bool? openCover, bool? lightOn, int? brightness}) async {
    final performed = await performAction(
      (api) => api.command('apply', {
        'open_cover': ?openCover,
        'light_on': ?lightOn,
        'brightness': ?brightness,
      }),
    );
    if (performed) {
      _lastApplyConfirmed =
          await _confirm(lightOn: lightOn, brightness: brightness);
    }
    return performed;
  }

  /// Whether the last [apply] was observed to take effect on the device. False
  /// when the confirm poll gave up — a jammed cover, or a panel that quietly
  /// refused. Read by the panel right after an apply so an unlanded command is
  /// reported instead of vanishing.
  bool get lastApplyConfirmed => _lastApplyConfirmed;
  bool _lastApplyConfirmed = true;

  /// Cadence and budget of the post-apply confirm poll (~4 s).
  @visibleForTesting
  static const Duration confirmInterval = Duration(milliseconds: 500);
  @visibleForTesting
  static const int maxConfirmPolls = 8;

  /// Absolute ceiling on the confirm poll (~60 s), independent of the busy
  /// exclusion below. A jammed cover — or a driver bug that reports `Moving` /
  /// `NotReady` forever — must not leave the panel polling the daemon every
  /// 500 ms for the rest of the session; past this the ordinary liveness poll
  /// owns the device again. Sized above the daemon's ~40 s cover-settle budget so
  /// a legitimately slow cover still confirms here.
  @visibleForTesting
  static const int maxConfirmPollsAbsolute = 120;

  /// Re-read until the device reflects the light change we just commanded.
  ///
  /// The apply is 202 + background: the daemon runs the blocking ASCOM
  /// CalibratorOn/Off on its own thread, so the re-read that `performAction` does
  /// the instant the 202 lands still reads the OLD state. Without this the panel
  /// then sits on the 15 s liveness poll — a light switch that takes up to fifteen
  /// seconds to visibly move. A moving cover is covered already (`isBusy` drives
  /// the 1.5 s settle-poll); the light has no busy sub-state, so it needs this.
  ///
  /// Gives up after [maxConfirmPolls] rather than polling forever — a device that
  /// refuses the command must not spin the panel; the liveness poll owns it again.
  /// Returns true when the device confirmed the change, false when it never did
  /// (budget or ceiling exhausted) — the caller reports that rather than leaving
  /// a command that silently never landed.
  Future<bool> _confirm({bool? lightOn, int? brightness}) async {
    // Only the light needs it, and only when the request actually pins an
    // expected end state. `brightness: 0` means "off"; any positive level means
    // "on at that level".
    final wantOn = lightOn ?? (brightness == null ? null : brightness > 0);
    if (wantOn == null) return true;
    var ticks = 0;
    var polls = 0;
    while (ticks < maxConfirmPolls && polls < maxConfirmPollsAbsolute) {
      polls++;
      await Future<void>.delayed(confirmInterval);
      if (!ref.mounted) return true;
      await refresh();
      final s = state.value;
      if (s == null) return true;
      if (s.lightOn == wantOn &&
          (brightness == null || !wantOn || s.brightness == brightness)) {
        return true; // the device is where we asked it to be
      }
      // A cover mid-travel doesn't spend budget: panels refuse a calibrator change
      // while the cover moves, so the daemon holds the light command until the
      // cover settles (up to ~40s). Spending the 4s budget here would abandon a
      // command that IS still coming, snapping the switch back for no reason.
      // A warming light (EL panels ramping to the commanded level) is likewise
      // still in progress, not a failure — don't spend budget on it either.
      if (!s.isMoving && !s.lightWarming) ticks++;
    }
    return false;
  }
}

final flatPanelProvider =
    AsyncNotifierProvider<FlatPanelNotifier, FlatPanelStatus?>(
      FlatPanelNotifier.new,
    );
