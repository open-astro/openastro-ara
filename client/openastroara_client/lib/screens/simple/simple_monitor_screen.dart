import 'dart:math' as math;

import 'package:flutter/cupertino.dart' show CupertinoIcons;
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../services/monitor_device_service.dart';
import '../../state/night_mode_state.dart';
import '../../state/simple_mode_state.dart' show companionModeProvider;
import '../../state/simple_messages_state.dart';
import '../../state/simple_monitor_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../widgets/emergency_stop_button.dart';
import '../../widgets/equipment/equipment_glyphs.dart' show EquipmentGlyph;
import '../../widgets/status_indicator.dart';
import 'simple_device_windows.dart' show simpleClockTime, simpleWeatherBreachText;
import 'simple_plan_panel.dart' show simpleRunDuration;
import 'simple_preview_panel.dart' show simpleCountdown;
import 'simple_shell.dart' show kSimpleStopSize, simpleRailScale;

/// Where a tile tap goes: Monitor closes and the shell opens the window or
/// panel that owns the thing (#1724).
enum MonitorDestination { queue, sky, mount, safety, weather, camera, guider, frameInfo }

/// Below this width the grid needs more room than it has: Monitor is offered
/// only from a phone's portrait deck (#1775), and lays its tiles in one
/// column under a verdict on a line of its own.
const double kMonitorMinWidth = 900;

/// The two right columns keep this width; extra width goes to the left two.
const double _kSide = 244;
const double _kGap = 8;

/// The grid's least height: the fixed rows (170 + 108) and room for the
/// tiles above them; below it the grid scrolls.
const double _kGridMinHeight = 560;

/// Monitor (#1724): Simple Mode's full-screen, read-only view of the Session.
/// It renders [monitorProvider] and nothing else; the safety alarm's layer
/// sits above every route, so it still covers this one.
class SimpleMonitorScreen extends ConsumerStatefulWidget {
  const SimpleMonitorScreen({super.key, required this.onOpen});

  /// A tile was tapped: the shell closes Monitor and opens what owns it.
  final ValueChanged<MonitorDestination> onOpen;

  @override
  ConsumerState<SimpleMonitorScreen> createState() => _SimpleMonitorScreenState();
}

class _SimpleMonitorScreenState extends ConsumerState<SimpleMonitorScreen> {
  late final KeepAwake _awake = ref.read(keepAwakeProvider);
  late final MonitorShowing _showing = ref.read(monitorShowingProvider.notifier);

  @override
  void initState() {
    super.initState();
    // Awake only while Monitor shows.
    _awake.set(true);
    // Not during the build that mounts this screen.
    Future.microtask(() => _showing.set(true));
  }

  @override
  void dispose() {
    _awake.set(false);
    Future.microtask(() => _showing.set(false));
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(monitorProvider);
    final clock = ref.watch(monitorClockProvider);
    final phone = ref.watch(companionModeProvider);
    // The iPad's 108 pt Stop; a phone's is the 60 px square of every phone
    // screen (board pick, 2026-11-12, #1775), growing with the text as the
    // deck's does.
    final stop = phone ? kSimpleStopSize * simpleRailScale(context) : 108.0;
    return Scaffold(
      key: const Key('simple-monitor'),
      backgroundColor: AraColors.bgPrimary,
      body: SafeArea(
        child: Padding(
          padding: const EdgeInsets.all(AraSpace.s12),
          child: Stack(
            children: [
              Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  _TopLine(s: s, clock: clock),
                  const SizedBox(height: _kGap),
                  // Frozen, the tiles say Last known with grey dots; no dimming,
                  // which took the last known values under 3:1.
                  Expanded(child: _Grid(s: s, onOpen: widget.onOpen, stop: stop)),
                ],
              ),
              Positioned(
                right: 0,
                bottom: 0,
                width: stop,
                height: stop,
                child: EmergencyStopButton(large: !phone, compact: phone),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// The top line: the verdict, the clock, this device's battery, Night mode
/// and close; on a phone the verdict goes under the rest.
class _TopLine extends ConsumerWidget {
  const _TopLine({required this.s, required this.clock});

  final MonitorSnapshot s;
  final DateTime clock;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = context.l10n;
    final v = s.verdict;
    final (head, reason) = monitorVerdictText(l10n, s, clock);
    final colour = switch (v.status) {
      MonitorStatus.fault => AraColors.errorText,
      MonitorStatus.warning => AraColors.accentWarning,
      _ => AraColors.textPrimary,
    };
    final battery = ref.watch(deviceBatteryProvider).value;
    final night = switch (ref.watch(nightModeProvider)) {
      AsyncData(:final value) => value,
      _ => false,
    };
    final question = v.kind == MonitorVerdictKind.question ? v.question : null;
    final message = v.kind == MonitorVerdictKind.message ? v.message : null;
    final narrow = MediaQuery.sizeOf(context).width < kMonitorMinWidth;
    // Shown whole: a question to answer, or a stop's outcome to read; on a
    // phone every verdict, which two lines cannot hold (#1775).
    final whole = question != null || message != null || narrow;
    // Back, at the top left, as the Sky Album leaves (DESIGN.md: Simple
    // Mode's full-screen pages).
    const back = BackButton(key: Key('simple-monitor-back'), color: AraColors.textSecondary);
    // The title with its icon, as the Sky Album has (#2014).
    final title = Semantics(
      header: true,
      child: Row(mainAxisSize: MainAxisSize.min, children: [
        const Icon(Icons.monitor_heart_outlined, size: 22, color: AraColors.textSecondary),
        const SizedBox(width: AraSpace.s8),
        Flexible(
          child: Text(
            l10n.monitorTitle,
            key: const Key('simple-monitor-title'),
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: kSimpleText.copyWith(fontSize: AraFontSize.simpleReadingLarge, fontWeight: FontWeight.w600),
          ),
        ),
      ]),
    );
    final words = [
      if (_dot(v.status) case final dot?) ...[dot, const SizedBox(width: AraSpace.s12)],
      Expanded(
        // The words scroll past the cap; the answers (or Dismiss) stay
        // fixed under them, always on screen (re-audit 3).
        child: Column(
          mainAxisSize: MainAxisSize.min,
          mainAxisAlignment: MainAxisAlignment.center,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Flexible(
              child: SingleChildScrollView(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    // Announced once per state: the visible line carries a
                    // countdown or an age that would re-announce every second.
                    Semantics(liveRegion: true, label: monitorVerdictAnnouncement(l10n, s, head), child: const SizedBox.shrink()),
                    Text(head,
                        key: const Key('simple-monitor-verdict'),
                        // A question or a stop's outcome shows whole.
                        maxLines: whole ? null : 2,
                        overflow: whole ? null : TextOverflow.ellipsis,
                        style: kSimpleText.copyWith(
                            fontSize: AraFontSize.monitorVerdict, fontWeight: FontWeight.w600, color: colour)),
                    if (reason != null)
                      Text(reason,
                          maxLines: whole ? null : 2,
                          overflow: whole ? null : TextOverflow.ellipsis,
                          style: _dim),
                  ],
                ),
              ),
            ),
            if (message != null)
              Padding(
                padding: const EdgeInsets.only(top: AraSpace.s8, bottom: AraSpace.s8),
                child: TextButton(
                  key: const Key('simple-monitor-dismiss'),
                  onPressed: () => ref.read(simpleMessagesProvider.notifier).dismiss(message.id),
                  child: Text(l10n.pillsDismiss),
                ),
              ),
            // A question's answers: the only controls besides Stop, Night
            // mode and Back. The same actions as its pill, which Monitor
            // doesn't draw.
            if (question != null)
              Padding(
                padding: const EdgeInsets.only(top: AraSpace.s8, bottom: AraSpace.s8),
                child: Wrap(spacing: AraSpace.s8, runSpacing: AraSpace.s8, children: [
                  for (var i = 0; i < question.actions.length; i++)
                    (i == question.actions.length - 1 ? FilledButton.new : TextButton.new)(
                      onPressed: () {
                        ref.read(simpleMessagesProvider.notifier).dismiss(question.id);
                        question.actions[i].onPressed();
                      },
                      child: Text(question.actions[i].label),
                    ),
                ]),
              ),
          ],
        ),
      ),
    ];
    final readings = [
      if (battery != null) ...[
        _Battery(level: battery.level, charging: battery.charging),
        const SizedBox(width: AraSpace.s12),
      ],
      Text(simpleClockTime(clock),
          style: kSimpleText.copyWith(fontSize: AraFontSize.simpleReadingLarge, fontWeight: FontWeight.w300)),
      const SizedBox(width: AraSpace.s8),
      IconButton(
        tooltip: l10n.shellNightMode,
        isSelected: night,
        color: AraColors.textSecondary,
        selectedIcon: const Icon(Icons.dark_mode, color: AraColors.accentInfo),
        icon: const Icon(Icons.dark_mode_outlined),
        onPressed: () => ref.read(nightModeProvider.notifier).set(!night),
      ),
    ];
    // 64 pt, growing with the user's text size up to half the screen; past
    // that it scrolls, so a question's answers stay reachable at 2x text.
    return ConstrainedBox(
      constraints: BoxConstraints(minHeight: 64, maxHeight: math.max(64, MediaQuery.sizeOf(context).height / 2)),
      // On a phone (#1775) the verdict has a line of its own under Back and
      // the readings, which fill a phone's width at 2x text; where they do
      // not fit beside Back (a 360 px phone at 2x), they go under it.
      child: narrow
          ? Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
              Wrap(
                alignment: WrapAlignment.spaceBetween,
                crossAxisAlignment: WrapCrossAlignment.center,
                children: [
                  Row(mainAxisSize: MainAxisSize.min, children: [back, title]),
                  Row(mainAxisSize: MainAxisSize.min, children: readings),
                ],
              ),
              Flexible(child: Row(children: words)),
            ])
          : Row(children: [
              back,
              // A question, a stop's outcome or large text needs the whole line.
              if (question == null && message == null && MediaQuery.textScalerOf(context).scale(1) <= 1.3) ...[
                title,
                const SizedBox(width: AraSpace.s16),
              ] else
                const SizedBox(width: AraSpace.s4),
              ...words,
              const SizedBox(width: AraSpace.s16),
              ...readings,
            ]),
    );
  }
}

class _Battery extends StatelessWidget {
  const _Battery({required this.level, required this.charging});

  final int level;
  final bool charging;

  @override
  Widget build(BuildContext context) {
    // Amber at 20 % or under, while it isn't charging.
    final low = level <= 20 && !charging;
    final c = low ? AraColors.accentWarning : AraColors.textSecondary;
    return Semantics(
      label: context.l10n.monitorBattery(level),
      excludeSemantics: true,
      child: Row(
        key: const Key('simple-monitor-battery'),
        children: [
          Icon(charging ? Icons.battery_charging_full : Icons.battery_std, size: 18, color: c),
          Text('$level %', style: kSimpleText.copyWith(color: c)),
        ],
      ),
    );
  }
}

/// The tiles: two fixed right columns, the left two sharing the rest; on a
/// phone, one column. Which areas show comes from the rig's profile.
class _Grid extends StatelessWidget {
  const _Grid({required this.s, required this.onOpen, required this.stop});

  final MonitorSnapshot s;
  final ValueChanged<MonitorDestination> onOpen;

  /// Stop's side: the phone column's end scrolls clear of it.
  final double stop;

  @override
  Widget build(BuildContext context) {
    final now = _NowTile(s: s, onTap: () => onOpen(MonitorDestination.queue));
    final target = _TargetTile(s: s, onTap: () => onOpen(MonitorDestination.sky));
    final flip = _FlipTile(s: s, onTap: () => onOpen(MonitorDestination.mount));
    final safety = s.safety == null ? null : _SafetyTile(s: s, onTap: () => onOpen(MonitorDestination.safety));
    final weather = s.conditions == null
        ? null
        : _ConditionsTile(
            s: s,
            // The weather's window when a station reports, else the camera's.
            onTap: () => onOpen(
              s.conditions!.weather != null ? MonitorDestination.weather : MonitorDestination.camera,
            ),
          );
    final guider = s.guider == null ? null : _GuiderTile(s: s, onTap: () => onOpen(MonitorDestination.guider));
    final focus = _LastFrameTile(s: s, onTap: () => onOpen(MonitorDestination.frameInfo));
    final night = _NightTile(s: s, onTap: () => onOpen(MonitorDestination.queue));
    const strip = 56.0;

    // A phone (#1775): one column that scrolls, each tile at a fixed height
    // (a graph tile tall enough for its graph, #1875); the end scrolls clear
    // of Stop in the corner.
    if (MediaQuery.sizeOf(context).width < kMonitorMinWidth) {
      return ListView(
        key: const Key('simple-monitor-column'),
        padding: EdgeInsets.only(bottom: stop + _kGap),
        children: [
          for (final (tile, height) in [
            (now, 440.0),
            (target, 220.0),
            (flip, 150.0),
            if (safety != null) (safety, strip),
            if (weather != null) (weather, 340.0),
            if (guider != null) (guider, 240.0),
            (focus, 220.0),
            (night, 130.0),
          ])
            Padding(
              padding: const EdgeInsets.only(bottom: _kGap),
              child: SizedBox(height: height, child: tile),
            ),
        ],
      );
    }

    Widget side = LayoutBuilder(builder: (context, box) {
      final h = box.maxHeight;
      if (safety != null && weather != null) {
        final fr = (h - strip - 2 * _kGap) / 2;
        return Row(children: [
          SizedBox(
              width: _kSide,
              child: Column(children: [
                SizedBox(height: strip + _kGap + fr, child: target),
                const SizedBox(height: _kGap),
                Expanded(child: flip),
              ])),
          const SizedBox(width: _kGap),
          SizedBox(
              width: _kSide,
              child: Column(children: [
                SizedBox(height: strip, child: safety),
                const SizedBox(height: _kGap),
                Expanded(child: weather),
              ])),
        ]);
      }
      if (safety != null) {
        return Column(children: [
          SizedBox(height: strip, child: safety),
          const SizedBox(height: _kGap),
          Expanded(child: target),
          const SizedBox(height: _kGap),
          Expanded(child: flip),
        ]);
      }
      if (weather != null) {
        return Row(children: [
          SizedBox(
              width: _kSide,
              child: Column(children: [
                Expanded(child: target),
                const SizedBox(height: _kGap),
                Expanded(child: flip),
              ])),
          const SizedBox(width: _kGap),
          SizedBox(width: _kSide, child: weather),
        ]);
      }
      return Column(children: [
        Expanded(child: target),
        const SizedBox(height: _kGap),
        SizedBox(height: 150, child: flip),
      ]);
    });

    // Short of room (a question stacked on the top line, large text), the
    // grid keeps its least height and scrolls rather than crushing tiles.
    return LayoutBuilder(
      builder: (context, box) => box.maxHeight >= _kGridMinHeight
          ? _grid(now, side, guider, focus, night)
          : SingleChildScrollView(
              child: SizedBox(height: _kGridMinHeight, child: _grid(now, side, guider, focus, night)),
            ),
    );
  }

  Widget _grid(Widget now, Widget side, Widget? guider, Widget focus, Widget night) => Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Expanded(
          child: Row(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
            Expanded(child: now),
            const SizedBox(width: _kGap),
            SizedBox(width: 2 * _kSide + _kGap, child: side),
          ]),
        ),
        const SizedBox(height: _kGap),
        SizedBox(
          height: 170,
          child: Row(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
            if (guider != null) ...[Expanded(child: guider), const SizedBox(width: _kGap)],
            guider != null ? SizedBox(width: 2 * _kSide + _kGap, child: focus) : Expanded(child: focus),
          ]),
        ),
        const SizedBox(height: _kGap),
        // The timeline leaves Stop its corner.
        SizedBox(height: 108, child: Padding(padding: const EdgeInsets.only(right: 108 + _kGap), child: night)),
      ],
    );
}

/// A healthy tile has no dot, so any dot means something changed.
Widget? _dot(MonitorStatus s) => switch (s) {
      MonitorStatus.changing => const StatusDot(color: AraColors.accentInfo, ring: true),
      MonitorStatus.warning => const StatusDot(color: AraColors.accentWarning),
      MonitorStatus.fault => const StatusDot(color: AraColors.accentError),
      MonitorStatus.off => const StatusDot(color: AraColors.accentDisconnected),
      _ => null,
    };

TextStyle get _dim => kSimpleText.copyWith(color: AraColors.textSecondary);
TextStyle get _big => kSimpleText.copyWith(fontSize: AraFontSize.simpleReadingLarge, fontWeight: FontWeight.w300);

/// One tile: its header (icon, name, the status word in the right corner,
/// dot), then its content, which scrolls inside the tile when larger text no
/// longer fits. Read out as its name, value and status.
class _Tile extends StatelessWidget {
  const _Tile({
    required this.tileKey,
    required this.icon,
    required this.name,
    required this.status,
    this.word,
    this.aside,
    required this.value,
    required this.onTap,
    required this.children,
    this.frozen = false,
  });

  final String tileKey;
  final IconData icon;
  final String name;
  final MonitorStatus status;
  final String? word;

  /// A line in the corner when there is no word and it fits there whole, else
  /// the content's first row. Not read out: [value] holds it.
  final String? aside;

  /// What a screen reader says after the name.
  final String value;
  final VoidCallback onTap;
  final List<Widget> children;
  final bool frozen;

  @override
  Widget build(BuildContext context) {
    final (accent, outline) = switch (status) {
      MonitorStatus.warning => (AraColors.accentWarning, AraColors.accentWarning),
      MonitorStatus.fault => (AraColors.errorText, AraColors.accentError),
      _ => (AraColors.textSecondary, AraColors.border),
    };
    final shown = frozen ? context.l10n.monitorLastKnown : word;
    final wordColour = frozen
        ? AraColors.textSecondary
        : status == MonitorStatus.working
        ? AraColors.accentConnected
        : accent;
    return Semantics(
      button: true,
      label: [name, value, ?shown].join(', '),
      excludeSemantics: true,
      child: Material(
        key: Key('simple-monitor-$tileKey'),
        color: AraColors.tileFill,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(AraRadius.simplePanel),
          side: BorderSide(color: outline, width: outline == AraColors.border ? 1 : 1.5),
        ),
        clipBehavior: Clip.antiAlias,
        child: InkWell(
          onTap: onTap,
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: AraSpace.s12, vertical: 10),
            child: LayoutBuilder(builder: (context, box) {
              // Cut short, an aside loses its end (Tonight's imaged time): the corner takes it only whole.
              final bold = MediaQuery.boldTextOf(context) ? const TextStyle(fontWeight: FontWeight.bold) : null;
              final style = DefaultTextStyle.of(context).style.merge(_dim).merge(bold);
              final scaler = MediaQuery.textScalerOf(context);
              final up = aside != null && shown == null && _textSize(aside!, scaler, style).width <= box.maxWidth * 0.6;
              final corner = up ? aside : shown;
              return SingleChildScrollView(
                child: ConstrainedBox(
                  constraints: BoxConstraints(minHeight: box.maxHeight.isFinite ? box.maxHeight : 0),
                  child: IntrinsicHeight(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        Row(children: [
                          Icon(icon, size: 18, color: accent),
                          const SizedBox(width: 6),
                          // The name takes what the word leaves; the word sits in the right
                          // corner at its own width, shortening only past 60 % of the tile (pt-BR).
                          Expanded(child: Text(name, style: _dim, maxLines: 1, overflow: TextOverflow.ellipsis)),
                          if (corner != null && corner.isNotEmpty) ...[
                            const SizedBox(width: 6),
                            ConstrainedBox(
                              constraints: BoxConstraints(maxWidth: box.maxWidth * 0.6),
                              child: Text(corner,
                                  maxLines: 1, overflow: TextOverflow.ellipsis, style: _dim.copyWith(color: wordColour)),
                            ),
                          ],
                          // Stale numbers get a grey dot, as off gear does.
                          if (_dot(frozen ? MonitorStatus.off : status) case final dot?) ...[const SizedBox(width: 6), dot],
                        ]),
                        // Wrapped, never cut: its own row is there to show it whole.
                        if (aside != null && !up) Text(aside!, style: kSimpleCaption),
                        ...children,
                      ],
                    ),
                  ),
                ),
              );
            }),
          ),
        ),
      ),
    );
  }
}

/// The top line's words: the headline and the reason under it.
(String, String?) monitorVerdictText(AppLocalizations l10n, MonitorSnapshot s, DateTime clock) {
  final v = s.verdict;
  String device(MonitorDevice d) => switch (d) {
        MonitorDevice.mount => l10n.deviceMount,
        MonitorDevice.camera => l10n.deviceCamera,
        MonitorDevice.guider => l10n.deviceGuider,
        MonitorDevice.focuser => l10n.deviceFocuser,
        MonitorDevice.safetyMonitor => l10n.shellSafetyMonitor,
        MonitorDevice.weather => l10n.shellWeather,
      };
  String c(double v) => '${v.toStringAsFixed(1)} °C';
  return switch (v.kind) {
    MonitorVerdictKind.linkLost => (
        l10n.monitorLinkLost(_age(clock.difference(v.at ?? clock))),
        l10n.monitorLinkLostReason,
      ),
    MonitorVerdictKind.deviceDropped => (l10n.monitorDeviceDropped(device(v.device!)), l10n.monitorDeviceDroppedReason),
    MonitorVerdictKind.unsafe => (
        v.breach == null ? l10n.deviceUnsafe : '${l10n.deviceUnsafe} · ${simpleWeatherBreachText(l10n, v.breach!)}',
        l10n.monitorUnsafeReason,
      ),
    MonitorVerdictKind.question => (v.question!.title, v.question!.detail),
    MonitorVerdictKind.message => (v.message!.title, v.message!.detail),
    MonitorVerdictKind.starLost => (l10n.monitorStarLost, l10n.monitorStarLostReason),
    MonitorVerdictKind.lowDisk => (
        l10n.monitorLowDisk('${((v.freeBytes ?? 0) / 1073741824).toStringAsFixed(0)} GB'),
        l10n.monitorLowDiskReason,
      ),
    MonitorVerdictKind.cooler => (
        l10n.monitorCooler(c(v.coolerTargetC ?? 0), c(v.coolerSensorC ?? 0)),
        l10n.monitorCoolerReason,
      ),
    MonitorVerdictKind.flipping => (l10n.deviceMeridianFlip, v.text ?? l10n.monitorFlippingReason),
    MonitorVerdictKind.waitingForFlip => (
        l10n.previewWaitingForFlip(simpleCountdown(v.at!.difference(s.asOf).inSeconds)),
        l10n.monitorWaitingForFlipReason,
      ),
    MonitorVerdictKind.paused => (l10n.monitorPaused, v.text),
    MonitorVerdictKind.waitingForYou => (l10n.monitorWaitingForYou, v.text),
    MonitorVerdictKind.waitingForDark => (
        l10n.monitorWaitingForDark(simpleClockTime(v.at!)),
        l10n.monitorWaitingForDarkReason,
      ),
    MonitorVerdictKind.noPlan => (l10n.monitorNoPlan, l10n.monitorNoPlanReason),
    MonitorVerdictKind.allGood => (
        l10n.monitorAllGood(v.target ?? ''),
        s.night.parkAt == null
            ? l10n.monitorAllGoodReason
            : l10n.monitorAllGoodParks(simpleClockTime(s.night.parkAt!)),
      ),
  };
}

/// What a screen reader hears when the top line changes: the headline,
/// without a countdown or an age that changes every second.
String monitorVerdictAnnouncement(AppLocalizations l10n, MonitorSnapshot s, String head) => switch (s.verdict.kind) {
      MonitorVerdictKind.linkLost => l10n.monitorLinkLostShort,
      MonitorVerdictKind.waitingForFlip => l10n.planWaitingForFlip,
      _ => head,
    };

/// "42 s", "3 min", "1 h 12 min".
String _age(Duration d) {
  final sec = math.max(0, d.inSeconds);
  if (sec < 60) return '$sec s';
  return simpleRunDuration(sec.toDouble());
}

String _n(double? v, [int digits = 1]) => v == null ? '—' : v.toStringAsFixed(digits);

/// A filter name worth showing: a camera with no filter wheel has none.
bool _named(String? f) => f != null && f.trim().isNotEmpty;

class _NowTile extends StatelessWidget {
  const _NowTile({required this.s, required this.onTap});

  final MonitorSnapshot s;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final n = s.now;
    final word = switch (n.word) {
      MonitorNowWord.exposing => l10n.deviceExposing,
      MonitorNowWord.working => l10n.monitorRunning,
      MonitorNowWord.flipping => l10n.monitorFlipping,
      MonitorNowWord.waitingForFlip => l10n.planWaitingForFlip,
      MonitorNowWord.paused => l10n.monitorPaused,
      MonitorNowWord.waitingForYou => l10n.monitorWaitingForYouWord,
      MonitorNowWord.waitingToStart => l10n.monitorWaiting,
      MonitorNowWord.idle || MonitorNowWord.lastKnown => l10n.monitorIdle,
    };
    final task = n.target == null
        ? l10n.monitorNothingRunning
        : [n.target!, if (_named(n.filter)) n.filter!, if (n.subSeconds != null) '${_n(n.subSeconds, 0)} s'].join(' · ');
    // The frame in hand, else a wait counted down, else the state in words.
    final end = n.frameEndsAt ?? n.countdownTo;
    final left = end?.difference(s.asOf).inSeconds;
    final caption = n.frameEndsAt != null
        ? l10n.monitorLeftInFrame
        : n.word == MonitorNowWord.waitingForFlip
        ? l10n.monitorUntilFlip
        : l10n.monitorUntilStart;
    final big = left != null
        ? Column(crossAxisAlignment: CrossAxisAlignment.start, mainAxisSize: MainAxisSize.min, children: [
            FittedBox(
              fit: BoxFit.scaleDown,
              alignment: Alignment.centerLeft,
              child: Text(
                simpleCountdown(left),
                key: const Key('simple-monitor-countdown'),
                style: kSimpleText.copyWith(
                  fontSize: left >= 600 ? AraFontSize.monitorCountdownLong : AraFontSize.monitorCountdown,
                  fontWeight: FontWeight.w200,
                  height: 1,
                ),
              ),
            ),
            Text(caption, style: _dim),
            if (n.frameEndsAt != null && n.frameSeconds != null) ...[
              const SizedBox(height: AraSpace.s8),
              ClipRRect(
                borderRadius: BorderRadius.circular(AraRadius.field),
                child: LinearProgressIndicator(
                  value: (1 - left / n.frameSeconds!).clamp(0.0, 1.0),
                  minHeight: 6,
                  color: AraColors.textSecondary,
                  backgroundColor: AraColors.bgInput,
                ),
              ),
            ],
          ])
        // The state in Monitor's own words, big; the daemon's step under it,
        // small: its text can be a solver's whole log line (user, 2026-11-11).
        : Column(crossAxisAlignment: CrossAxisAlignment.start, mainAxisSize: MainAxisSize.min, children: [
            Text(
              n.word == MonitorNowWord.idle ? l10n.monitorNoPlanBig : word,
              maxLines: 2,
              overflow: TextOverflow.ellipsis,
              style: kSimpleText.copyWith(fontSize: AraFontSize.monitorWord, fontWeight: FontWeight.w300),
            ),
            if (n.word != MonitorNowWord.idle && n.instruction != null && n.instruction!.isNotEmpty)
              Text(n.instruction!, maxLines: 2, overflow: TextOverflow.ellipsis, style: _dim),
          ]);
    // One way to count: the frame in hand, and what is saved (#1875).
    final saved = n.filters.fold<int>(0, (a, f) => a + f.done);
    final sub = n.frame != null && n.frames != null
        ? l10n.monitorFrameOf(n.frame!.clamp(0, n.frames!), n.frames!, simpleRunDuration(n.targetSecondsLeft ?? 0))
        : null;
    // A camera with no filter wheel has one unnamed row: one bar, no name.
    final named = n.filters.any((f) => _named(f.filter));
    return _Tile(
      tileKey: 'now',
      icon: Icons.photo_camera_outlined,
      name: l10n.monitorNow,
      status: n.status,
      word: word,
      value: [task, if (left != null) simpleCountdown(left)].join(', '),
      frozen: s.frozen,
      onTap: onTap,
      children: [
        Text(task, style: kSimpleReading, maxLines: 2, overflow: TextOverflow.ellipsis),
        // A phone's busy tile has no height to spare: with named filters the
        // counts under it say what is saved, so the line is only the frame.
        if (sub != null)
          MediaQuery.sizeOf(context).width < kMonitorMinWidth && named
              ? Text(sub, style: _dim)
              : Row(children: [
                  Expanded(child: Text(sub, style: _dim)),
                  const SizedBox(width: AraSpace.s8),
                  Text(l10n.monitorSaved(saved), style: _dim),
                ]),
        const SizedBox(height: AraSpace.s8),
        // A phone (#1915): Up next goes under the state block, so the countdown
        // and the state word keep the tile's whole width.
        Expanded(
          child: MediaQuery.sizeOf(context).width < kMonitorMinWidth
              ? Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    Expanded(
                      child: Align(alignment: Alignment.centerLeft, child: big),
                    ),
                    const SizedBox(height: AraSpace.s8),
                    _UpNext(s: s, limit: n.filters.length > 4 ? 1 : null),
                  ],
                )
              : Row(
                  crossAxisAlignment: CrossAxisAlignment.center,
                  children: [
                    Expanded(child: big),
                    const SizedBox(width: AraSpace.s16),
                    SizedBox(width: 190, child: _UpNext(s: s)),
                  ],
                ),
        ),
        if (n.filters.isNotEmpty && !named && n.frames != null) ...[
          const SizedBox(height: AraSpace.s8),
          ClipRRect(
            borderRadius: BorderRadius.circular(AraRadius.field),
            child: LinearProgressIndicator(
              value: n.frames == 0 ? 0 : (saved / n.frames!).clamp(0.0, 1.0),
              minHeight: 6,
              color: AraColors.accentInfo,
              backgroundColor: AraColors.bgInput,
            ),
          ),
        ],
        if (named) ...[
          const SizedBox(height: AraSpace.s8),
          MediaQuery.sizeOf(context).width < kMonitorMinWidth
              // A phone (#1942): a count is never scaled below the 14 px floor
              // and a name keeps at least a glyph. Up to four filters wrap, each
              // as wide as its name and count; more take a grid of four columns
              // (two at 2x text), name above count, in two short rows so the
              // tile does not scroll at 1x (the Up next list shrinks to one row).
              ? n.filters.length > 4
                  ? _PhoneFilterGrid(
                      filters: n.filters,
                      cols: (4 / MediaQuery.textScalerOf(context).scale(1)).floor().clamp(1, 4),
                    )
                  : Wrap(
                  spacing: 10,
                  runSpacing: AraSpace.s8,
                  children: [
                    for (final f in n.filters)
                      IntrinsicWidth(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.stretch,
                          children: [
                            Row(
                              children: [
                                Flexible(
                                  child: Text(
                                    f.filter,
                                    maxLines: 1,
                                    overflow: TextOverflow.ellipsis,
                                    style: kSimpleText.copyWith(fontWeight: FontWeight.w600),
                                  ),
                                ),
                                const SizedBox(width: AraSpace.s8),
                                Text('${f.done}/${f.total}', maxLines: 1, style: _dim),
                              ],
                            ),
                            const SizedBox(height: 3),
                            _FilterBar(f),
                          ],
                        ),
                      ),
                  ],
                )
              : Row(
            children: [
              for (final f in n.filters)
                Expanded(
                  child: Padding(
                    padding: const EdgeInsets.only(right: 10),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        // The name keeps its width up to half the cell; the
                        // count gets the rest, after a gap, and shrinks only
                        // when it does not fit (#1915).
                        Table(
                          columnWidths: const {
                            0: MinColumnWidth(
                              IntrinsicColumnWidth(),
                              FractionColumnWidth(0.5),
                            ),
                            1: FlexColumnWidth(),
                          },
                          defaultVerticalAlignment:
                              TableCellVerticalAlignment.middle,
                          children: [
                            TableRow(
                              children: [
                                Text(
                                  f.filter,
                                  maxLines: 1,
                                  overflow: TextOverflow.ellipsis,
                                  style: kSimpleText.copyWith(
                                    fontWeight: FontWeight.w600,
                                  ),
                                ),
                                Padding(
                                  padding: const EdgeInsets.only(
                                    left: AraSpace.s4,
                                  ),
                                  child: FittedBox(
                                    fit: BoxFit.scaleDown,
                                    alignment: Alignment.centerRight,
                                    child: Text(
                                      '${f.done}/${f.total}',
                                      maxLines: 1,
                                      style: _dim,
                                    ),
                                  ),
                                ),
                              ],
                            ),
                          ],
                        ),
                        const SizedBox(height: 3),
                        _FilterBar(f),
                      ],
                    ),
                  ),
                ),
            ],
          ),
        ],
      ],
    );
  }
}

class _PhoneFilterGrid extends StatelessWidget {
  const _PhoneFilterGrid({required this.filters, required this.cols});

  final List<({String filter, int done, int total, bool inHand})> filters;
  final int cols;

  @override
  Widget build(BuildContext context) => Column(
    children: [
      for (var i = 0; i < filters.length; i += cols) ...[
        if (i > 0) const SizedBox(height: AraSpace.s8),
        Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            for (var j = i; j < i + cols; j++) ...[
              if (j > i) const SizedBox(width: 10),
              Expanded(
                child: j >= filters.length
                    ? const SizedBox.shrink()
                    : Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          Text(
                            filters[j].filter,
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                            style: kSimpleText.copyWith(fontWeight: FontWeight.w600),
                          ),
                          Text('${filters[j].done}/${filters[j].total}', maxLines: 1, style: _dim),
                          const SizedBox(height: 3),
                          _FilterBar(filters[j]),
                        ],
                      ),
              ),
            ],
          ],
        ),
      ],
    ],
  );
}

class _FilterBar extends StatelessWidget {
  const _FilterBar(this.f);

  final ({String filter, int done, int total, bool inHand}) f;

  @override
  Widget build(BuildContext context) => ClipRRect(
    borderRadius: BorderRadius.circular(AraRadius.field),
    child: LinearProgressIndicator(
      value: f.total == 0 ? 0 : f.done / f.total,
      minHeight: 6,
      // Finished green, in hand blue.
      color: f.done >= f.total ? AraColors.accentConnected : AraColors.accentInfo,
      backgroundColor: AraColors.bgInput,
    ),
  );
}

class _UpNext extends StatelessWidget {
  const _UpNext({required this.s, this.limit});

  final MonitorSnapshot s;

  /// Rows shown; null shows all. A phone with many filters gives their grid
  /// the height of the rows past the first (#1942).
  final int? limit;

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    return Column(
      key: const Key('simple-monitor-up-next'),
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        // No plan, no rows: no caption over nothing.
        if (s.upNext.isNotEmpty) Text(l10n.monitorUpNext, style: kSimpleCaption),
        for (final u in limit == null ? s.upNext : s.upNext.take(limit!))
          Container(
            constraints: const BoxConstraints(minHeight: 30),
            decoration: const BoxDecoration(border: Border(top: BorderSide(color: AraColors.border))),
            child: Row(children: [
              Expanded(
                child: Text(
                  switch (u.kind) {
                    MonitorUpKind.nextFrame =>
                      '${l10n.monitorUpFrame(u.frame ?? 0)}${_named(u.name) ? ' · ${u.name}' : ''}',
                    MonitorUpKind.meridianFlip => l10n.deviceMeridianFlip,
                    MonitorUpKind.nextTarget => u.name ?? '',
                    MonitorUpKind.park => l10n.monitorUpPark,
                  },
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: kSimpleText,
                ),
              ),
              Text(
                u.at == null ? l10n.monitorThen : simpleClockTime(u.at!),
                style: kSimpleUnit,
              ),
            ]),
          ),
      ],
    );
  }
}

class _TargetTile extends StatelessWidget {
  const _TargetTile({required this.s, required this.onTap});

  final MonitorSnapshot s;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final t = s.target;
    if (t == null) {
      return _Tile(
        tileKey: 'target',
        icon: Icons.my_location,
        name: l10n.monitorTarget,
        status: MonitorStatus.off,
        value: l10n.monitorNoTarget,
        frozen: s.frozen,
        onTap: onTap,
        children: [Text(l10n.monitorNoTarget, style: _dim)],
      );
    }
    final lines = [
      if (t.imagingTo != null) l10n.monitorImagingTo(simpleClockTime(t.imagingTo!)),
      if (t.maxAltitudeDeg != null) l10n.monitorMaxAltitude(_n(t.maxAltitudeDeg, 0)),
    ].join(' · ');
    final lines2 = [
      if (t.transit != null) l10n.monitorTransits(simpleClockTime(t.transit!)),
      if (t.moonPct != null) l10n.monitorMoon(t.moonPct!),
    ].join(' · ');
    return _Tile(
      tileKey: 'target',
      icon: Icons.my_location,
      name: l10n.monitorTarget,
      status: t.status,
      word: switch (t.word) {
        MonitorTargetWord.tracking => l10n.monitorTracking,
        MonitorTargetWord.queued => l10n.monitorQueued,
        MonitorTargetWord.parked => l10n.monitorParked,
      },
      value: [t.name, lines].join(', '),
      frozen: s.frozen,
      onTap: onTap,
      children: [
        Text.rich(TextSpan(text: t.name, style: kSimpleReading, children: [
          if (t.longName != null) TextSpan(text: '  ${t.longName}', style: _dim),
        ])),
        if (lines.isNotEmpty) Text(lines, style: _dim),
        if (lines2.isNotEmpty) Text(lines2, style: _dim),
        if (t.curve.length > 1) ...[
          Expanded(
            child: Padding(
              padding: const EdgeInsets.only(top: AraSpace.s4),
              child: CustomPaint(
                key: const Key('simple-monitor-altitude'),
                painter: _AltitudePainter(
                  curve: t.curve,
                  start: s.night.darkStart!,
                  end: s.night.darkEnd!,
                  now: s.asOf,
                  spanStart: t.spanStart,
                  spanEnd: t.spanEnd,
                  scaler: MediaQuery.textScalerOf(context),
                ),
              ),
            ),
          ),
          if (t.spanStart != null && t.spanEnd != null)
            Text(l10n.monitorImagingSpan(simpleClockTime(t.spanStart!), simpleClockTime(t.spanEnd!)), style: kSimpleUnit),
        ],
      ],
    );
  }
}

class _FlipTile extends StatelessWidget {
  const _FlipTile({required this.s, required this.onTap});

  final MonitorSnapshot s;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final f = s.flip;
    final left = f.at?.difference(s.asOf).inSeconds;
    final (status, word, big, sub) = switch (f.state) {
      'due_at' when left != null => (
          MonitorStatus.normal,
          l10n.monitorFlipAutomatic,
          left >= 3600 ? simpleRunDuration(left.toDouble()) : simpleCountdown(left),
          l10n.monitorFlipAt(simpleClockTime(f.at!)),
        ),
      'waiting_for_exposure' => (MonitorStatus.changing, l10n.monitorFlipComingUp, null, l10n.monitorFlipWaitsForFrame),
      'waiting_for_meridian' => (MonitorStatus.changing, l10n.planWaitingForFlip, left == null ? null : simpleCountdown(left), l10n.monitorUntilFlip),
      'flipping' => (MonitorStatus.working, l10n.deviceMoving, null, f.step),
      'auto_flip_off' => (MonitorStatus.off, null, null, l10n.monitorFlipOff),
      'none_tonight' => (MonitorStatus.normal, null, null, l10n.monitorFlipNone),
      _ => (MonitorStatus.off, null, null, l10n.monitorNoPlanRunning),
    };
    return _Tile(
      tileKey: 'flip',
      icon: Icons.swap_horiz,
      name: l10n.deviceMeridianFlip,
      status: status,
      word: word,
      value: [?big, ?sub].join(', '),
      frozen: s.frozen,
      onTap: onTap,
      children: [
        if (big != null) Text(big, key: const Key('simple-monitor-flip-countdown'), style: _big),
        if (sub != null) Text(sub, style: big == null ? kSimpleText : _dim),
      ],
    );
  }
}

class _SafetyTile extends StatelessWidget {
  const _SafetyTile({required this.s, required this.onTap});

  final MonitorSnapshot s;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final safety = s.safety!;
    // The same status colours and stale handling as every other tile.
    final word = s.frozen
        ? l10n.monitorLastKnown
        : switch (safety.safe) {
            true => l10n.deviceSafe,
            false => l10n.deviceUnsafe,
            null => l10n.monitorDisconnected,
          };
    final since = safety.since == null || s.frozen
        ? null
        : l10n.monitorSince(simpleClockTime(safety.since!), _age(s.asOf.difference(safety.since!)));
    final colour = s.frozen
        ? AraColors.textSecondary
        : switch (safety.status) {
            MonitorStatus.normal => AraColors.accentConnected,
            MonitorStatus.fault => AraColors.errorText,
            MonitorStatus.warning => AraColors.accentWarning,
            _ => AraColors.textSecondary,
          };
    return Semantics(
      button: true,
      label: [l10n.shellSafetyMonitor, word, ?since].join(', '),
      excludeSemantics: true,
      child: Material(
        key: const Key('simple-monitor-safety'),
        color: AraColors.tileFill,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(AraRadius.simplePanel),
          side: BorderSide(
            color: switch (safety.status) {
              MonitorStatus.warning => AraColors.accentWarning,
              MonitorStatus.fault => AraColors.accentError,
              _ => AraColors.border,
            },
            width: safety.status == MonitorStatus.normal ? 1 : 1.5,
          ),
        ),
        clipBehavior: Clip.antiAlias,
        child: InkWell(
          onTap: onTap,
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: AraSpace.s12),
            child: Row(children: [
              Icon(Icons.shield_outlined, size: 22, color: colour),
              const SizedBox(width: AraSpace.s8),
              Flexible(
                child: Text(word,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: kSimpleText.copyWith(
                        fontWeight: FontWeight.w600, color: colour == AraColors.accentConnected ? null : colour)),
              ),
              const SizedBox(width: AraSpace.s8),
              if (since != null)
                Expanded(child: Text(since, textAlign: TextAlign.right, maxLines: 1, overflow: TextOverflow.ellipsis, style: kSimpleCaption)),
            ]),
          ),
        ),
      ),
    );
  }
}

/// Conditions: the weather station's readings and a "Camera and focuser" group
/// of temperatures, each only when its gear is in the profile (#2014).
class _ConditionsTile extends StatelessWidget {
  const _ConditionsTile({required this.s, required this.onTap});

  final MonitorSnapshot s;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final c0 = s.conditions!;
    final w = c0.weather;
    final rig = c0.rig;
    final cells =
        w?.cells ??
        const <({MonitorWeatherCell cell, double value, double? overLimit})>[];
    const compass = ['N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW'];
    (String, String, String) cell(
      ({MonitorWeatherCell cell, double value, double? overLimit}) c,
    ) => switch (c.cell) {
      MonitorWeatherCell.cloud => (_n(c.value, 0), '%', l10n.monitorCloud),
      MonitorWeatherCell.skyQuality => (
        _n(c.value, 2),
        '',
        l10n.monitorSkyQuality,
      ),
      MonitorWeatherCell.air => (_n(c.value), '°C', l10n.monitorAir),
      MonitorWeatherCell.dewMargin => (_n(c.value), '°', l10n.monitorDewMargin),
      MonitorWeatherCell.humidity => (
        _n(c.value, 0),
        '%',
        l10n.monitorHumidity,
      ),
      MonitorWeatherCell.rain => (_n(c.value), 'mm/h', l10n.monitorRain),
      MonitorWeatherCell.wind => (
        _n(c.value, 0),
        'km/h',
        [
          l10n.monitorWind,
          if (w?.windDeg != null)
            compass[((w!.windDeg! % 360) / 45).round() % 8],
          if (c.overLimit != null) l10n.monitorLimit(_n(c.overLimit, 0)),
          if (w?.gustKmh != null) l10n.monitorGusts(_n(w!.gustKmh, 0)),
        ].join(' · '),
      ),
    };
    // Camera and focuser: only what the rig reports.
    final gear = <(String, String, String)>[
      if (rig?.sensorC != null)
        (
          _n(rig!.sensorC),
          '°C',
          rig.sensorTargetC == null
              ? l10n.monitorSensor
              : '${l10n.monitorSensor} · ${_n(rig.sensorTargetC, 0)}',
        ),
      if (rig?.coolerPowerPct != null)
        (_n(rig!.coolerPowerPct, 0), '%', l10n.monitorCoolerPower),
      if (rig?.focuserC != null)
        (_n(rig!.focuserC), '°C', l10n.monitorFocuserTemp),
      if (rig?.autofocusAt case final at?)
        rig!.tempSinceAutofocus == null
            ? ('—', '', l10n.monitorAutofocusAt(simpleClockTime(at)))
            : (
                '${rig.tempSinceAutofocus! >= 0 ? '+' : '−'}${rig.tempSinceAutofocus!.abs().toStringAsFixed(1)}',
                '°C',
                l10n.monitorAutofocusAt(simpleClockTime(at)),
              ),
    ];
    // The approved mock's cell: an 18 px reading over a 12 px label, both at
    // a line height of 1.2, so seven weather readings and the whole group fit.
    Widget reading(
      String v,
      String unit,
      String label, {
      Widget? icon,
      Color? colour,
    }) => Column(
      mainAxisAlignment: MainAxisAlignment.center,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            if (icon != null) ...[icon, const SizedBox(width: 6)],
            Flexible(
              child: Text.rich(
                TextSpan(
                  text: v,
                  style: kSimpleReading.copyWith(
                    fontSize: AraFontSize.monitorCell,
                    height: 1.2,
                    color: colour,
                  ),
                  children: [TextSpan(text: ' $unit', style: kSimpleUnit)],
                ),
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
              ),
            ),
          ],
        ),
        Text(
          label,
          maxLines: 2,
          overflow: TextOverflow.ellipsis,
          style: kSimpleCaption.copyWith(height: 1.2, color: colour),
        ),
      ],
    );
    return _Tile(
      tileKey: 'conditions',
      icon: Icons.cloud_outlined,
      name: l10n.monitorConditions,
      status: w?.status ?? MonitorStatus.normal,
      word: w?.status == MonitorStatus.warning ? l10n.monitorPastLimit : null,
      value: [
        for (final c in cells) '${cell(c).$3} ${cell(c).$1} ${cell(c).$2}',
        for (final g in gear) '${g.$3} ${g.$1} ${g.$2}',
      ].join(', '),
      frozen: s.frozen,
      onTap: onTap,
      children: [
        const SizedBox(height: AraSpace.s4),
        // One flex child, so each row keeps its own height in the tile's
        // IntrinsicHeight (flex rows would all take the tallest one's) and
        // spare room spreads between them.
        if (cells.isNotEmpty)
          Expanded(
            child: Column(
              mainAxisAlignment: MainAxisAlignment.spaceEvenly,
              children: [
                // An odd last reading takes the whole row: the wind's
                // direction and gusts stay on one line.
                for (final row in _pairs(cells))
                  Row(
                    children: [
                      for (final c in row)
                        Expanded(
                          child: reading(
                            cell(c).$1,
                            cell(c).$2,
                            cell(c).$3,
                            // The Weather window's icon in front of each reading.
                            icon: _weatherIcon(
                              c.cell,
                              c.overLimit != null
                                  ? AraColors.accentWarning
                                  : AraColors.textSecondary,
                            ),
                            colour: c.overLimit != null
                                ? AraColors.accentWarning
                                : null,
                          ),
                        ),
                    ],
                  ),
              ],
            ),
          ),
        if (gear.isNotEmpty) ...[
          Container(
            margin: const EdgeInsets.only(top: AraSpace.s4),
            padding: const EdgeInsets.only(top: AraSpace.s4),
            decoration: const BoxDecoration(
              border: Border(top: BorderSide(color: AraColors.border)),
            ),
            child: Text(l10n.monitorCameraAndFocuser, style: kSimpleCaption),
          ),
          // At their own height, under the weather.
          for (final row in _pairs(gear))
            Row(
              children: [
                for (final g in row) Expanded(child: reading(g.$1, g.$2, g.$3)),
              ],
            ),
        ],
      ],
    );
  }
}

/// The icon the Weather window shows for a reading (simple_device_windows).
Widget _weatherIcon(MonitorWeatherCell cell, Color color) => switch (cell) {
      MonitorWeatherCell.cloud => Icon(Icons.cloud_outlined, size: 22, color: color),
      MonitorWeatherCell.skyQuality => Icon(Icons.auto_awesome_outlined, size: 22, color: color),
      MonitorWeatherCell.air => Icon(Icons.thermostat, size: 22, color: color),
      MonitorWeatherCell.dewMargin => EquipmentGlyph.dewPoint(size: 22, color: color),
      MonitorWeatherCell.humidity => Icon(Icons.water_drop_outlined, size: 22, color: color),
      MonitorWeatherCell.rain => Icon(CupertinoIcons.cloud_rain, size: 22, color: color),
      MonitorWeatherCell.wind => Icon(Icons.air, size: 22, color: color),
    };

/// Two readings a row, as the Monitor mock has them.
List<List<T>> _pairs<T>(List<T> items) => [
      for (var i = 0; i < items.length; i += 2) items.sublist(i, i + 2 > items.length ? items.length : i + 2),
    ];

class _GuiderTile extends StatelessWidget {
  const _GuiderTile({required this.s, required this.onTap});

  final MonitorSnapshot s;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final g = s.guider!;
    final narrow = MediaQuery.sizeOf(context).width < kMonitorMinWidth;
    Widget reading(String label, double? v) => Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text.rich(TextSpan(text: _n(v, 2), style: _big, children: [TextSpan(text: ' ″', style: kSimpleUnit)])),
          Text(label, style: kSimpleUnit),
        ]);
    return _Tile(
      tileKey: 'guider',
      icon: Icons.track_changes,
      name: l10n.deviceGuider,
      status: g.status,
      word: switch (g.word) {
        MonitorGuiderWord.guiding => l10n.shellGuiding,
        MonitorGuiderWord.starLost => l10n.monitorStarLostWord,
        MonitorGuiderWord.disconnected => l10n.monitorDisconnected,
        MonitorGuiderWord.working => l10n.monitorRunning,
        MonitorGuiderWord.idle => l10n.monitorIdle,
      },
      value: 'RMS ${_n(g.rms, 2)}″, RA ${_n(g.ra, 2)}″, Dec ${_n(g.dec, 2)}″',
      frozen: s.frozen,
      onTap: onTap,
      children: [
        // The readings, then the live trace on a fixed scale beside the whole
        // session's RMS, one point per saved frame (#1875); on a phone the
        // graphs go under the readings.
        Expanded(
          child: Flex(
            direction: narrow ? Axis.vertical : Axis.horizontal,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            spacing: AraSpace.s12,
            children: [
              SizedBox(
                width: narrow ? null : 220,
                child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Row(children: [
                    Expanded(child: reading('RMS', g.rms)),
                    Expanded(child: reading('RA', g.ra)),
                    Expanded(child: reading('Dec', g.dec)),
                  ]),
                  const SizedBox(height: AraSpace.s4),
                  Wrap(spacing: AraSpace.s12, crossAxisAlignment: WrapCrossAlignment.center, children: [
                    _Key(AraColors.accentInfo, 'RA'),
                    _Key(AraColors.errorText, 'Dec', dashed: true),
                    Text(l10n.monitorFixedScale, style: kSimpleUnit),
                    if (g.rmsPerFrame.length > 1) Text(l10n.monitorRmsPerFrame, style: kSimpleUnit),
                  ]),
                  if (!narrow) const Spacer(),
                  if (g.lastDither != null) Text(l10n.monitorLastDither(simpleClockTime(g.lastDither!)), style: _dim),
                ]),
              ),
              Expanded(
                child: Row(crossAxisAlignment: CrossAxisAlignment.stretch, spacing: AraSpace.s12, children: [
                  Expanded(
                    child: CustomPaint(
                      key: const Key('simple-monitor-trace'),
                      painter: _TracePainter(g.trace, MediaQuery.textScalerOf(context)),
                    ),
                  ),
                  if (g.rmsPerFrame.length > 1)
                    Expanded(
                      child: CustomPaint(
                        key: const Key('simple-monitor-rms-chart'),
                        painter: _SeriesPainter(
                          scaler: MediaQuery.textScalerOf(context),
                          values: g.rmsPerFrame,
                          lo: 0,
                          ticks: const [1, 2],
                          decimals: 0,
                        ),
                      ),
                    ),
                ]),
              ),
            ],
          ),
        ),
      ],
    );
  }
}

/// "HFR +18 % since session start", the flag of a reading that moved the
/// wrong way (#1875).
class _Flag extends StatelessWidget {
  const _Flag(this.text);

  final String text;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.symmetric(horizontal: AraSpace.s8),
    decoration: BoxDecoration(border: Border.all(color: AraColors.accentWarning), borderRadius: BorderRadius.circular(AraRadius.pill)),
    child: Text(text, style: kSimpleUnit.copyWith(color: AraColors.accentWarning, fontWeight: FontWeight.w600)),
  );
}

String _flagText(AppLocalizations l10n, String metric, MonitorDeviation d) =>
    l10n.monitorChangeSinceStart(metric, '${d.pct >= 0 ? '+' : '−'}${d.pct.abs().round()}');

/// A graph key entry: a short line (dashed or solid) and its name.
class _Key extends StatelessWidget {
  const _Key(this.colour, this.label, {this.dashed = false});

  final Color colour;
  final String label;
  final bool dashed;

  @override
  Widget build(BuildContext context) => Row(mainAxisSize: MainAxisSize.min, children: [
    CustomPaint(size: const Size(18, 10), painter: _SwatchPainter(colour, dashed)),
    const SizedBox(width: AraSpace.s4),
    Flexible(child: Text(label, maxLines: 1, overflow: TextOverflow.ellipsis, style: kSimpleUnit)),
  ]);
}

class _SwatchPainter extends CustomPainter {
  _SwatchPainter(this.colour, this.dashed);

  final Color colour;
  final bool dashed;

  @override
  void paint(Canvas canvas, Size size) {
    final paint = Paint()
      ..color = colour
      ..strokeWidth = 1.5;
    _line(canvas, Offset(0, size.height / 2), Offset(size.width, size.height / 2), paint, dashed: dashed);
  }

  @override
  bool shouldRepaint(_SwatchPainter old) => old.colour != colour || old.dashed != dashed;
}

/// The saved frames of the session: HFR, and stars with the sky background,
/// each a graph of one point per frame flagged against the session's own
/// start (#1875).
class _LastFrameTile extends StatelessWidget {
  const _LastFrameTile({required this.s, required this.onTap});

  final MonitorSnapshot s;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final f = s.lastFrame;
    final last = f.last;
    final hfrDev = f.hfrDeviation, starsDev = f.starsDeviation;
    String? flag(String metric, MonitorDeviation? d) => d?.flagged ?? false ? _flagText(l10n, metric, d!) : null;
    final hfrFlag = flag(l10n.monitorHfr, hfrDev);
    final starsFlags = [?flag(l10n.monitorStars, starsDev), ?flag(l10n.monitorSkyBackground, f.backgroundDeviation)];
    final flags = [?hfrFlag, ...starsFlags];
    final hfr = [for (final x in f.frames) ?x.hfr];
    final stars = [for (final x in f.frames) ?x.stars?.toDouble()];
    final sky = [for (final x in f.frames) ?x.background];
    final detail = [
      if (last?.background != null) '${l10n.monitorSkyBackground} ${_n(last!.background, 0)}',
      if (last?.clippedShare != null) '${l10n.monitorClippedStars} ${_n(last!.clippedShare! * 100)} %',
    ].join(' · ');
    // [guide] is where the flag starts: 15 % off the start, the wrong way.
    Widget graph(String id, List<double> values, MonitorDeviation? dev, double? guide, int decimals, [List<double>? own]) =>
        values.length > 1
            ? Expanded(
                child: CustomPaint(
                  key: Key('simple-monitor-$id-chart'),
                  size: Size.infinite,
                  painter: _SeriesPainter(
                    scaler: MediaQuery.textScalerOf(context),
                    values: values,
                    own: own,
                    guide: guide,
                    colour: dev?.flagged ?? false ? AraColors.accentWarning : AraColors.textPrimary,
                    decimals: decimals,
                    lastLabel: '${values.length}',
                  ),
                ),
              )
            : const Spacer();
    return _Tile(
      tileKey: 'last-frame',
      icon: Icons.insights,
      name: l10n.monitorLastFrame,
      status: flags.isEmpty ? MonitorStatus.normal : MonitorStatus.warning,
      word: flags.isEmpty ? null : l10n.monitorDrifting,
      value: [
        '${l10n.monitorHfr} ${_n(last?.hfr, 2)}',
        '${l10n.monitorStars} ${last?.stars ?? '—'}',
        if (detail.isNotEmpty) detail,
        ...flags,
      ].join(', '),
      frozen: s.frozen,
      onTap: onTap,
      children: [
        Expanded(
          child: Row(crossAxisAlignment: CrossAxisAlignment.stretch, spacing: AraSpace.s16, children: [
            Expanded(
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Wrap(crossAxisAlignment: WrapCrossAlignment.center, spacing: AraSpace.s8, children: [
                  Text(_n(last?.hfr, 2), style: _big),
                  Text(hfrDev == null ? l10n.monitorHfr : '${l10n.monitorHfr} · ${l10n.monitorStartedAt(_n(hfrDev.start, 2))}', style: kSimpleUnit),
                  if (hfrFlag != null) _Flag(hfrFlag),
                ]),
                graph('hfr', hfr, hfrDev, hfrDev == null ? null : hfrDev.start * (1 + kMonitorDeviationPct / 100), 1),
              ]),
            ),
            Expanded(
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Wrap(crossAxisAlignment: WrapCrossAlignment.center, spacing: AraSpace.s8, children: [
                  Text(last?.stars?.toString() ?? '—', style: _big),
                  Text(l10n.monitorStars, style: kSimpleUnit),
                  if (detail.isNotEmpty) Text(detail, style: kSimpleUnit),
                  for (final t in starsFlags) _Flag(t),
                ]),
                graph('stars', stars, starsDev, starsDev == null ? null : starsDev.start * (1 - kMonitorDeviationPct / 100), 0,
                    sky.length == stars.length ? sky : null),
                Wrap(spacing: AraSpace.s12, children: [
                  _Key(AraColors.textPrimary, l10n.monitorStars),
                  _Key(AraColors.textSecondary, l10n.monitorSkyOwnScale, dashed: true),
                ]),
              ]),
            ),
          ]),
        ),
      ],
    );
  }
}

class _NightTile extends StatelessWidget {
  const _NightTile({required this.s, required this.onTap});

  final MonitorSnapshot s;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final n = s.night;
    final start = n.darkStart, end = n.darkEnd;
    final line = start == null || end == null
        ? l10n.monitorNoDark
        : l10n.monitorDarkSpan(simpleClockTime(start), simpleClockTime(end), simpleRunDuration(n.imagedSeconds));
    return _Tile(
      tileKey: 'night',
      icon: Icons.nights_stay_outlined,
      name: l10n.monitorTonight,
      status: MonitorStatus.normal,
      // In the corner when it fits whole, leaving the timeline room for labels.
      aside: line,
      // The timeline is painted: a screen reader hears its blocks and markers.
      value: [
        line,
        for (final b in n.blocks) '${b.name} ${simpleClockTime(b.start)}–${simpleClockTime(b.end)}',
        if (n.flipAt != null) '${l10n.deviceMeridianFlip} ${simpleClockTime(n.flipAt!)}',
        if (n.parkAt != null) '${l10n.monitorUpPark} ${simpleClockTime(n.parkAt!)}',
      ].join(', '),
      frozen: s.frozen,
      onTap: onTap,
      children: [
        if (start != null && end != null)
          Expanded(
            child: CustomPaint(
              key: const Key('simple-monitor-timeline'),
              painter: _NightPainter(
                n,
                s.asOf,
                MediaQuery.textScalerOf(context),
                flipLabel: n.flipAt == null ? null : l10n.monitorFlipPin(simpleClockTime(n.flipAt!)),
                parkLabel: n.parkAt == null ? null : l10n.monitorParkPin(simpleClockTime(n.parkAt!)),
              ),
            ),
          ),
      ],
    );
  }
}

/// A dashed line from [a] to [b]: 4 px on, 4 px off.
void _line(Canvas canvas, Offset a, Offset b, Paint paint, {bool dashed = false}) {
  if (!dashed) return canvas.drawLine(a, b, paint);
  final len = (b - a).distance;
  if (len == 0) return;
  final dir = (b - a) / len;
  for (var d = 0.0; d < len; d += 8) {
    canvas.drawLine(a + dir * d, a + dir * math.min(d + 4, len), paint);
  }
}

/// Axis text at Simple Mode's caption size, scaled with the user's text:
/// [at] is its left, centre or right edge, by [align]; its top, or its middle
/// when [middle]. Left out when it would not fit inside [size]'s height, so
/// large text on a short canvas never paints over the tile's other text.
void _text(
  Canvas canvas,
  Size size,
  String t,
  Offset at,
  TextScaler scaler, {
  TextAlign align = TextAlign.left,
  bool middle = false,
}) {
  final tp = _axisText(t, scaler);
  final x = switch (align) {
    TextAlign.right || TextAlign.end => at.dx - tp.width,
    TextAlign.center => at.dx - tp.width / 2,
    _ => at.dx,
  };
  final y = middle ? at.dy - tp.height / 2 : at.dy;
  if (y >= 0 && y + tp.height <= size.height) tp.paint(canvas, Offset(x, y));
  tp.dispose();
}

TextPainter _axisText(String t, TextScaler scaler, [TextStyle style = kSimpleCaption]) => TextPainter(
  text: TextSpan(text: t, style: style),
  textScaler: scaler,
  textDirection: TextDirection.ltr,
  maxLines: 1,
)..layout();

/// The size [t] takes in [style], by default as [_text] paints it.
Size _textSize(String t, TextScaler scaler, [TextStyle style = kSimpleCaption]) {
  final tp = _axisText(t, scaler, style);
  final size = tp.size;
  tp.dispose();
  return size;
}

/// Tonight's timeline: one block per Queue Target with progress under the
/// one in hand, the flip and park markers with their times above them, hour
/// marks under the bar, and a line at the current time.
class _NightPainter extends CustomPainter {
  _NightPainter(this.n, this.now, this.scaler, {this.flipLabel, this.parkLabel});

  final MonitorNight n;
  final DateTime now;
  final TextScaler scaler;
  final String? flipLabel, parkLabel;

  @override
  void paint(Canvas canvas, Size size) {
    final start = n.darkStart!, end = n.darkEnd!;
    double x(DateTime t) =>
        (t.difference(start).inSeconds / math.max(1, end.difference(start).inSeconds)).clamp(0.0, 1.0) * size.width;
    final top = _textSize('0', scaler).height + 4;
    const h = 24.0;
    // Hour marks.
    final firstHour = DateTime(start.year, start.month, start.day, start.hour).add(const Duration(hours: 1));
    for (var t = firstHour; t.isBefore(end); t = t.add(const Duration(hours: 1))) {
      canvas.drawLine(Offset(x(t), top + h), Offset(x(t), top + h + 3), Paint()..color = AraColors.textSecondary);
      _text(canvas, size, t.hour.toString().padLeft(2, '0'), Offset(x(t), top + h + 4), scaler, align: TextAlign.center);
    }
    for (final b in n.blocks) {
      final r = Rect.fromLTRB(x(b.start), top, math.max(x(b.start) + 2, x(b.end) - 2), top + h);
      final rr = RRect.fromRectAndRadius(r, const Radius.circular(AraRadius.field));
      canvas.save();
      canvas.clipRRect(rr);
      canvas.drawRRect(rr, Paint()..color = AraColors.bgInput);
      if (b.progress != null) {
        canvas.drawRect(Rect.fromLTWH(r.left, r.bottom - 3, r.width * b.progress!, 3), Paint()..color = AraColors.accentInfo);
      }
      final label = TextPainter(
        text: TextSpan(text: b.name, style: kSimpleCaption.copyWith(color: AraColors.textPrimary)),
        textScaler: scaler,
        textDirection: TextDirection.ltr,
        maxLines: 1,
      )..layout();
      label.paint(canvas, Offset(r.left + 8, r.center.dy - label.height / 2));
      label.dispose();
      canvas.restore();
    }
    for (final (t, icon, label) in [
      (n.flipAt, Icons.swap_horiz, flipLabel),
      (n.parkAt, Icons.local_parking, parkLabel),
    ]) {
      if (t == null) continue;
      final c = Offset(x(t), top + h / 2);
      canvas.drawCircle(c, 11, Paint()..color = AraColors.bgPrimary);
      canvas.drawCircle(
        c,
        11,
        Paint()
          ..color = AraColors.textSecondary
          ..style = PaintingStyle.stroke,
      );
      final glyph = TextPainter(
        text: TextSpan(
          text: String.fromCharCode(icon.codePoint),
          style: TextStyle(
            fontFamily: icon.fontFamily,
            package: icon.fontPackage,
            fontSize: AraFontSize.simpleCaption + 2,
            color: AraColors.textPrimary,
          ),
        ),
        textDirection: TextDirection.ltr,
      )..layout();
      glyph.paint(canvas, c - Offset(glyph.width / 2, glyph.height / 2));
      glyph.dispose();
      if (label != null) {
        _text(canvas, size, label, Offset(c.dx.clamp(30.0, size.width - 30), 0), scaler, align: TextAlign.center);
      }
    }
    canvas.drawRect(Rect.fromLTWH(x(now) - 1, top - 4, 2, h + 8), Paint()..color = AraColors.textPrimary);
  }

  @override
  bool shouldRepaint(_NightPainter old) => true;
}

/// A per-frame trend: [lo] and [hi] fixed, else the values' own range with a
/// minimum span so sensor noise doesn't look dramatic. [guide] is a dashed
/// deviation line, [own] a second series on its own scale, the last point a
/// dot.
class _SeriesPainter extends CustomPainter {
  _SeriesPainter({
    required this.scaler,
    required this.values,
    this.lo,
    this.ticks = const [],
    this.guide,
    this.own,
    this.colour = AraColors.textPrimary,
    this.decimals = 1,
    this.lastLabel,
  });

  final TextScaler scaler;
  final List<double> values;
  final double? lo, guide;
  final List<double> ticks;
  final List<double>? own;
  final Color colour;
  final int decimals;

  /// The x axis' right label (the frame count); the left is "1".
  final String? lastLabel;

  @override
  void paint(Canvas canvas, Size size) {
    if (values.length < 2 || size.isEmpty) return;
    final axis = lastLabel == null ? 0.0 : _textSize('1', scaler).height;
    final left = [30.0, for (final t in ticks) _textSize(t.toStringAsFixed(decimals), scaler).width + 6].reduce(math.max);
    final ph = size.height - axis, pw = size.width - left;
    if (ph <= 0) return;
    final all = [...values, ?guide];
    final dataLo = all.reduce(math.min), dataHi = all.reduce(math.max);
    final mid = (dataHi + dataLo) / 2;
    final span = math.max(dataHi - dataLo, math.max(mid.abs() * 0.1, 1e-6));
    final l = lo ?? mid - span * 0.6, h = mid + span * 0.6;
    double y(double v) => ph * (1 - (v - l) / (h - l)).clamp(0.0, 1.0);
    Offset at(int i, double v, int n) => Offset(left + i / (n - 1) * pw, y(v));
    final grid = Paint()..color = AraColors.border;
    for (final t in ticks.where((t) => t > l && t < h)) {
      canvas.drawLine(Offset(left, y(t)), Offset(size.width, y(t)), grid);
      _text(canvas, size, t.toStringAsFixed(decimals), Offset(left - 4, y(t)), scaler, align: TextAlign.right, middle: true);
    }
    if (guide != null) {
      _line(
        canvas,
        Offset(left, y(guide!)),
        Offset(size.width, y(guide!)),
        Paint()..color = AraColors.accentWarning,
        dashed: true,
      );
    }
    if (own != null && own!.length > 1) {
      final lo2 = own!.reduce(math.min), hi2 = math.max(own!.reduce(math.max), own!.reduce(math.min) + 1e-6);
      final path = Path();
      for (var i = 0; i < own!.length; i++) {
        final p = Offset(left + i / (own!.length - 1) * pw, ph * (0.9 - (own![i] - lo2) / (hi2 - lo2) * 0.8));
        i == 0 ? path.moveTo(p.dx, p.dy) : path.lineTo(p.dx, p.dy);
      }
      for (final m in path.computeMetrics()) {
        for (var d = 0.0; d < m.length; d += 8) {
          canvas.drawPath(
            m.extractPath(d, math.min(d + 4, m.length)),
            Paint()
              ..color = AraColors.textSecondary
              ..style = PaintingStyle.stroke
              ..strokeWidth = 1.2,
          );
        }
      }
    }
    final path = Path();
    for (var i = 0; i < values.length; i++) {
      final p = at(i, values[i], values.length);
      i == 0 ? path.moveTo(p.dx, p.dy) : path.lineTo(p.dx, p.dy);
    }
    canvas.drawPath(
      path,
      Paint()
        ..color = colour
        ..style = PaintingStyle.stroke
        ..strokeWidth = 1.5,
    );
    canvas.drawCircle(at(values.length - 1, values.last, values.length), 3.5, Paint()..color = colour);
    if (lastLabel != null) {
      _text(canvas, size, '1', Offset(left, ph), scaler);
      _text(canvas, size, lastLabel!, Offset(size.width, ph), scaler, align: TextAlign.right);
    }
  }

  @override
  bool shouldRepaint(_SeriesPainter old) => true;
}

/// The guide trace on a fixed ±2.5″ scale, so a calm night and a runaway
/// don't look alike: RA solid, Dec dashed, so they aren't told apart by
/// colour alone.
class _TracePainter extends CustomPainter {
  _TracePainter(this.trace, this.scaler);

  final List<({double ra, double dec})> trace;
  final TextScaler scaler;

  /// The scale's half height (″).
  static const double scale = 2.5;

  @override
  void paint(Canvas canvas, Size size) {
    final left = math.max(30.0, _textSize('-2″', scaler).width + 6);
    final mid = size.height / 2, pw = size.width - left;
    double y(double v) => mid - v.clamp(-scale, scale) / scale * mid;
    final grid = Paint()..color = AraColors.border;
    for (final t in const [-2.0, -1.0, 1.0, 2.0]) {
      canvas.drawLine(
        Offset(left, y(t)),
        Offset(size.width, y(t)),
        Paint()..color = AraColors.border.withValues(alpha: 0.5),
      );
      _text(canvas, size, '${t.toStringAsFixed(0)}″', Offset(left - 4, y(t)), scaler, align: TextAlign.right, middle: true);
    }
    canvas.drawLine(Offset(left, mid), Offset(size.width, mid), grid);
    if (trace.length < 2) return;
    Offset at(int i, double v) => Offset(left + i / (trace.length - 1) * pw, y(v));
    final ra = Path(), dec = Path();
    for (var i = 0; i < trace.length; i++) {
      final a = at(i, trace[i].ra), d = at(i, trace[i].dec);
      i == 0 ? ra.moveTo(a.dx, a.dy) : ra.lineTo(a.dx, a.dy);
      i == 0 ? dec.moveTo(d.dx, d.dy) : dec.lineTo(d.dx, d.dy);
    }
    final paint = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = 1.5;
    canvas.drawPath(ra, paint..color = AraColors.accentInfo);
    // Dashes: every other 6 px of the Dec path.
    paint.color = AraColors.errorText;
    for (final m in dec.computeMetrics()) {
      for (var d = 0.0; d < m.length; d += 12) {
        canvas.drawPath(m.extractPath(d, math.min(d + 6, m.length)), paint);
      }
    }
  }

  @override
  bool shouldRepaint(_TracePainter old) => old.trace != trace;
}

/// The Target's altitude over the night: the imaging span shaded under the
/// curve, now a dot on it, a dashed 30° line, and the dusk, plan start, plan
/// end and dawn times on the axis.
class _AltitudePainter extends CustomPainter {
  _AltitudePainter({
    required this.curve,
    required this.start,
    required this.end,
    required this.now,
    this.spanStart,
    this.spanEnd,
    required this.scaler,
  });

  final List<double> curve;
  final DateTime start, end, now;
  final DateTime? spanStart, spanEnd;
  final TextScaler scaler;

  @override
  void paint(Canvas canvas, Size size) {
    final left = math.max(28.0, _textSize('90°', scaler).width + 6), axis = _textSize('0', scaler).height;
    final ph = size.height - axis, pw = size.width - left;
    // No room left for the curve under the time labels (large text).
    if (ph <= 0) return;
    final total = math.max(1, end.difference(start).inSeconds);
    double frac(DateTime t) => (t.difference(start).inSeconds / total).clamp(0.0, 1.0);
    double x(DateTime t) => left + frac(t) * pw;
    double y(double alt) => ph * (1 - alt.clamp(0, 90) / 90);
    Offset pt(int i) => Offset(left + i / (curve.length - 1) * pw, y(curve[i]));
    final path = Path();
    for (var i = 0; i < curve.length; i++) {
      final p = pt(i);
      i == 0 ? path.moveTo(p.dx, p.dy) : path.lineTo(p.dx, p.dy);
    }
    if (spanStart != null && spanEnd != null) {
      final fill = Path.from(path)
        ..lineTo(left + pw, ph)
        ..lineTo(left, ph)
        ..close();
      canvas.save();
      canvas.clipRect(Rect.fromLTRB(x(spanStart!), 0, x(spanEnd!), ph));
      canvas.drawPath(fill, Paint()..color = AraColors.selectionTint);
      canvas.restore();
    }
    for (final t in const [30.0, 60.0, 90.0]) {
      _text(canvas, size, '${t.round()}°', Offset(left - 4, y(t)), scaler, align: TextAlign.right, middle: true);
    }
    _line(
      canvas,
      Offset(left, y(30)),
      Offset(size.width, y(30)),
      Paint()
        ..color = AraColors.textDisabled
        ..strokeWidth = 1,
      dashed: true,
    );
    canvas.drawPath(
      path,
      Paint()
        ..color = AraColors.textPrimary
        ..style = PaintingStyle.stroke
        ..strokeWidth = 1.5,
    );
    if (!now.isBefore(start) && !now.isAfter(end)) {
      final f = frac(now) * (curve.length - 1);
      final i = f.floor().clamp(0, curve.length - 2);
      final alt = curve[i] + (curve[i + 1] - curve[i]) * (f - i);
      canvas.drawCircle(Offset(x(now), y(alt)), 5, Paint()..color = AraColors.accentInfo);
    }
    for (final (t, align) in [
      (start, TextAlign.left),
      if (spanStart != null) (spanStart!, TextAlign.center),
      if (spanEnd != null) (spanEnd!, TextAlign.center),
      (end, TextAlign.right),
    ]) {
      _text(
        canvas,
        size,
        simpleClockTime(t),
        Offset(
          align == TextAlign.left
              ? left
              : align == TextAlign.right
              ? size.width
              : x(t),
          ph,
        ),
        scaler,
        align: align,
      );
    }
  }

  @override
  bool shouldRepaint(_AltitudePainter old) => true;
}
