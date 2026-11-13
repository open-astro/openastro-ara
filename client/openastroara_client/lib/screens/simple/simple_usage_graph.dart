import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart' show KeyUpEvent, LogicalKeyboardKey;
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../models/stats/usage_history.dart';
import '../../state/night_mode_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';

/// #1582 — Settings › Rig: Storage, CPU and Memory drawn from the rig's own
/// 12 h of readings, over the last hour or the run so far.

enum UsageKind { storage, cpu, memory }

enum UsageWindow { hour, run }

const _gib = 1073741824;
const _keep = Duration(hours: 12);

/// Memory's scale and warning level, in GiB (the Pi the panel was drawn for has 4).
const _memoryMaxGb = 4.0;
const _memoryWarnGb = 3.4;
const _cpuWarn = 85.0;

/// The run's start when "Run so far" can be drawn: it began inside the 12 h the
/// rig keeps. Null otherwise (the choice shows disabled). Pure.
DateTime? usageRunStart(DateTime? runStart, DateTime now) =>
    runStart != null && now.difference(runStart) < _keep ? runStart : null;

/// The samples inside the window, oldest first. Pure.
List<UsageSample> usageWindowSamples(
  List<UsageSample> all, {
  required DateTime now,
  required UsageWindow window,
  DateTime? runStart,
}) {
  final from = window == UsageWindow.run && runStart != null
      ? runStart
      : now.subtract(const Duration(hours: 1));
  return [
    for (final s in all)
      if (!s.at.isBefore(from) && !s.at.isAfter(now)) s,
  ];
}

/// One sample's figure for [kind]: GiB free, % of all cores, GiB in use. Null = unread.
double? usageValue(UsageKind kind, UsageSample s) => switch (kind) {
  UsageKind.storage =>
    s.storageFreeBytes == null ? null : s.storageFreeBytes! / _gib,
  UsageKind.cpu => s.cpuPercent,
  UsageKind.memory => s.memoryBytes == null ? null : s.memoryBytes! / _gib,
};

/// The trend over the readings, in words ("Falling about 16 GB per hour"), or
/// "Collecting…" under three readings. [real] is oldest first. Pure.
String usageTrend(
  AppLocalizations l10n,
  UsageKind kind,
  List<(DateTime, double)> real,
) {
  if (real.length < 3) return l10n.usageCollecting;
  final (t0, v0) = real.first;
  final (t1, v1) = real.last;
  if (kind == UsageKind.cpu) {
    final d = v1 - v0;
    if (d.abs() < 5) return l10n.usageSteady;
    return d > 0
        ? l10n.usageCpuRising(d.round().toString())
        : l10n.usageCpuFalling(d.abs().round().toString());
  }
  final hours = math.max(t1.difference(t0).inSeconds, 1) / 3600;
  final perHour = (v1 - v0) / hours;
  if (perHour.abs() < (kind == UsageKind.storage ? 1 : 0.05)) {
    return l10n.usageSteady;
  }
  final n = perHour.abs().toStringAsFixed(kind == UsageKind.storage ? 0 : 2);
  return perHour > 0 ? l10n.usageRising(n) : l10n.usageFalling(n);
}

/// The y range of a graph: storage is scaled to its window (free space barely moves
/// against the whole disk, and the axis says so), CPU is 0-100, memory 0-4 GB. Pure.
(double, double) usageRange(UsageKind kind, List<double> values) {
  switch (kind) {
    case UsageKind.cpu:
      return (0, 100);
    case UsageKind.memory:
      return (
        0,
        math.max(
          _memoryMaxGb,
          values.isEmpty ? 0 : values.reduce(math.max).ceilToDouble(),
        ),
      );
    case UsageKind.storage:
      if (values.isEmpty) return (0, 1);
      final lo = math.max(0.0, (values.reduce(math.min) - 4).floorToDouble());
      return (lo, values.reduce(math.max).ceilToDouble() + 4);
  }
}

/// Where the dashed warning line sits for [kind], or null when it is off the scale. Pure.
double? usageWarnLevel(UsageKind kind, {double? totalGb}) => switch (kind) {
  UsageKind.storage => totalGb == null ? null : totalGb * 0.10,
  UsageKind.cpu => _cpuWarn,
  UsageKind.memory => _memoryWarnGb,
};

bool usageWarns(UsageKind kind, double current, {double? totalGb}) {
  final level = usageWarnLevel(kind, totalGb: totalGb);
  if (level == null) return false;
  return kind == UsageKind.storage ? current < level : current > level;
}

String usageFormat(UsageKind kind, double v) => switch (kind) {
  UsageKind.storage => '${v.toStringAsFixed(v >= 100 ? 0 : 1)} GB',
  UsageKind.cpu => '${v.round()} %',
  UsageKind.memory => '${v.toStringAsFixed(2)} GB',
};

/// Night draws a heavier line and fill (and a ringed end), so no meaning rests on hue (N2).
double usageLineWidth(bool night) => night ? 3 : 2;
double usageFillAlpha(bool night) => night ? 0.30 : 0.16;

/// The three graphs and the Last hour / Run so far choice.
class SimpleUsageGraphs extends ConsumerStatefulWidget {
  const SimpleUsageGraphs({
    super.key,
    required this.history,
    this.runStart,
    this.clock,
  });

  final UsageHistory history;

  /// When the current or latest sequence run began, if the app knows.
  final DateTime? runStart;

  /// Test seam for "now".
  final DateTime Function()? clock;

  @override
  ConsumerState<SimpleUsageGraphs> createState() => _SimpleUsageGraphsState();
}

class _SimpleUsageGraphsState extends ConsumerState<SimpleUsageGraphs> {
  UsageWindow _window = UsageWindow.hour;

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final now = (widget.clock ?? DateTime.now)().toUtc();
    final runStart = usageRunStart(widget.runStart?.toUtc(), now);
    // The run left the 12 h the rig keeps while the choice was on: fall back to the hour.
    final window = runStart == null ? UsageWindow.hour : _window;
    final samples = usageWindowSamples(
      widget.history.samples,
      now: now,
      window: window,
      runStart: runStart,
    );
    final span = window == UsageWindow.run
        ? now.difference(runStart!)
        : const Duration(hours: 1);
    final night = switch (ref.watch(nightModeProvider)) {
      AsyncData(:final value) => value,
      _ => false,
    };
    double? totalGb;
    for (final s in widget.history.samples.reversed) {
      if (s.storageTotalBytes != null) {
        totalGb = s.storageTotalBytes! / _gib;
        break;
      }
    }
    return Column(
      key: const Key('simple-usage-graphs'),
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s8,
      children: [
        _WindowChoice(
          window: window,
          runAvailable: runStart != null,
          onPick: (w) => setState(() => _window = w),
        ),
        if (widget.history.samples.isEmpty)
          Text(
            l10n.usageEmpty,
            key: const Key('usage-empty'),
            style: AraText.caption,
          ),
        for (final kind in UsageKind.values)
          _UsageCard(
            key: Key('usage-${kind.name}'),
            kind: kind,
            samples: samples,
            now: now,
            span: span,
            window: window,
            processors: widget.history.processorCount,
            totalGb: totalGb,
            night: night,
          ),
      ],
    );
  }
}

class _WindowChoice extends StatelessWidget {
  const _WindowChoice({
    required this.window,
    required this.runAvailable,
    required this.onPick,
  });

  final UsageWindow window;
  final bool runAvailable;
  final ValueChanged<UsageWindow> onPick;

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    Widget choice(UsageWindow w, String label, bool enabled) => Semantics(
      button: true,
      selected: window == w,
      enabled: enabled,
      child: ConstrainedBox(
        constraints: const BoxConstraints(minHeight: 44, minWidth: 44),
        child: Material(
          color: window == w
              ? AraColors.selectionBg
              : AraColors.buttonSecondary,
          borderRadius: BorderRadius.circular(AraRadius.container),
          child: InkWell(
            key: Key('usage-window-${w.name}'),
            borderRadius: BorderRadius.circular(AraRadius.container),
            onTap: enabled && window != w ? () => onPick(w) : null,
            child: Padding(
              padding: const EdgeInsets.symmetric(
                horizontal: AraSpace.s12,
                vertical: AraSpace.s8,
              ),
              child: Center(
                widthFactor: 1,
                child: Text(
                  label,
                  style: kSimpleText.copyWith(
                    color: enabled
                        ? (window == w
                              ? AraColors.selectionFg
                              : AraColors.textPrimary)
                        : AraColors.textDisabled,
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    );
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      spacing: AraSpace.s4,
      children: [
        Wrap(
          spacing: AraSpace.s8,
          runSpacing: AraSpace.s8,
          children: [
            choice(UsageWindow.hour, l10n.usageWindowHour, true),
            choice(UsageWindow.run, l10n.usageWindowRun, runAvailable),
          ],
        ),
        if (!runAvailable)
          Text(
            l10n.usageRunUnavailable,
            key: const Key('usage-run-unavailable'),
            style: AraText.caption,
          ),
      ],
    );
  }
}

class _UsageCard extends StatefulWidget {
  const _UsageCard({
    super.key,
    required this.kind,
    required this.samples,
    required this.now,
    required this.span,
    required this.window,
    required this.processors,
    required this.totalGb,
    required this.night,
  });

  final UsageKind kind;
  final List<UsageSample> samples;
  final DateTime now;
  final Duration span;
  final UsageWindow window;
  final int processors;
  final double? totalGb;
  final bool night;

  @override
  State<_UsageCard> createState() => _UsageCardState();
}

class _UsageCardState extends State<_UsageCard> {
  bool _table = false;
  DateTime? _cursor;
  final _focus = FocusNode();

  // The ring follows the last input: keys show it, a tap or click never draws
  // it (WCAG 2.4.7; the mount pad's rule).
  void _onHighlight(FocusHighlightMode _) {
    if (mounted) setState(() {});
  }

  @override
  void initState() {
    super.initState();
    FocusManager.instance.addHighlightModeListener(_onHighlight);
  }

  @override
  void dispose() {
    FocusManager.instance.removeHighlightModeListener(_onHighlight);
    _focus.dispose();
    super.dispose();
  }

  UsageSample? _nearest(DateTime t) {
    UsageSample? best;
    for (final s in widget.samples) {
      if (best == null ||
          (s.at.difference(t)).abs() < (best.at.difference(t)).abs()) {
        best = s;
      }
    }
    return best;
  }

  void _pointAt(double fraction) {
    final t = widget.now.subtract(widget.span * (1 - fraction.clamp(0.0, 1.0)));
    setState(() => _cursor = _nearest(t)?.at);
  }

  KeyEventResult _key(FocusNode node, KeyEvent event) {
    if (event is KeyUpEvent) return KeyEventResult.ignored;
    final key = event.logicalKey;
    if (key == LogicalKeyboardKey.escape) {
      // No crosshair to clear: let Escape reach the route (close a sheet).
      if (_cursor == null) return KeyEventResult.ignored;
      setState(() => _cursor = null);
      return KeyEventResult.handled;
    }
    if (key != LogicalKeyboardKey.arrowLeft &&
        key != LogicalKeyboardKey.arrowRight) {
      return KeyEventResult.ignored;
    }
    final s = widget.samples;
    if (s.isEmpty) return KeyEventResult.handled;
    var i = _cursor == null
        ? s.length - 1
        : s.indexWhere((x) => x.at == _cursor);
    if (i < 0) i = s.length - 1;
    // About a minute a press (4 readings), not 15 s, so an hour is 60 presses.
    final step = key == LogicalKeyboardKey.arrowLeft ? -4 : 4;
    setState(() => _cursor = s[(i + step).clamp(0, s.length - 1)].at);
    return KeyEventResult.handled;
  }

  String _ago(AppLocalizations l10n, DateTime at) {
    final mins = widget.now.difference(at).inMinutes;
    if (mins <= 0) return l10n.usageNow;
    return mins >= 120
        ? l10n.usageHoursAgo((mins / 60).round().toString())
        : l10n.usageMinAgo(mins.toString());
  }

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final kind = widget.kind;
    final points = <(DateTime, double)>[
      for (final s in widget.samples)
        if (usageValue(kind, s) case final v?) (s.at, v),
    ];
    final name = switch (kind) {
      UsageKind.storage => l10n.previewStorage,
      UsageKind.cpu => l10n.previewCpu,
      UsageKind.memory => l10n.previewMemory,
    };
    final cur = points.isEmpty ? null : points.last.$2;
    final trend = usageTrend(l10n, kind, points);
    final totalGb = widget.totalGb;
    final warn = cur != null && usageWarns(kind, cur, totalGb: totalGb);
    final big = cur == null
        ? '—'
        : switch (kind) {
            UsageKind.storage =>
              totalGb == null
                  ? usageFormat(kind, cur)
                  : l10n.usageStorageBig(
                      cur.toStringAsFixed(cur >= 100 ? 0 : 1),
                      totalGb.round().toString(),
                    ),
            UsageKind.cpu => l10n.usageCpuBig(
              cur.round().toString(),
              widget.processors.toString(),
            ),
            UsageKind.memory => l10n.usageMemoryBig(cur.toStringAsFixed(1)),
          };
    final warnText = switch (kind) {
      UsageKind.storage => l10n.usageWarnStorage,
      UsageKind.cpu => l10n.usageWarnCpu,
      UsageKind.memory => l10n.usageWarnMemory,
    };
    final windowPhrase = widget.window == UsageWindow.run
        ? l10n.usageWindowRunPhrase
        : l10n.usageWindowHourPhrase;
    final alt = l10n.usageAltText(
      name,
      '$big${warn ? ' — $warnText' : ''}',
      windowPhrase,
      trend,
    );
    final range = usageRange(kind, [for (final p in points) p.$2]);
    final warnLevel = usageWarnLevel(kind, totalGb: totalGb);
    final showWarnLine =
        warnLevel != null && warnLevel >= range.$1 && warnLevel <= range.$2;
    final cursorSample = _cursor == null
        ? null
        : widget.samples.where((s) => s.at == _cursor).firstOrNull;
    final tip = cursorSample == null
        ? ''
        : '${_ago(l10n, cursorSample.at)}: ${usageValue(kind, cursorSample) == null ? l10n.usageNoData : usageFormat(kind, usageValue(kind, cursorSample)!)}';
    final trendIcon = trend == l10n.usageCollecting
        ? Icons.more_horiz
        : trend == l10n.usageSteady
        ? Icons.trending_flat
        : points.last.$2 > points.first.$2
        ? Icons.trending_up
        : Icons.trending_down;
    final axisLeft = widget.span.inMinutes >= 120
        ? l10n.usageHoursAgo((widget.span.inMinutes / 60).round().toString())
        : l10n.usageMinAgo(widget.span.inMinutes.toString());
    final axisMid = showWarnLine
        ? l10n.usageWarnLine
        : (kind == UsageKind.storage && points.isNotEmpty
              ? l10n.usageScale(
                  range.$1.round().toString(),
                  range.$2.round().toString(),
                )
              : '');
    return DecoratedBox(
      decoration: BoxDecoration(
        color: AraColors.tileFill,
        borderRadius: BorderRadius.circular(AraRadius.container),
      ),
      child: Padding(
        padding: const EdgeInsets.symmetric(
          horizontal: AraSpace.s12,
          vertical: AraSpace.s8,
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          spacing: AraSpace.s4,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    name,
                    style: kSimpleText.copyWith(color: AraColors.textSecondary),
                  ),
                ),
                ConstrainedBox(
                  constraints: const BoxConstraints(
                    minHeight: 44,
                    minWidth: 44,
                  ),
                  child: TextButton(
                    key: Key('usage-table-${kind.name}'),
                    onPressed: () => setState(() => _table = !_table),
                    child: Text(
                      _table ? l10n.usageHideTable : l10n.usageShowTable,
                    ),
                  ),
                ),
              ],
            ),
            Text(
              big,
              key: Key('usage-big-${kind.name}'),
              style: kSimpleText.copyWith(
                fontSize: AraFontSize.simpleReadingLarge,
                fontWeight: FontWeight.w300,
                fontFeatures: const [FontFeature.tabularFigures()],
              ),
            ),
            Row(
              spacing: AraSpace.s8,
              children: [
                Icon(trendIcon, size: 18, color: AraColors.textPrimary),
                Expanded(
                  child: Text(
                    trend,
                    key: Key('usage-trend-${kind.name}'),
                    style: kSimpleText,
                  ),
                ),
              ],
            ),
            if (warn)
              Row(
                key: Key('usage-warn-${kind.name}'),
                spacing: AraSpace.s8,
                children: [
                  const Icon(
                    Icons.warning_amber_rounded,
                    size: 18,
                    color: AraColors.accentWarning,
                  ),
                  Expanded(child: Text(warnText, style: kSimpleText)),
                ],
              ),
            Semantics(
              label: alt,
              container: true,
              excludeSemantics: true,
              child: Focus(
                focusNode: _focus,
                onKeyEvent: _key,
                child: LayoutBuilder(
                  builder: (context, box) => Listener(
                    onPointerDown: (e) {
                      _focus.requestFocus();
                      _pointAt(e.localPosition.dx / box.maxWidth);
                    },
                    onPointerMove: (e) =>
                        _pointAt(e.localPosition.dx / box.maxWidth),
                    child: MouseRegion(
                      onHover: (e) =>
                          _pointAt(e.localPosition.dx / box.maxWidth),
                      onExit: (_) => setState(() => _cursor = null),
                      child: AnimatedBuilder(
                        animation: _focus,
                        builder: (context, _) => Container(
                          key: Key('usage-graph-${kind.name}'),
                          height: 96,
                          decoration: BoxDecoration(
                            border: Border.all(
                              color:
                                  _focus.hasFocus &&
                                      FocusManager.instance.highlightMode ==
                                          FocusHighlightMode.traditional
                                  ? AraColors.accentInfo
                                  : Colors.transparent,
                              width: 2,
                            ),
                            borderRadius: BorderRadius.circular(
                              AraRadius.field,
                            ),
                          ),
                          child: CustomPaint(
                            painter: _GraphPainter(
                              samples: widget.samples,
                              kind: kind,
                              now: widget.now,
                              span: widget.span,
                              range: range,
                              warnLevel: showWarnLine ? warnLevel : null,
                              cursor: _cursor,
                              night: widget.night,
                            ),
                            size: Size.infinite,
                          ),
                        ),
                      ),
                    ),
                  ),
                ),
              ),
            ),
            Wrap(
              alignment: WrapAlignment.spaceBetween,
              spacing: AraSpace.s8,
              children: [
                Text(axisLeft, style: AraText.caption),
                if (axisMid.isNotEmpty) Text(axisMid, style: AraText.caption),
                Text(l10n.usageNow, style: AraText.caption),
              ],
            ),
            if (points.length < 2)
              Text(l10n.usageCollecting, style: AraText.caption),
            if (tip.isNotEmpty)
              Semantics(
                liveRegion: true,
                child: Text(
                  tip,
                  key: Key('usage-tip-${kind.name}'),
                  style: kSimpleText,
                ),
              ),
            if (_table) _table_(l10n, kind, name),
          ],
        ),
      ),
    );
  }

  /// Six readings spread across the window, each with its age.
  Widget _table_(AppLocalizations l10n, UsageKind kind, String name) {
    final s = widget.samples;
    final rows = <UsageSample>[
      if (s.isNotEmpty)
        for (var i = 0; i < 6; i++) s[((s.length - 1) * i / 5).round()],
    ];
    return Table(
      key: Key('usage-table-body-${kind.name}'),
      defaultColumnWidth: const IntrinsicColumnWidth(),
      columnWidths: const {0: FlexColumnWidth()},
      children: [
        TableRow(
          children: [
            Text(l10n.usageTableWhen, style: AraText.caption),
            Text(name, style: AraText.caption),
          ],
        ),
        for (final r in rows)
          TableRow(
            children: [
              Text(_ago(l10n, r.at), style: kSimpleText),
              Text(
                usageValue(kind, r) == null
                    ? l10n.usageNoData
                    : usageFormat(kind, usageValue(kind, r)!),
                style: kSimpleText.copyWith(
                  fontFeatures: const [FontFeature.tabularFigures()],
                ),
              ),
            ],
          ),
      ],
    );
  }
}

class _GraphPainter extends CustomPainter {
  _GraphPainter({
    required this.samples,
    required this.kind,
    required this.now,
    required this.span,
    required this.range,
    required this.warnLevel,
    required this.cursor,
    required this.night,
  });

  final List<UsageSample> samples;
  final UsageKind kind;
  final DateTime now;
  final Duration span;
  final (double, double) range;
  final double? warnLevel;
  final DateTime? cursor;
  final bool night;

  @override
  void paint(Canvas canvas, Size size) {
    final (lo, hi) = range;
    final spanMs = math.max(span.inMilliseconds, 1);
    double x(DateTime t) =>
        (1 - now.difference(t).inMilliseconds / spanMs) * size.width;
    double y(double v) =>
        size.height -
        3 -
        (v - lo) / math.max(hi - lo, 1e-9) * (size.height - 8);
    // Night: a heavier line and fill, and a ringed end, so nothing rests on hue.
    final line = Paint()
      ..color = AraColors.accentInfo
      ..style = PaintingStyle.stroke
      ..strokeWidth = usageLineWidth(night)
      ..strokeJoin = StrokeJoin.round;
    final fill = Paint()
      ..color = AraColors.accentInfo.withValues(alpha: usageFillAlpha(night));
    final grid = Paint()
      ..color = AraColors.border
      ..strokeWidth = 1;
    canvas.drawLine(
      Offset(0, size.height / 2),
      Offset(size.width, size.height / 2),
      grid,
    );

    // A null reading breaks the line: a gap, never a zero.
    var run = <Offset>[];
    void flush() {
      if (run.length > 1) {
        final path = Path()..moveTo(run.first.dx, run.first.dy);
        for (final p in run.skip(1)) {
          path.lineTo(p.dx, p.dy);
        }
        final area = Path.from(path)
          ..lineTo(run.last.dx, size.height - 3)
          ..lineTo(run.first.dx, size.height - 3)
          ..close();
        canvas.drawPath(area, fill);
        canvas.drawPath(path, line);
      }
      run = [];
    }

    Offset? last;
    for (final s in samples) {
      final v = usageValue(kind, s);
      if (v == null) {
        flush();
      } else {
        last = Offset(x(s.at), y(v));
        run.add(last);
      }
    }
    flush();

    if (warnLevel != null) {
      final wy = y(warnLevel!);
      final dash = Paint()
        ..color = AraColors.accentWarning
        ..strokeWidth = 1.5;
      for (double dx = 0; dx < size.width; dx += 10) {
        canvas.drawLine(
          Offset(dx, wy),
          Offset(math.min(dx + 5, size.width), wy),
          dash,
        );
      }
    }
    if (night && last != null) {
      canvas.drawCircle(last, 5, Paint()..color = AraColors.bgPanel);
      canvas.drawCircle(
        last,
        5,
        Paint()
          ..color = AraColors.accentInfo
          ..style = PaintingStyle.stroke
          ..strokeWidth = 2,
      );
    }
    final c = cursor;
    if (c != null) {
      final cx = x(c);
      canvas.drawLine(
        Offset(cx, 0),
        Offset(cx, size.height),
        Paint()
          ..color = AraColors.textPrimary
          ..strokeWidth = 1,
      );
      for (final s in samples) {
        if (s.at == c && usageValue(kind, s) != null) {
          canvas.drawCircle(
            Offset(cx, y(usageValue(kind, s)!)),
            4,
            Paint()..color = AraColors.textPrimary,
          );
        }
      }
    }
  }

  @override
  bool shouldRepaint(_GraphPainter old) => true;
}
