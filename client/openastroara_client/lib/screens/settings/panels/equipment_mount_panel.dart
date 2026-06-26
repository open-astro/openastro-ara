import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/equipment_device_status.dart';
import '../../../models/mount_status.dart';
import '../../../services/equipment_device_api.dart';
import '../../../state/equipment/mount_state.dart';
import '../../../state/settings/equipment_connection_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/equipment/equipment_connection_card.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.5 Mount panel. Shows the connected mount's live RA/Dec + tracking/park
/// state with tracking, park/unpark, and abort-slew controls (each gated on the
/// device's capabilities) via the shared connection card.
class EquipmentMountPanel extends ConsumerWidget {
  const EquipmentMountPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connection = ref.watch(equipmentConnectionProvider);
    final n = ref.read(equipmentConnectionProvider.notifier);
    final status = ref.watch(mountProvider);
    final notifier = ref.read(mountProvider.notifier);
    // The live mount when connected — gates the manual-control section (GoTo + the
    // direction pad), which only makes sense for a connected, capable mount.
    final mount = status.maybeWhen(
      data: (s) => (s != null && s.isConnected) ? s : null,
      orElse: () => null,
    );

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Connection'),
        EquipmentConnectionCard<MountStatus>(
          status: status,
          deviceType: EquipmentDeviceType.mount,
          deviceTypeLabel: 'mount',
          emptyLabel: 'No mount connected.',
          onConnect: notifier.connect,
          onDisconnect: notifier.disconnect,
          onReconnect: notifier.reconnect,
          onRetry: notifier.refresh,
          connectedBody: (context, s) => _MountBody(status: s),
        ),
        SettingsSwitchRow(
          label: 'Auto-connect on boot',
          helpKey: 'eq.auto_connect_on_boot',
          value: connection.autoConnect(EquipmentDeviceType.mount),
          onChanged: (v) => n.setAutoConnect(EquipmentDeviceType.mount, v),
        ),
        if (mount != null &&
            ((mount.capabilities?.canSlew ?? false) ||
                (mount.capabilities?.canMoveAxis ?? false))) ...[
          const SettingsSectionHeader('Manual control'),
          _ManualControl(status: mount),
        ],
      ],
    );
  }
}

/// The connected mount's live body: RA/Dec + tracking/park/home state, with
/// tracking, park/unpark, and abort-slew controls gated on capabilities.
class _MountBody extends ConsumerWidget {
  final MountStatus status;
  const _MountBody({required this.status});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = status;
    if (s.isConnecting) return const Text('Reading…');
    if (s.connectionState == EquipmentConnectionState.error) {
      return const Row(children: [
        Icon(Icons.error_outline, color: AraColors.accentError, size: 20),
        SizedBox(width: 8),
        Expanded(child: Text('Mount read failed — check the device.')),
      ]);
    }
    final caps = s.capabilities;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _row('Right ascension', formatRaHours(s.rightAscensionHours)),
        _row('Declination', formatDecDegrees(s.declinationDegrees)),
        _row('Parked', s.parked ? 'Yes' : 'No'),
        _row('At home', s.atHome ? 'Yes' : 'No'),
        if (s.isBusy)
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 4),
            child: Text(s.runtimeState == 'unparking' ? 'Unparking…' : 'Slewing…',
                style: const TextStyle(color: AraColors.accentBusy)),
          ),
        if (caps?.canSetTracking ?? false)
          Row(children: [
            const Expanded(child: Text('Tracking')),
            Switch(
              key: const Key('mount_tracking_switch'),
              value: s.tracking,
              onChanged: s.parked
                  ? null
                  : (v) => _run(context, ref, 'set tracking',
                      () => ref.read(mountProvider.notifier).setTracking(v)),
            ),
          ]),
        const SizedBox(height: 8),
        Wrap(spacing: 8, runSpacing: 8, children: [
          if ((caps?.canPark ?? false) && !s.parked)
            OutlinedButton(
              onPressed: s.isBusy
                  ? null
                  : () => _run(context, ref, 'park',
                      () => ref.read(mountProvider.notifier).park()),
              child: const Text('Park'),
            ),
          if ((caps?.canUnpark ?? false) && s.parked)
            OutlinedButton(
              onPressed: s.isBusy
                  ? null
                  : () => _run(context, ref, 'unpark',
                      () => ref.read(mountProvider.notifier).unpark()),
              child: const Text('Unpark'),
            ),
          // Home slews to the mount's homing switch — disabled while parked (must
          // unpark first) or busy. Shown only when the mount supports FindHome.
          if (caps?.canFindHome ?? false)
            OutlinedButton(
              onPressed: (s.isBusy || s.parked)
                  ? null
                  : () => _run(context, ref, 'home',
                      () => ref.read(mountProvider.notifier).findHome()),
              child: const Text('Home'),
            ),
          OutlinedButton(
            onPressed: () => _run(context, ref, 'stop',
                () => ref.read(mountProvider.notifier).abortSlew()),
            child: const Text('Stop'),
          ),
        ]),
      ],
    );
  }

  Widget _row(String label, String value) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 2),
        child: Row(children: [Expanded(child: Text(label)), Text(value)]),
      );

  Future<void> _run(BuildContext context, WidgetRef ref, String verb,
      Future<bool> Function() action) async {
    final messenger = ScaffoldMessenger.of(context);
    try {
      final performed = await action();
      if (!performed) {
        messenger.showSnackBar(const SnackBar(
          content: Text('Another action is still in progress.'),
        ));
      }
    } catch (e) {
      messenger.showSnackBar(SnackBar(
        content: Text("Couldn't $verb: ${describeEquipmentError(e)}"),
        backgroundColor: AraColors.accentError,
      ));
    }
  }
}

/// §37.5 manual control: a GoTo (slew to RA/Dec) plus a press-and-hold 8-way
/// direction pad with a speed picker. Primary axis = RA/Az (E/W), secondary =
/// Dec/Alt (N/S); corners drive both. Each part is gated on the mount's caps.
class _ManualControl extends ConsumerStatefulWidget {
  final MountStatus status;
  const _ManualControl({required this.status});

  @override
  ConsumerState<_ManualControl> createState() => _ManualControlState();
}

class _ManualControlState extends ConsumerState<_ManualControl> {
  final _ra = TextEditingController();
  final _dec = TextEditingController();
  double? _rate;

  static const int _primary = 0; // RA / Azimuth (E/W)
  static const int _secondary = 1; // Dec / Altitude (N/S)

  @override
  void initState() {
    super.initState();
    _rate = _defaultRate(widget.status.capabilities?.axisRatesDegPerSec ?? const []);
  }

  @override
  void didUpdateWidget(_ManualControl old) {
    super.didUpdateWidget(old);
    // If the panel rebuilds for a mount with a different rate set (e.g. disconnect
    // then reconnect to different hardware), re-seed _rate — otherwise it holds the
    // old mount's value: no chip shows selected and a nudge sends a stale rate the
    // new driver may reject or clamp with no feedback.
    final oldRates = old.status.capabilities?.axisRatesDegPerSec ?? const <double>[];
    final newRates = widget.status.capabilities?.axisRatesDegPerSec ?? const <double>[];
    if (!_sameRates(oldRates, newRates)) {
      _rate = _defaultRate(newRates);
    }
  }

  // Default to a middle rate — a usable nudge without lurching at full speed.
  static double? _defaultRate(List<double> rates) =>
      rates.isEmpty ? null : rates[(rates.length - 1) ~/ 2];

  static bool _sameRates(List<double> a, List<double> b) {
    if (a.length != b.length) return false;
    for (var i = 0; i < a.length; i++) {
      if (a[i] != b[i]) return false;
    }
    return true;
  }

  @override
  void dispose() {
    _ra.dispose();
    _dec.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final caps = widget.status.capabilities;
    final busy = widget.status.isBusy;
    final rates = caps?.axisRatesDegPerSec ?? const <double>[];
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        if (caps?.canSlew ?? false) _goTo(busy),
        if (caps?.canMoveAxis ?? false) ...[
          const SizedBox(height: 16),
          if (rates.isNotEmpty) _speedPicker(rates) else
            const Text('This mount reports no manual slew rates.',
                style: TextStyle(color: AraColors.textSecondary)),
          const SizedBox(height: 10),
          _directionPad(disabled: busy || _rate == null),
          const Padding(
            padding: EdgeInsets.only(top: 6),
            child: Text('Hold a direction to move; release to stop. Centre stops all motion.',
                style: TextStyle(color: AraColors.textSecondary, fontSize: 12)),
          ),
        ],
      ],
    );
  }

  Widget _goTo(bool busy) {
    return Row(children: [
      SizedBox(
        width: 110,
        child: TextField(
          controller: _ra,
          keyboardType: const TextInputType.numberWithOptions(decimal: true),
          decoration: const InputDecoration(isDense: true, labelText: 'RA (h)'),
        ),
      ),
      const SizedBox(width: 8),
      SizedBox(
        width: 110,
        child: TextField(
          controller: _dec,
          keyboardType:
              const TextInputType.numberWithOptions(signed: true, decimal: true),
          decoration: const InputDecoration(isDense: true, labelText: 'Dec (°)'),
        ),
      ),
      const SizedBox(width: 12),
      FilledButton(
        onPressed: busy ? null : _dispatchGoTo,
        child: const Text('GoTo'),
      ),
    ]);
  }

  Future<void> _dispatchGoTo() async {
    final messenger = ScaffoldMessenger.of(context);
    final ra = double.tryParse(_ra.text.trim());
    final dec = double.tryParse(_dec.text.trim());
    if (ra == null || dec == null || ra < 0 || ra >= 24 || dec < -90 || dec > 90) {
      messenger.showSnackBar(const SnackBar(
          content: Text('Enter RA 0–24 h and Dec −90 to 90°.')));
      return;
    }
    try {
      final performed = await ref.read(mountProvider.notifier).slewTo(ra, dec);
      if (!performed) {
        messenger.showSnackBar(const SnackBar(
            content: Text('Another action is still in progress.')));
      }
    } catch (e) {
      messenger.showSnackBar(SnackBar(
        content: Text("Couldn't slew: ${describeEquipmentError(e)}"),
        backgroundColor: AraColors.accentError,
      ));
    }
  }

  Widget _speedPicker(List<double> rates) {
    return Wrap(
      spacing: 8,
      runSpacing: 4,
      crossAxisAlignment: WrapCrossAlignment.center,
      children: [
        const Text('Speed'),
        for (final r in rates)
          ChoiceChip(
            label: Text(_fmtRate(r)),
            selected: _rate == r,
            onSelected: (_) => setState(() => _rate = r),
          ),
      ],
    );
  }

  Widget _directionPad({required bool disabled}) {
    Widget pad(IconData icon, List<(int, double)> moves) => _HoldButton(
          icon: icon,
          enabled: !disabled,
          onStart: () => _start(moves),
          onStop: () => _stop(moves.map((m) => m.$1).toSet()),
        );
    return Column(mainAxisSize: MainAxisSize.min, children: [
      Row(mainAxisSize: MainAxisSize.min, children: [
        pad(Icons.north_west, [(_secondary, 1), (_primary, -1)]),
        pad(Icons.north, [(_secondary, 1)]),
        pad(Icons.north_east, [(_secondary, 1), (_primary, 1)]),
      ]),
      Row(mainAxisSize: MainAxisSize.min, children: [
        pad(Icons.west, [(_primary, -1)]),
        _stopButton(),
        pad(Icons.east, [(_primary, 1)]),
      ]),
      Row(mainAxisSize: MainAxisSize.min, children: [
        pad(Icons.south_west, [(_secondary, -1), (_primary, -1)]),
        pad(Icons.south, [(_secondary, -1)]),
        pad(Icons.south_east, [(_secondary, -1), (_primary, 1)]),
      ]),
    ]);
  }

  Widget _stopButton() => Padding(
        padding: const EdgeInsets.all(4),
        child: SizedBox(
          width: 52,
          height: 52,
          child: OutlinedButton(
            onPressed: () => ref.read(mountProvider.notifier).abortSlew(),
            style: OutlinedButton.styleFrom(
                padding: EdgeInsets.zero, shape: const CircleBorder()),
            child: const Icon(Icons.stop, color: AraColors.accentError),
          ),
        ),
      );

  void _start(List<(int, double)> moves) {
    final rate = _rate;
    if (rate == null) return;
    for (final (axis, sign) in moves) {
      _dispatchAxis(axis, sign * rate);
    }
  }

  void _stop(Set<int> axes) {
    for (final axis in axes) {
      _dispatchAxis(axis, 0);
    }
  }

  // Fire-and-forget one MoveAxis. The whole point is to not await (a release must
  // not block the UI), so the returned Future's errors are caught here — both the
  // synchronous `ref.read` (which can throw if the provider is deactivating during
  // teardown) and the async HTTP send (e.g. a momentary network drop on release).
  // A genuinely lost stop is covered by the deadman: the centre Stop / AbortSlew
  // halts all axes. (A server-side MoveAxis watchdog that auto-stops on a lost
  // heartbeat is a possible future hardening — tracked separately.)
  void _dispatchAxis(int axis, double rate) {
    try {
      ref
          .read(mountProvider.notifier)
          .moveAxis(axis: axis, rate: rate)
          .catchError((_) => false);
    } catch (_) {
      // ref.read threw during teardown — nothing to do; Stop/AbortSlew is the backstop.
    }
  }

  static String _fmtRate(double r) => r >= 1
      ? '${r.toStringAsFixed(r == r.roundToDouble() ? 0 : 1)}°/s'
      : '${r.toStringAsFixed(3)}°/s';
}

/// A press-and-hold button: fires [onStart] on pointer-down and [onStop] on
/// pointer-up/cancel (and on dispose mid-hold), so a held direction moves the
/// mount and releasing — or the widget going away — always stops it.
class _HoldButton extends StatefulWidget {
  final IconData icon;
  final bool enabled;
  final VoidCallback onStart;
  final VoidCallback onStop;
  const _HoldButton({
    required this.icon,
    required this.enabled,
    required this.onStart,
    required this.onStop,
  });

  @override
  State<_HoldButton> createState() => _HoldButtonState();
}

class _HoldButtonState extends State<_HoldButton> {
  bool _held = false;

  void _down() {
    if (!widget.enabled || _held) return;
    setState(() => _held = true);
    widget.onStart();
  }

  void _up() {
    if (!_held) return;
    setState(() => _held = false);
    widget.onStop();
  }

  @override
  void dispose() {
    if (_held) widget.onStop(); // stop the axis if torn down mid-hold
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.all(4),
      child: Listener(
        onPointerDown: (_) => _down(),
        onPointerUp: (_) => _up(),
        onPointerCancel: (_) => _up(),
        child: Container(
          width: 52,
          height: 52,
          decoration: BoxDecoration(
            color: _held
                ? AraColors.accentBusy.withValues(alpha: 0.25)
                : AraColors.bgPanel,
            border: Border.all(color: AraColors.border),
            borderRadius: BorderRadius.circular(8),
          ),
          child: Icon(widget.icon,
              color: widget.enabled ? null : AraColors.textDisabled),
        ),
      ),
    );
  }
}
