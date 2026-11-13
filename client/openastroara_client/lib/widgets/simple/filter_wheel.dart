import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../../models/filter_wheel_status.dart';
import '../../theme/ara_colors.dart';

/// The usual filter names, lower case, and the short form a glass cell shows
/// (user, 2026-11-13: Green and Clear read as slot numbers beside the Plan's
/// names).
const kFilterShortNames = {
  'red': 'R',
  'r': 'R',
  'green': 'G',
  'g': 'G',
  'blue': 'B',
  'b': 'B',
  'clear': 'L',
  'luminance': 'L',
  'lum': 'L',
  'l': 'L',
  'ha': 'Ha',
  'h-alpha': 'Ha',
  'halpha': 'Ha',
  'h alpha': 'Ha',
  'hα': 'Ha',
  'oiii': 'OIII',
  'o3': 'OIII',
  'o-iii': 'OIII',
  'sii': 'SII',
  's2': 'SII',
  's-ii': 'SII',
};

/// The name's short form, lower case ('Green' → 'g'), so a tint follows the
/// letters the cell shows.
String _filterKey(String name) {
  final n = name.trim().toLowerCase();
  return (kFilterShortNames[n] ?? n).toLowerCase();
}

/// A filter's glass for its cell (#1676): the tint and the label colour on
/// it. A name Ara doesn't know is plain glass. The tint is content, like the
/// image behind the panel; the mark and the rings tell the state.
({Color glass, Color label}) filterGlass(String name) {
  const light = AraColors.bgPrimary, white = AraColors.selectionFg;
  return switch (_filterKey(name)) {
    'l' => (glass: AraColors.glassClear, label: white),
    'uv/ir cut' => (glass: AraColors.glassUvIr, label: light),
    'r' => (glass: AraColors.glassRed, label: white),
    'g' => (glass: AraColors.glassGreen, label: white),
    'b' => (glass: AraColors.glassBlue, label: white),
    'ha' => (glass: AraColors.glassHa, label: white),
    'oiii' => (glass: AraColors.glassOiii, label: white),
    'sii' => (glass: AraColors.glassSii, label: white),
    'dual-band' => (glass: AraColors.glassDualBand, label: white),
    'dark' => (glass: AraColors.glassDark, label: AraColors.glassDarkLabel),
    _ => (glass: AraColors.glassPlain, label: AraColors.textPrimary),
  };
}

/// Luminance passes all visible light, so its cell is a spectrum rather than
/// a white disc that would be the brightest thing on screen at night.
bool filterSpectrum(String name) => _filterKey(name) == 'l';

/// What a cell's ring says.
enum FilterCellRing { none, here, moving, asked, swap }

/// The Filter wheel window's wheel (#1676): a knurled rim, one round glass
/// cell per slot on its face and a fixed blue mark at the top over the light
/// path. Slot `i` of `n` sits `i·360/n` clockwise from the top and the wheel
/// turns by the shortest way to bring [pointAt] under the mark. The cells are
/// the only picker. Geometry is a ratio of [size], so it grows with the text.
class SimpleFilterWheel extends StatefulWidget {
  const SimpleFilterWheel({
    super.key,
    required this.slots,
    required this.size,
    required this.large,
    required this.pointAt,
    required this.ringOf,
    required this.locked,
    required this.semanticLabel,
    required this.slotLabel,
    required this.onPick,
    this.hub,
  });

  /// The wheel's slots in order.
  final List<FilterSlot> slots;

  /// The wheel's diameter, already scaled with the text.
  final double size;

  /// More than 8 slots: the bigger wheel with engraved slot numbers.
  final bool large;

  /// The slot (position) to bring under the mark; null holds it where it is
  /// (the wheel is turning by itself, or its slot is unknown).
  final int? pointAt;

  /// The ring of the cell at [FilterSlot.position].
  final FilterCellRing Function(int position) ringOf;

  /// Nothing can be tapped now (the wheel turns, or an ask or a swap waits).
  final bool locked;

  /// "5 · Ha" for a screen reader.
  final String Function(FilterSlot) semanticLabel;

  /// The cell's own words: the name when it fits (four letters), else the
  /// slot number.
  final String Function(FilterSlot) slotLabel;

  final ValueChanged<int> onPick;

  /// The big wheel's hub: caption, name and slot.
  final Widget? hub;

  @override
  State<SimpleFilterWheel> createState() => _SimpleFilterWheelState();
}

class _SimpleFilterWheelState extends State<SimpleFilterWheel>
    with SingleTickerProviderStateMixin {
  late final AnimationController _turn = AnimationController(vsync: this);
  double _from = 0, _to = 0;
  int? _at;

  double get _step => 360 / math.max(1, widget.slots.length);

  int? _indexOf(int? position) {
    if (position == null) return null;
    final i = widget.slots.indexWhere((s) => s.position == position);
    return i < 0 ? null : i;
  }

  @override
  void initState() {
    super.initState();
    _place();
  }

  /// Puts [SimpleFilterWheel.pointAt] under the mark, without a turn; with
  /// none, the cell already there stays (the first, if the count has shrunk
  /// past it).
  void _place() {
    _at = _indexOf(widget.pointAt) ?? _at;
    if (_at != null && _at! >= widget.slots.length) _at = null;
    _from = _to = -(_at ?? 0) * _step;
  }

  /// The angle now, in degrees.
  double get _rot =>
      _from + (_to - _from) * Curves.easeInOut.transform(_turn.value);

  @override
  void didUpdateWidget(SimpleFilterWheel old) {
    super.didUpdateWidget(old);
    final n = widget.slots.length;
    if (n != old.slots.length) {
      // Another slot count spaces the cells anew: lay them out again, no turn.
      _place();
      return;
    }
    final next = _indexOf(widget.pointAt);
    if (n == 0 || next == null || next == _at) return;
    if (_at == null) {
      // Opened while the slot was unknown: no angle to turn from, so jump.
      _at = next;
      _from = _to = -next * _step;
      return;
    }
    // The short way round, a signed number of slots.
    var d = (next - _at!) % n;
    if (d > n / 2) d -= n;
    final start = _rot;
    _at = next;
    _from = start;
    _to = start - d * _step;
    if (MediaQuery.disableAnimationsOf(context) || d == 0) {
      _turn.value = 1;
    } else {
      _turn
        ..duration = Duration(milliseconds: 450 * d.abs() + 250)
        ..forward(from: 0);
    }
  }

  @override
  void dispose() {
    _turn.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final n = widget.slots.length;
    final size = widget.size;
    final k = size / (widget.large ? 260 : 180);
    final centre = size / 2;
    final ring = size * (widget.large ? 0.37 : 0.33);
    final hubR = size * (widget.large ? 0.20 : 0.14);
    final cell = math.min(
      (widget.large ? 52 : 48) * k,
      2 * math.pi * ring / n - 6 * k,
    );
    final markH = 14 * k;
    final labelScaler = MediaQuery.textScalerOf(context);
    return SizedBox(
      width: size,
      height: size + markH - 4 * k,
      child: Stack(
        clipBehavior: Clip.none,
        children: [
          Positioned(
            left: 0,
            top: markH - 4 * k,
            width: size,
            height: size,
            child: AnimatedBuilder(
              animation: _turn,
              builder: (context, _) {
                final rot = _rot * math.pi / 180;
                return Stack(
                  clipBehavior: Clip.none,
                  children: [
                    Positioned.fill(
                      child: Transform.rotate(
                        angle: rot,
                        child: CustomPaint(painter: _RimPainter(k)),
                      ),
                    ),
                    for (var i = 0; i < n; i++)
                      Builder(
                        builder: (context) {
                          final a = i * _step * math.pi / 180;
                          // The carrier turns the cells round the centre; the
                          // labels counter-turn so they stay upright.
                          final p = Offset(
                            centre + math.sin(a) * ring,
                            centre - math.cos(a) * ring,
                          );
                          final turned = Offset(
                            centre +
                                (p.dx - centre) * math.cos(rot) -
                                (p.dy - centre) * math.sin(rot),
                            centre +
                                (p.dx - centre) * math.sin(rot) +
                                (p.dy - centre) * math.cos(rot),
                          );
                          // A round target, 44 pt where there is room but never
                          // past half-way to the next cell, so a tap on a cell's
                          // edge never picks its neighbour.
                          final hit = math.min(
                            math.max(cell, 44.0),
                            n > 1
                                ? 2 * ring * math.sin(math.pi / n)
                                : double.infinity,
                          );
                          return Positioned(
                            left: turned.dx - hit / 2,
                            top: turned.dy - hit / 2,
                            width: hit,
                            height: hit,
                            child: _Cell(
                              key: Key(
                                'simple-filterwheel-slot-${widget.slots[i].position}',
                              ),
                              slot: widget.slots[i],
                              diameter: cell,
                              ring: widget.ringOf(widget.slots[i].position),
                              dim:
                                  widget.locked &&
                                  widget.ringOf(widget.slots[i].position) ==
                                      FilterCellRing.none,
                              enabled: !widget.locked,
                              semanticLabel: widget.semanticLabel(
                                widget.slots[i],
                              ),
                              label: widget.slotLabel(widget.slots[i]),
                              labelScaler: labelScaler,
                              onTap:
                                  widget.locked ||
                                      widget.ringOf(widget.slots[i].position) ==
                                          FilterCellRing.here
                                  ? null
                                  : () =>
                                        widget.onPick(widget.slots[i].position),
                            ),
                          );
                        },
                      ),
                    if (widget.large)
                      for (var i = 0; i < n; i++)
                        Builder(
                          builder: (context) {
                            // The slot number engraved toward the hub. It grows
                            // with the wheel, which already scales with the text.
                            final a = i * _step * math.pi / 180 + rot;
                            final r = ring - cell / 2 - 9 * k;
                            return Positioned(
                              left: centre + math.sin(a) * r - 12 * k,
                              top: centre - math.cos(a) * r - 8 * k,
                              width: 24 * k,
                              height: 16 * k,
                              child: ExcludeSemantics(
                                child: Center(
                                  child: Text(
                                    '${widget.slots[i].position}',
                                    maxLines: 1,
                                    textScaler: TextScaler.noScaling,
                                    style: TextStyle(
                                      fontSize: 10 * k,
                                      fontWeight: FontWeight.w600,
                                      color: AraColors.textSecondary,
                                      fontFeatures: const [
                                        FontFeature.tabularFigures(),
                                      ],
                                    ),
                                  ),
                                ),
                              ),
                            );
                          },
                        ),
                  ],
                );
              },
            ),
          ),
          // The hub and the mark stay still.
          Positioned(
            left: centre - hubR,
            top: markH - 4 * k + centre - hubR,
            width: hubR * 2,
            height: hubR * 2,
            child: IgnorePointer(
              child: DecoratedBox(
                decoration: const BoxDecoration(
                  shape: BoxShape.circle,
                  color: AraColors.bgPrimary,
                  border: Border.fromBorderSide(
                    BorderSide(color: AraColors.border),
                  ),
                ),
                child: widget.hub == null
                    ? null
                    : Padding(
                        padding: const EdgeInsets.all(4),
                        child: widget.hub,
                      ),
              ),
            ),
          ),
          Positioned(
            left: centre - 9 * k,
            top: 0,
            child: ExcludeSemantics(
              child: IgnorePointer(
                child: CustomPaint(
                  size: Size(18 * k, markH),
                  painter: _MarkPainter(),
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// One glass cell: round, tinted, a soft highlight at its top left and an
/// inner dark edge, ringed by its state; a button for a screen reader.
class _Cell extends StatefulWidget {
  const _Cell({
    super.key,
    required this.slot,
    required this.diameter,
    required this.ring,
    required this.dim,
    required this.enabled,
    required this.semanticLabel,
    required this.label,
    required this.labelScaler,
    required this.onTap,
  });

  final FilterSlot slot;
  final double diameter;
  final FilterCellRing ring;
  final bool dim, enabled;
  final String semanticLabel, label;
  final TextScaler labelScaler;
  final VoidCallback? onTap;

  @override
  State<_Cell> createState() => _CellState();
}

class _CellState extends State<_Cell> {
  bool _focused = false;

  Color? get _ringColor => switch (widget.ring) {
    FilterCellRing.none => null,
    FilterCellRing.here => AraColors.linkText,
    FilterCellRing.moving => AraColors.accentConnected,
    FilterCellRing.asked => AraColors.textSecondary,
    FilterCellRing.swap => AraColors.accentBusy,
  };

  @override
  Widget build(BuildContext context) {
    final tint = filterGlass(widget.slot.name);
    final d = widget.diameter;
    final ring = _ringColor;
    return Semantics(
      container: true,
      button: true,
      enabled: widget.enabled,
      selected: widget.ring == FilterCellRing.here,
      label: widget.semanticLabel,
      excludeSemantics: true,
      onTap: widget.onTap,
      // The whole round box is the target, not only the glass.
      child: ClipOval(
        child: Opacity(
          opacity: widget.dim ? 0.45 : 1,
          child: Material(
            type: MaterialType.transparency,
            child: InkResponse(
              onTap: widget.onTap,
              onFocusChange: (v) => setState(() => _focused = v),
              customBorder: const CircleBorder(),
              containedInkWell: false,
              radius: d / 2,
              child: Center(
                child: Container(
                  width: d + 8,
                  height: d + 8,
                  alignment: Alignment.center,
                  decoration: BoxDecoration(
                    shape: BoxShape.circle,
                    border: Border.all(
                      width: 2,
                      color: _focused
                          ? AraColors.accentInfo
                          : Colors.transparent,
                    ),
                  ),
                  child: Container(
                    width: d,
                    height: d,
                    decoration: BoxDecoration(
                      shape: BoxShape.circle,
                      border: ring == null
                          ? null
                          : Border.all(color: ring, width: 2),
                    ),
                    padding: EdgeInsets.all(ring == null ? 0 : 2),
                    child: DecoratedBox(
                      decoration: BoxDecoration(
                        shape: BoxShape.circle,
                        color: tint.glass,
                        gradient: filterSpectrum(widget.slot.name)
                            ? AraColors.glassSpectrum
                            : null,
                        border: Border.all(
                          color: AraColors.bgPrimary,
                          width: 2,
                        ),
                      ),
                      position: DecorationPosition.background,
                      child: DecoratedBox(
                        position: DecorationPosition.foreground,
                        decoration: const BoxDecoration(
                          shape: BoxShape.circle,
                          gradient: RadialGradient(
                            center: Alignment(-0.32, -0.44),
                            radius: 0.9,
                            colors: [AraColors.glassGloss, Colors.transparent],
                            stops: [0, 0.5],
                          ),
                        ),
                        child: Center(
                          child: Padding(
                            padding: const EdgeInsets.all(2),
                            child: FittedBox(
                              fit: BoxFit.scaleDown,
                              // Upright whatever the wheel's turn.
                              child: Text(
                                widget.label,
                                maxLines: 1,
                                textScaler: widget.labelScaler,
                                style: TextStyle(
                                  fontSize: 12,
                                  fontWeight: FontWeight.w600,
                                  color: tint.label,
                                  shadows: filterSpectrum(widget.slot.name)
                                      ? const [
                                          Shadow(
                                            color: AraColors.glassDark,
                                            blurRadius: 3,
                                          ),
                                        ]
                                      : null,
                                ),
                              ),
                            ),
                          ),
                        ),
                      ),
                    ),
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// The knurled rim (alternating 2° bands) and the face inside it.
class _RimPainter extends CustomPainter {
  _RimPainter(this.k);
  final double k;

  @override
  void paint(Canvas canvas, Size size) {
    final c = size.center(Offset.zero);
    final r = size.width / 2;
    final rect = Rect.fromCircle(center: c, radius: r);
    canvas.drawCircle(c, r, Paint()..color = AraColors.wheelRimDark);
    final light = Paint()..color = AraColors.wheelRimLight;
    const band = 2 * math.pi / 180;
    for (var a = 0.0; a < 2 * math.pi - 1e-6; a += 2 * band) {
      canvas.drawArc(rect, a, band, true, light);
    }
    canvas.drawCircle(c, r - 7 * k, Paint()..color = AraColors.wheelFace);
  }

  @override
  bool shouldRepaint(_RimPainter old) => old.k != k;
}

/// The fixed blue mark at the top, pointing at the cell in the light path.
class _MarkPainter extends CustomPainter {
  @override
  void paint(Canvas canvas, Size size) {
    canvas.drawPath(
      Path()
        ..moveTo(0, 0)
        ..lineTo(size.width, 0)
        ..lineTo(size.width / 2, size.height)
        ..close(),
      Paint()..color = AraColors.accentInfo,
    );
  }

  @override
  bool shouldRepaint(_MarkPainter old) => false;
}
