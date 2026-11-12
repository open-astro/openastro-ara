import 'package:flutter/cupertino.dart' show CupertinoSlidingSegmentedControl;
import 'package:flutter/material.dart';

import '../../models/switch_device.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../help_icon.dart';
import 'switch_port_layout.dart';

/// Writes [value] to [port]. Resolves **true** only when the write actually
/// committed; false when it was refused (safety interlock), rejected by the
/// device, or dropped because another change was still in flight. The
/// implementation reports the reason to the user — the return value exists so
/// an optimistic control knows to snap back instead of showing a value the
/// hardware never took.
typedef SwitchPortWriter = Future<bool> Function(SwitchPort port, double value);

/// The body of one connected Switch device: telemetry as gauges, controls
/// grouped by kind.
///
/// ASCOM hands us a flat list of up to a few dozen untyped ports, which reads
/// as an undifferentiated wall in a plain list. This lays them out the way the
/// hardware is actually organised — readings on top, then power outputs, dew
/// heaters and data ports as separate inset groups — using only the shape of
/// each port (see [classifySwitchPort]), so it holds for any vendor.
class SwitchDeviceBody extends StatelessWidget {
  final SwitchDevice device;
  final SwitchPortWriter onWrite;

  const SwitchDeviceBody({
    super.key,
    required this.device,
    required this.onWrite,
  });

  @override
  Widget build(BuildContext context) {
    final ports = device.ports;
    final readings = <SwitchPort>[];
    // Writable and read-only two-state ports are grouped together: a power box
    // exposes its always-on rail as a read-only boolean sitting in the middle
    // of its switchable ones (DC1 beside DC2..DC5), and splitting them would
    // strand it under "Sensors". Singletons fall back to a sensor pill below.
    final booleans = <SwitchPort>[];
    // An output that owns a companion mode port, rendered as one card. A dew
    // channel changes *shape* with its mode — the Gemini ADV3 reports DEW6 as
    // 0..100 in Manual but 0..1 in Auto/Switch — so a channel can arrive as
    // either a level or a toggle, and must stay paired with its mode either
    // way rather than splitting into a lone row plus an orphan picker.
    final channels = <SwitchPort>[];
    // Writable, but the device gave bounds admitting no value — show the number,
    // claim nothing.
    final stuck = <SwitchPort>[];
    // Mode ports are rendered inside their companion control, never as a row of
    // their own.
    final claimedModes = <int>{};
    for (final p in ports) {
      final kind = classifySwitchPort(p);
      // A device that reports its output read-only while auto-regulating still
      // owns its mode port — as a read-only boolean (indicator) OR a read-only
      // range (reading). _LevelCard's Auto branch renders either fine, and
      // pairing them keeps the channel from splitting into a stray row plus an
      // orphan picker.
      final mode = kind == SwitchPortKind.level ||
              kind == SwitchPortKind.toggle ||
              kind == SwitchPortKind.indicator ||
              kind == SwitchPortKind.reading
          ? findModePort(p, ports)
          : null;
      if (mode != null) {
        channels.add(p);
        claimedModes.add(mode.id);
        continue;
      }
      switch (kind) {
        case SwitchPortKind.reading:
          readings.add(p);
        case SwitchPortKind.indicator:
        case SwitchPortKind.toggle:
          booleans.add(p);
        case SwitchPortKind.level:
          channels.add(p);
        case SwitchPortKind.stuck:
          stuck.add(p);
        case SwitchPortKind.mode:
          break;
      }
    }
    // An orphan mode port (no matching output) still deserves a control.
    final orphanModes = [
      for (final p in ports)
        if (classifySwitchPort(p) == SwitchPortKind.mode &&
            !claimedModes.contains(p.id))
          p,
    ];

    const looseTitle = 'Switches';
    final groups = groupSwitchPorts(booleans, fallbackTitle: looseTitle);
    // A read-only boolean that didn't join a prefix group isn't a rail — it's a
    // status bit (sensor attached), which reads better as a pill than as a row
    // with a dead switch.
    final loose = groups
        .where((g) => g.title == looseTitle)
        .expand((g) => g.ports)
        .toList();
    final looseToggles = [
      for (final p in loose)
        if (p.canWrite) p,
    ];
    final sensors = [
      for (final p in loose)
        if (!p.canWrite) p,
    ];

    final sections = <Widget>[
      if (readings.isNotEmpty) _GaugeStrip(readings: readings),
      for (final g in groups)
        if (g.title != looseTitle) _ToggleGroup(group: g, onWrite: onWrite),
      if (looseToggles.isNotEmpty)
        _ToggleGroup(
          group: SwitchPortGroup(looseTitle, looseToggles),
          onWrite: onWrite,
        ),
      for (final c in channels)
        _LevelCard(
          key: ValueKey('level-${c.id}'),
          port: c,
          mode: findModePort(c, ports),
          onWrite: onWrite,
        ),
      for (final m in orphanModes)
        _LevelCard(
          key: ValueKey('mode-${m.id}'),
          port: null,
          mode: m,
          onWrite: onWrite,
        ),
      if (stuck.isNotEmpty)
        Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const _GroupHeader('Unavailable'),
            _InsetGroup(
              children: [for (final p in stuck) _ValueRow(port: p)],
            ),
          ],
        ),
      if (sensors.isNotEmpty) _SensorStrip(sensors: sensors),
    ];

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        for (var i = 0; i < sections.length; i++) ...[
          if (i > 0) const SizedBox(height: AraSpace.s16),
          sections[i],
        ],
      ],
    );
  }
}

/// Section header — uppercased, quiet, the macOS System Settings idiom.
class _GroupHeader extends StatelessWidget {
  final String text;

  /// §69 help entry for the section, when it has one. Device-named groups
  /// ("USB", "DC") describe themselves; the interpreted ones don't.
  final String? helpKey;
  const _GroupHeader(this.text, {this.helpKey});

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.only(left: 2, bottom: AraSpace.s8),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            // The header can be a device-supplied port name; a narrow settings
            // pane must ellipsise it rather than overflow.
            Flexible(
              child: Text(
                text.toUpperCase(),
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: AraText.section,
              ),
            ),
            if (helpKey != null) HelpIcon(helpKey: helpKey!),
          ],
        ),
      );
}

/// Rounded inset container that holds a run of rows, separated by hairlines.
class _InsetGroup extends StatelessWidget {
  final List<Widget> children;
  const _InsetGroup({required this.children});

  @override
  Widget build(BuildContext context) {
    return DecoratedBox(
      decoration: BoxDecoration(
        color: AraColors.bgPanelAlt,
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: AraColors.border, width: 0.5),
      ),
      child: Column(
        children: [
          for (var i = 0; i < children.length; i++) ...[
            if (i > 0)
              const Padding(
                padding: EdgeInsets.only(left: AraSpace.s12),
                child: Divider(
                  height: 0.5,
                  thickness: 0.5,
                  color: AraColors.border,
                ),
              ),
            children[i],
          ],
        ],
      ),
    );
  }
}

/// Read-only telemetry as a row of glanceable tiles.
class _GaugeStrip extends StatelessWidget {
  final List<SwitchPort> readings;
  const _GaugeStrip({required this.readings});

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        const _GroupHeader('Readings', helpKey: 'eq.switch.readings'),
        Wrap(
          spacing: AraSpace.s8,
          runSpacing: AraSpace.s8,
          children: [for (final p in readings) _MetricTile(port: p)],
        ),
      ],
    );
  }
}

/// One telemetry value: big figure, unit, caption.
class _MetricTile extends StatelessWidget {
  final SwitchPort port;
  const _MetricTile({required this.port});

  @override
  Widget build(BuildContext context) {
    final unit = switchPortUnit(port);
    // A MINIMUM height, not a fixed one: it keeps the strip even when a label
    // wraps to two lines, while still letting a tile grow under OS text scaling
    // (Windows 125 %, macOS accessibility sizes) instead of overflowing.
    final scale = MediaQuery.textScalerOf(context).scale(1);
    return Container(
      width: 128 * (scale > 1 ? scale : 1),
      constraints: BoxConstraints(minHeight: 88 * (scale > 1 ? scale : 1)),
      padding: const EdgeInsets.symmetric(
        horizontal: AraSpace.s12,
        vertical: 10,
      ),
      decoration: BoxDecoration(
        color: AraColors.bgPanelAlt,
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: AraColors.border, width: 0.5),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisSize: MainAxisSize.min,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.baseline,
            textBaseline: TextBaseline.alphabetic,
            children: [
              Flexible(
                child: Text(
                  formatSwitchValue(port.value),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: const TextStyle(
                    fontSize: 22,
                    fontWeight: FontWeight.w600,
                    color: AraColors.textPrimary,
                    fontFeatures: [FontFeature.tabularFigures()],
                  ),
                ),
              ),
              if (unit.isNotEmpty) ...[
                const SizedBox(width: 3),
                Padding(
                  padding: const EdgeInsets.only(bottom: 2),
                  child: Text(
                    unit,
                    style: const TextStyle(
                      fontSize: 12,
                      color: AraColors.textSecondary,
                    ),
                  ),
                ),
              ],
            ],
          ),
          const SizedBox(height: 2),
          Text(
            port.label,
            maxLines: 2,
            overflow: TextOverflow.ellipsis,
            style: AraText.caption,
          ),
        ],
      ),
    );
  }
}

/// Read-only booleans — sensor presence and similar — as compact status pills.
class _SensorStrip extends StatelessWidget {
  final List<SwitchPort> sensors;
  const _SensorStrip({required this.sensors});

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        const _GroupHeader('Sensors'),
        Wrap(
          spacing: AraSpace.s8,
          runSpacing: AraSpace.s8,
          children: [
            for (final s in sensors)
              Builder(builder: (context) {
                final on = s.value >= 0.5;
                final color =
                    on ? AraColors.accentConnected : AraColors.textDisabled;
                return Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: AraSpace.s12,
                    vertical: 7,
                  ),
                  decoration: BoxDecoration(
                    color: AraColors.bgPanelAlt,
                    borderRadius: BorderRadius.circular(999),
                    border: Border.all(color: AraColors.border, width: 0.5),
                  ),
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Container(
                        width: 7,
                        height: 7,
                        decoration: BoxDecoration(
                          color: color,
                          shape: BoxShape.circle,
                        ),
                      ),
                      const SizedBox(width: AraSpace.s8),
                      Flexible(
                        child: Text(
                          s.label,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: AraText.body,
                        ),
                      ),
                      const SizedBox(width: 6),
                      Text(on ? 'Attached' : 'Absent',
                          style: AraText.caption.copyWith(color: color)),
                    ],
                  ),
                );
              }),
          ],
        ),
      ],
    );
  }
}

/// A named run of two-state controls.
class _ToggleGroup extends StatelessWidget {
  final SwitchPortGroup group;
  final SwitchPortWriter onWrite;
  const _ToggleGroup({required this.group, required this.onWrite});

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _GroupHeader(group.title),
        _InsetGroup(
          children: [
            for (final p in group.ports) _ToggleRow(port: p, onWrite: onWrite),
          ],
        ),
      ],
    );
  }
}

/// One toggle row. A read-only "always on" rail still gets a row so the user
/// can see it exists — as a label, not a dead switch.
class _ToggleRow extends StatelessWidget {
  final SwitchPort port;
  final SwitchPortWriter onWrite;
  const _ToggleRow({required this.port, required this.onWrite});

  @override
  Widget build(BuildContext context) {
    final on = port.value >= 0.5;
    return Padding(
      padding: const EdgeInsets.symmetric(
        horizontal: AraSpace.s12,
        vertical: 6,
      ),
      child: SizedBox(
        height: 30,
        child: Row(
          children: [
            Expanded(child: Text(port.label, style: AraText.body)),
            if (!port.canWrite)
              Text(
                on ? 'Always on' : 'Off',
                style: AraText.caption,
              )
            else
              Transform.scale(
                scale: 0.8,
                alignment: Alignment.centerRight,
                child: Switch.adaptive(
                  value: on,
                  activeTrackColor: AraColors.accentConnected,
                  onChanged: (v) => onWrite(port, v ? port.max : port.min),
                ),
              ),
          ],
        ),
      ),
    );
  }
}

/// A port shown as a bare label and value — no control, and no claim that it
/// is read-only telemetry.
class _ValueRow extends StatelessWidget {
  final SwitchPort port;

  /// Optional note explaining why this is a readout rather than a control.
  final String? caption;

  /// Replaces the formatted numeric value when the raw number would not read
  /// as anything meaningful (a two-state output shown as 0/1).
  final String? overrideValue;
  const _ValueRow({required this.port, this.caption, this.overrideValue});

  @override
  Widget build(BuildContext context) {
    final unit = switchPortUnit(port);
    return Padding(
      padding: const EdgeInsets.symmetric(
        horizontal: AraSpace.s12,
        vertical: 10,
      ),
      child: Row(
        children: [
          Expanded(
            child: Text(
              port.label,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: AraText.body,
            ),
          ),
          const SizedBox(width: AraSpace.s8),
          if (caption != null) ...[
            Text(caption!, style: AraText.caption),
            const SizedBox(width: AraSpace.s8),
          ],
          Text(
            overrideValue ??
                (unit.isEmpty
                    ? formatSwitchValue(port.value)
                    : '${formatSwitchValue(port.value)} $unit'),
            style: AraText.numeric,
          ),
        ],
      ),
    );
  }
}

/// A continuous port (PWM duty, brightness) with its optional companion mode
/// picker. Keeps a local drag value so the thumb tracks the pointer before the
/// write round-trips.
class _LevelCard extends StatefulWidget {
  final SwitchPort? port;
  final SwitchPort? mode;
  final SwitchPortWriter onWrite;
  const _LevelCard({
    super.key,
    required this.port,
    required this.mode,
    required this.onWrite,
  });

  @override
  State<_LevelCard> createState() => _LevelCardState();
}

class _LevelCardState extends State<_LevelCard> {
  double? _dragValue;

  /// True between the first drag update and the write that follows the release.
  /// The panel now re-reads on its own every ~3 s, so without this a poll
  /// landing mid-gesture would clear the drag value and snap the thumb out from
  /// under the user's finger — and then write wherever that left it.
  bool _dragging = false;

  @override
  void didUpdateWidget(_LevelCard old) {
    super.didUpdateWidget(old);
    if (_dragging) return;
    if (old.port?.value != widget.port?.value) _dragValue = null;
  }

  Future<void> _write(SwitchPort port, double value) async {
    try {
      await widget.onWrite(port, quantiseSwitchWrite(port, value));
    } finally {
      // Always drop the optimistic value once the write has been answered, and
      // do it HERE rather than leaving it to didUpdateWidget: the drag guard
      // below suppresses that callback for the whole gesture, and the
      // post-write list read lands while the guard is still up (the notifier
      // refreshes before returning, and the daemon re-reads the device right
      // after the PUT). Waiting for a later transition would mean waiting for
      // one that was already consumed — leaving the card showing the dragged
      // number forever whenever the device clamps or quantises the value it
      // was sent. On a refusal the device never moved, so this is also the
      // snap-back off a value the hardware rejected.
      //
      // finally, not a plain await: if the writer ever rejects, _dragging must
      // still come down or didUpdateWidget stays suppressed for this card's
      // whole life.
      _dragging = false;
      if (mounted) setState(() => _dragValue = null);
    }
  }

  @override
  Widget build(BuildContext context) {
    final port = widget.port;
    final mode = widget.mode;
    final title = port?.label ?? mode?.label ?? '';
    final unit = port == null ? '' : switchPortUnit(port);
    final value =
        port == null ? 0.0 : (_dragValue ?? port.value).clamp(port.min, port.max);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _GroupHeader(title),
        _InsetGroup(
          children: [
            // Auto means the device regulates the channel from its own dew
            // point maths. Offering a live control there invites the user to
            // fight the regulation, and the §69 entry promises a readout.
            if (port != null && isAutoRegulating(mode))
              _ValueRow(
                port: port,
                caption: 'Set automatically',
                // In Auto the device reports the output as a plain on/off, so a
                // bare "1" would be meaningless where the help promises a
                // readout of what the controller chose.
                overrideValue: port.isBoolean
                    ? (port.value >= 0.5 ? 'On' : 'Off')
                    : null,
              )
            // In Switch mode the device reports the output as 0..1, so the
            // channel is an on/off — a two-stop slider would be a lie.
            else if (port != null && port.isBoolean)
              _ToggleRow(port: port, onWrite: widget.onWrite)
            else if (port != null)
              Padding(
                padding: const EdgeInsets.fromLTRB(
                  AraSpace.s12,
                  AraSpace.s8,
                  AraSpace.s12,
                  0,
                ),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        Text('Output', style: AraText.caption),
                        const Spacer(),
                        Text(
                          unit.isEmpty
                              ? formatSwitchValue(value.toDouble())
                              : '${formatSwitchValue(value.toDouble())} $unit',
                          style: AraText.numeric,
                        ),
                      ],
                    ),
                    SliderTheme(
                      data: SliderTheme.of(context).copyWith(
                        trackHeight: 3,
                        activeTrackColor: AraColors.accentInfo,
                        inactiveTrackColor: AraColors.border,
                        thumbColor: AraColors.textPrimary,
                        overlayShape:
                            const RoundSliderOverlayShape(overlayRadius: 12),
                        thumbShape:
                            const RoundSliderThumbShape(enabledThumbRadius: 7),
                      ),
                      child: Slider.adaptive(
                        min: port.min,
                        max: port.max,
                        // Snap to the device's step, or to whole steps when the
                        // port's range is whole — a dew channel is 0..100% in
                        // integer duty, and a bare continuous slider would
                        // write 63.42 and render "63.42 %". Left continuous for
                        // a genuinely fractional range, and capped so a huge
                        // range doesn't build a division per unit.
                        divisions: switchSliderDivisions(port),
                        value: value.toDouble(),
                        onChanged: (v) => setState(() {
                          _dragging = true;
                          _dragValue = v;
                        }),
                        onChangeEnd: (v) => _write(port, v),
                      ),
                    ),
                  ],
                ),
              ),
            if (mode != null)
              Padding(
                padding: const EdgeInsets.fromLTRB(
                  AraSpace.s12,
                  AraSpace.s8,
                  AraSpace.s12,
                  AraSpace.s12,
                ),
                child: Row(
                  children: [
                    Text('Mode', style: AraText.caption),
                    // Only a dew channel gets the dew help; a 0..3 fan speed
                    // also classifies as a mode and must not be annotated with
                    // text about dew points.
                    if (isDewModePort(mode))
                      const HelpIcon(helpKey: 'eq.switch.dew_mode'),
                    const Spacer(),
                    _ModePicker(mode: mode, onWrite: widget.onWrite),
                  ],
                ),
              ),
          ],
        ),
      ],
    );
  }
}

/// Segmented picker for a small integer range — the macOS control for
/// "choose one of a few".
class _ModePicker extends StatelessWidget {
  final SwitchPort mode;
  final SwitchPortWriter onWrite;
  const _ModePicker({required this.mode, required this.onWrite});

  @override
  Widget build(BuildContext context) {
    final labels = switchModeLabels(mode);
    final base = mode.min.round();
    final selected = mode.value.round().clamp(base, mode.max.round());
    return CupertinoSlidingSegmentedControl<int>(
      groupValue: selected,
      backgroundColor: AraColors.bgInput,
      thumbColor: AraColors.buttonSecondary,
      padding: const EdgeInsets.all(3),
      children: {
        for (var i = 0; i < labels.length; i++)
          base + i: Padding(
            padding: const EdgeInsets.symmetric(horizontal: AraSpace.s8),
            child: Text(
              labels[i],
              style: AraText.body.copyWith(fontSize: 12),
            ),
          ),
      },
      onValueChanged: (v) {
        if (v != null) onWrite(mode, v.toDouble());
      },
    );
  }
}
