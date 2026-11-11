import 'dart:math' as math;

import 'package:flutter/cupertino.dart' show CupertinoIcons;
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../services/monitor_device_service.dart';
import '../../state/night_mode_state.dart';
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

/// Where a tile tap goes: Monitor closes and the shell opens the window or
/// panel that owns the thing (#1724).
enum MonitorDestination { queue, sky, mount, safety, weather, guider, frameInfo }

/// Monitor below this width isn't offered: the grid needs the room.
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
                  Expanded(child: _Grid(s: s, onOpen: widget.onOpen)),
                ],
              ),
              const Positioned(
                right: 0,
                bottom: 0,
                width: 108,
                height: 108,
                child: EmergencyStopButton(large: true),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// The top line: the verdict, the clock, this device's battery, Night mode
/// and close.
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
    // Shown whole: a question to answer, or a stop's outcome to read.
    final whole = question != null || message != null;
    // 64 pt, growing with the user's text size up to half the screen; past
    // that it scrolls, so a question's answers stay reachable at 2x text.
    return ConstrainedBox(
      constraints: BoxConstraints(minHeight: 64, maxHeight: math.max(64, MediaQuery.sizeOf(context).height / 2)),
      child: Row(
        children: [
          const SizedBox(width: AraSpace.s12),
          if (_dot(v.status) case final dot?) ...[dot, const SizedBox(width: AraSpace.s12)],
          Expanded(
            child: SingleChildScrollView(
              child: Column(
              mainAxisAlignment: MainAxisAlignment.center,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                // Announced once per state: the visible line carries a
                // countdown or an age that would re-announce every second.
                Semantics(liveRegion: true, label: monitorVerdictAnnouncement(l10n, s, head), child: const SizedBox.shrink()),
                Text(head,
                      key: const Key('simple-monitor-verdict'),
                      // A question shows whole: it is what the person answers.
                      maxLines: whole ? null : 2,
                      overflow: whole ? null : TextOverflow.ellipsis,
                      style: kSimpleText.copyWith(
                          fontSize: AraFontSize.monitorVerdict, fontWeight: FontWeight.w600, color: colour)),
                if (reason != null)
                  Text(reason,
                      maxLines: whole ? null : 2,
                      overflow: whole ? null : TextOverflow.ellipsis,
                      style: _dim),
                // A question's answers, under its words: the only controls
                // besides Stop, Night mode and close. The same actions as its
                // pill, which Monitor doesn't draw.
                if (message != null)
                  Padding(
                    padding: const EdgeInsets.only(top: AraSpace.s8, bottom: AraSpace.s8),
                    child: TextButton(
                      key: const Key('simple-monitor-dismiss'),
                      onPressed: () => ref.read(simpleMessagesProvider.notifier).dismiss(message.id),
                      child: Text(l10n.pillsDismiss),
                    ),
                  ),
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
          ),
          const SizedBox(width: AraSpace.s16),
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
          IconButton(
            key: const Key('simple-monitor-close'),
            tooltip: l10n.monitorClose,
            color: AraColors.textSecondary,
            icon: const Icon(Icons.close),
            onPressed: () => Navigator.of(context).maybePop(),
          ),
        ],
      ),
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

/// The tiles: two fixed right columns, the left two sharing the rest. Which
/// areas show comes from the rig's profile.
class _Grid extends StatelessWidget {
  const _Grid({required this.s, required this.onOpen});

  final MonitorSnapshot s;
  final ValueChanged<MonitorDestination> onOpen;

  @override
  Widget build(BuildContext context) {
    final now = _NowTile(s: s, onTap: () => onOpen(MonitorDestination.queue));
    final target = _TargetTile(s: s, onTap: () => onOpen(MonitorDestination.sky));
    final flip = _FlipTile(s: s, onTap: () => onOpen(MonitorDestination.mount));
    final safety = s.safety == null ? null : _SafetyTile(s: s, onTap: () => onOpen(MonitorDestination.safety));
    final weather = s.weather == null ? null : _WeatherTile(s: s, onTap: () => onOpen(MonitorDestination.weather));
    final guider = s.guider == null ? null : _GuiderTile(s: s, onTap: () => onOpen(MonitorDestination.guider));
    final focus = _LastFrameTile(s: s, onTap: () => onOpen(MonitorDestination.frameInfo));
    final night = _NightTile(s: s, onTap: () => onOpen(MonitorDestination.queue));
    const strip = 56.0;

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

/// One tile: its header (icon, name, status word, dot), then its content,
/// which scrolls inside the tile when larger text no longer fits. Read out as
/// its name, value and status.
class _Tile extends StatelessWidget {
  const _Tile({
    required this.tileKey,
    required this.icon,
    required this.name,
    required this.status,
    this.word,
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
            child: LayoutBuilder(
              builder: (context, box) => SingleChildScrollView(
                child: ConstrainedBox(
                  constraints: BoxConstraints(minHeight: box.maxHeight.isFinite ? box.maxHeight : 0),
                  child: IntrinsicHeight(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        Row(children: [
                          Icon(icon, size: 18, color: accent),
                          const SizedBox(width: 6),
                          Expanded(child: Text(name, style: _dim, maxLines: 1, overflow: TextOverflow.ellipsis)),
                          // Flexible: a longer word (pt-BR) shortens, never overflows.
                          if (shown != null && shown.isNotEmpty)
                            Flexible(
                              child: Text(shown,
                                  maxLines: 1, overflow: TextOverflow.ellipsis, style: _dim.copyWith(color: wordColour)),
                            ),
                          // Stale numbers get a grey dot, as off gear does.
                          if (_dot(frozen ? MonitorStatus.off : status) case final dot?) ...[const SizedBox(width: 6), dot],
                        ]),
                        ...children,
                      ],
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
    MonitorVerdictKind.pausedByAra => (l10n.monitorPausedByAra, v.text ?? l10n.monitorPausedByAraReason),
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
      MonitorNowWord.pausedByAra => l10n.monitorPausedByAra,
      MonitorNowWord.waitingForYou => l10n.monitorWaitingForYouWord,
      MonitorNowWord.waitingToStart => l10n.monitorWaiting,
      MonitorNowWord.idle || MonitorNowWord.lastKnown => l10n.monitorIdle,
    };
    final task = n.target == null
        ? l10n.monitorNothingRunning
        : [n.target!, ?n.filter, if (n.subSeconds != null) '${_n(n.subSeconds, 0)} s'].join(' · ');
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
    final sub = n.frame != null && n.frames != null
        ? l10n.monitorFrameOf(n.frame!.clamp(0, n.frames!), n.frames!,
            simpleRunDuration(n.targetSecondsLeft ?? 0), n.target ?? '')
        : null;
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
        if (sub != null) Text(sub, style: _dim),
        const SizedBox(height: AraSpace.s8),
        Expanded(
          child: Row(crossAxisAlignment: CrossAxisAlignment.center, children: [
            Expanded(child: big),
            const SizedBox(width: AraSpace.s16),
            SizedBox(width: 190, child: _UpNext(s: s)),
          ]),
        ),
        if (n.filters.isNotEmpty) ...[
          const SizedBox(height: AraSpace.s8),
          Row(children: [
            for (final f in n.filters)
              Expanded(
                child: Padding(
                  padding: const EdgeInsets.only(right: 10),
                  child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
                    Row(children: [
                      Expanded(
                        child: Text(f.filter,
                            maxLines: 1, overflow: TextOverflow.ellipsis, style: kSimpleText.copyWith(fontWeight: FontWeight.w600)),
                      ),
                      Flexible(child: Text('${f.done}/${f.total}', maxLines: 1, overflow: TextOverflow.ellipsis, style: _dim)),
                    ]),
                    const SizedBox(height: 3),
                    ClipRRect(
                      borderRadius: BorderRadius.circular(AraRadius.field),
                      child: LinearProgressIndicator(
                        value: f.total == 0 ? 0 : f.done / f.total,
                        minHeight: 6,
                        // Finished green, in hand blue.
                        color: f.done >= f.total ? AraColors.accentConnected : AraColors.accentInfo,
                        backgroundColor: AraColors.bgInput,
                      ),
                    ),
                  ]),
                ),
              ),
          ]),
        ],
      ],
    );
  }
}

class _UpNext extends StatelessWidget {
  const _UpNext({required this.s});

  final MonitorSnapshot s;

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    return Column(
      key: const Key('simple-monitor-up-next'),
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(s.tonightsBest ? l10n.monitorTonightsBest : l10n.monitorUpNext, style: kSimpleCaption),
        for (final u in s.upNext)
          Container(
            constraints: const BoxConstraints(minHeight: 30),
            decoration: const BoxDecoration(border: Border(top: BorderSide(color: AraColors.border))),
            child: Row(children: [
              Expanded(
                child: Text(
                  switch (u.kind) {
                    MonitorUpKind.nextFrame => l10n.monitorUpFrame(u.frame ?? 0, u.name ?? ''),
                    MonitorUpKind.meridianFlip => l10n.deviceMeridianFlip,
                    MonitorUpKind.nextTarget || MonitorUpKind.best => u.name ?? '',
                    MonitorUpKind.park => l10n.monitorUpPark,
                  },
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: kSimpleText,
                ),
              ),
              Text(
                u.kind == MonitorUpKind.best
                    ? l10n.monitorAltitudeUp(_n(u.altitudeDeg, 0))
                    : u.at == null
                    ? l10n.monitorThen
                    : simpleClockTime(u.at!),
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
        if (t.curve.length > 1)
          Expanded(
            child: Padding(
              padding: const EdgeInsets.only(top: AraSpace.s4),
              child: CustomPaint(
                painter: _AltitudePainter(
                  curve: t.curve,
                  start: s.night.darkStart!,
                  end: s.night.darkEnd!,
                  now: s.asOf,
                  spanStart: t.spanStart,
                  spanEnd: t.spanEnd,
                ),
              ),
            ),
          ),
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

class _WeatherTile extends StatelessWidget {
  const _WeatherTile({required this.s, required this.onTap});

  final MonitorSnapshot s;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final w = s.weather!;
    const compass = ['N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW'];
    (String, String, String) cell(({MonitorWeatherCell cell, double value, double? overLimit}) c) => switch (c.cell) {
          MonitorWeatherCell.cloud => (_n(c.value, 0), '%', l10n.monitorCloud),
          MonitorWeatherCell.skyQuality => (_n(c.value, 2), '', l10n.monitorSkyQuality),
          MonitorWeatherCell.air => (_n(c.value), '°C', l10n.monitorAir),
          MonitorWeatherCell.dewMargin => (_n(c.value), '°', l10n.monitorDewMargin),
          MonitorWeatherCell.humidity => (_n(c.value, 0), '%', l10n.monitorHumidity),
          MonitorWeatherCell.rain => (_n(c.value), 'mm/h', l10n.monitorRain),
          MonitorWeatherCell.wind => (
              _n(c.value, 0),
              'km/h',
              [
                l10n.monitorWind,
                if (w.windDeg != null) compass[((w.windDeg! % 360) / 45).round() % 8],
                if (c.overLimit != null) l10n.monitorLimit(_n(c.overLimit, 0)),
                if (w.gustKmh != null) l10n.monitorGusts(_n(w.gustKmh, 0)),
              ].join(' · '),
            ),
        };
    return _Tile(
      tileKey: 'weather',
      icon: Icons.cloud_outlined,
      name: l10n.shellWeather,
      status: w.status,
      word: w.status == MonitorStatus.warning ? l10n.monitorPastLimit : null,
      value: [for (final c in w.cells) '${cell(c).$3} ${cell(c).$1} ${cell(c).$2}'].join(', '),
      frozen: s.frozen,
      onTap: onTap,
      children: [
        const SizedBox(height: AraSpace.s4),
        for (final row in _weatherRows(w.cells))
          Expanded(
            child: Row(children: [
              for (final c in row)
                Expanded(
                  child: Column(mainAxisAlignment: MainAxisAlignment.center, crossAxisAlignment: CrossAxisAlignment.start, children: [
                    // The Weather window's icon in front of each reading.
                    Row(children: [
                      _weatherIcon(c.cell, c.overLimit != null ? AraColors.accentWarning : AraColors.textSecondary),
                      const SizedBox(width: 6),
                      Flexible(
                        child: Text.rich(
                          TextSpan(
                            text: cell(c).$1,
                            style: kSimpleReading.copyWith(color: c.overLimit != null ? AraColors.accentWarning : null),
                            children: [TextSpan(text: ' ${cell(c).$2}', style: kSimpleUnit)],
                          ),
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                        ),
                      ),
                    ]),
                    Text(cell(c).$3,
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                        style: kSimpleUnit.copyWith(color: c.overLimit != null ? AraColors.accentWarning : null)),
                  ]),
                ),
            ]),
          ),
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

/// Two readings a row; the wind, with its direction and gusts, takes a row
/// of its own, and so does a last odd reading.
List<List<T>> _weatherRows<T extends ({MonitorWeatherCell cell, double value, double? overLimit})>(List<T> cells) {
  final rows = <List<T>>[];
  for (final c in cells) {
    if (c.cell != MonitorWeatherCell.wind && rows.isNotEmpty && rows.last.length == 1 && rows.last.first.cell != MonitorWeatherCell.wind) {
      rows.last.add(c);
    } else {
      rows.add([c]);
    }
  }
  return rows;
}

class _GuiderTile extends StatelessWidget {
  const _GuiderTile({required this.s, required this.onTap});

  final MonitorSnapshot s;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final g = s.guider!;
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
        Expanded(
          child: Row(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
            SizedBox(
              width: 220,
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Row(children: [
                  Expanded(child: reading('RMS', g.rms)),
                  Expanded(child: reading('RA', g.ra)),
                  Expanded(child: reading('Dec', g.dec)),
                ]),
                const Spacer(),
                if (g.lastDither != null) Text(l10n.monitorLastDither(simpleClockTime(g.lastDither!)), style: _dim),
              ]),
            ),
            const SizedBox(width: AraSpace.s12),
            Expanded(child: CustomPaint(painter: _TracePainter(g.trace))),
          ]),
        ),
      ],
    );
  }
}

class _LastFrameTile extends StatelessWidget {
  const _LastFrameTile({required this.s, required this.onTap});

  final MonitorSnapshot s;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final f = s.lastFrame;
    final last = f.last;
    final cols = <(String, String, List<double>)>[
      (l10n.monitorHfr, _n(last?.hfr, 2), [for (final x in f.frames) ?x.hfr]),
      (l10n.monitorStars, last?.stars?.toString() ?? '—', [for (final x in f.frames) ?x.stars?.toDouble()]),
      (l10n.monitorSkyBackground, _n(last?.background, 0), [for (final x in f.frames) ?x.background]),
      (
        l10n.monitorClippedStars,
        last?.clippedShare == null ? '—' : '${_n(last!.clippedShare! * 100)} %',
        [for (final x in f.frames) ?x.clippedShare],
      ),
    ];
    final af = f.focuser && f.autofocusAt != null
        ? l10n.monitorAutofocusSince(
            simpleClockTime(f.autofocusAt!),
            f.tempSinceAutofocus == null ? '—' : '${f.tempSinceAutofocus! >= 0 ? '+' : '−'}${f.tempSinceAutofocus!.abs().toStringAsFixed(1)} °C',
          )
        : null;
    return _Tile(
      tileKey: 'last-frame',
      icon: Icons.insights,
      name: l10n.monitorLastFrame,
      status: MonitorStatus.normal,
      value: [for (final c in cols) '${c.$1} ${c.$2}'].join(', '),
      frozen: s.frozen,
      onTap: onTap,
      children: [
        Expanded(
          child: Row(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
            for (final c in cols)
              Expanded(
                child: Padding(
                  padding: const EdgeInsets.only(right: AraSpace.s12),
                  child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    Text(c.$2, style: _big),
                    Text(c.$1, style: kSimpleUnit),
                    // No trend while a value is missing.
                    if (c.$2 != '—' && c.$3.length > 1)
                      Expanded(child: CustomPaint(painter: _SparkPainter(c.$3), size: Size.infinite)),
                  ]),
                ),
              ),
          ]),
        ),
        if (af != null) Text(af, style: _dim),
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
        Text(line, maxLines: 1, overflow: TextOverflow.ellipsis, style: kSimpleCaption),
        if (start != null && end != null)
          Expanded(
            child: CustomPaint(
              key: const Key('simple-monitor-timeline'),
              painter: _NightPainter(n, s.asOf, MediaQuery.textScalerOf(context)),
            ),
          ),
      ],
    );
  }
}

/// Tonight's timeline: one block per Queue Target with progress under the
/// one in hand, the flip and park markers, and a line at the current time.
class _NightPainter extends CustomPainter {
  _NightPainter(this.n, this.now, this.scaler);

  final MonitorNight n;
  final DateTime now;
  final TextScaler scaler;

  @override
  void paint(Canvas canvas, Size size) {
    final start = n.darkStart!, end = n.darkEnd!;
    double x(DateTime t) =>
        (t.difference(start).inSeconds / math.max(1, end.difference(start).inSeconds)).clamp(0.0, 1.0) * size.width;
    const top = 4.0, h = 30.0;
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
    for (final (t, icon) in [(n.flipAt, Icons.swap_horiz), (n.parkAt, Icons.local_parking)]) {
      if (t == null) continue;
      final c = Offset(x(t), top + h / 2);
      canvas.drawCircle(c, 12, Paint()..color = AraColors.bgPrimary);
      canvas.drawCircle(c, 12, Paint()
        ..color = AraColors.textSecondary
        ..style = PaintingStyle.stroke);
      final glyph = TextPainter(
        text: TextSpan(
          text: String.fromCharCode(icon.codePoint),
          style: TextStyle(fontFamily: icon.fontFamily, package: icon.fontPackage, fontSize: AraFontSize.simpleCaption + 4, color: AraColors.textPrimary),
        ),
        textDirection: TextDirection.ltr,
      )..layout();
      glyph.paint(canvas, c - Offset(glyph.width / 2, glyph.height / 2));
      glyph.dispose();
    }
    canvas.drawRect(Rect.fromLTWH(x(now) - 1, 0, 2, top * 2 + h), Paint()..color = AraColors.textPrimary);
  }

  @override
  bool shouldRepaint(_NightPainter old) => true;
}

/// A trend scaled to the night's own range, with a minimum span so sensor
/// noise doesn't look dramatic.
class _SparkPainter extends CustomPainter {
  _SparkPainter(this.values);

  final List<double> values;

  @override
  void paint(Canvas canvas, Size size) {
    if (values.length < 2 || size.isEmpty) return;
    final lo = values.reduce(math.min), hi = values.reduce(math.max);
    final mid = (lo + hi) / 2;
    final span = math.max(hi - lo, math.max(mid.abs() * 0.1, 1e-6));
    final path = Path();
    for (var i = 0; i < values.length; i++) {
      final p = Offset(i / (values.length - 1) * size.width, size.height * (0.5 - (values[i] - mid) / span * 0.9));
      i == 0 ? path.moveTo(p.dx, p.dy) : path.lineTo(p.dx, p.dy);
    }
    canvas.drawPath(path, Paint()
      ..color = AraColors.textSecondary
      ..style = PaintingStyle.stroke
      ..strokeWidth = 1.5);
  }

  @override
  bool shouldRepaint(_SparkPainter old) => old.values != values;
}

/// The guide trace: RA solid, Dec dashed, so they aren't told apart by colour
/// alone.
class _TracePainter extends CustomPainter {
  _TracePainter(this.trace);

  final List<({double ra, double dec})> trace;

  @override
  void paint(Canvas canvas, Size size) {
    final mid = size.height / 2;
    canvas.drawLine(Offset(0, mid), Offset(size.width, mid), Paint()..color = AraColors.border);
    if (trace.length < 2) return;
    final peak = math.max(1.0, trace.fold<double>(0, (m, t) => math.max(m, math.max(t.ra.abs(), t.dec.abs()))));
    Offset at(int i, double v) => Offset(i / (trace.length - 1) * size.width, mid - v / peak * mid * 0.9);
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

/// The Target's altitude over the night: its scheduled span shaded, a dashed
/// 30° line, and the current time.
class _AltitudePainter extends CustomPainter {
  _AltitudePainter({
    required this.curve,
    required this.start,
    required this.end,
    required this.now,
    this.spanStart,
    this.spanEnd,
  });

  final List<double> curve;
  final DateTime start, end, now;
  final DateTime? spanStart, spanEnd;

  @override
  void paint(Canvas canvas, Size size) {
    double x(DateTime t) =>
        (t.difference(start).inSeconds / end.difference(start).inSeconds).clamp(0.0, 1.0) * size.width;
    double y(double alt) => size.height * (1 - alt.clamp(0, 90) / 90);
    if (spanStart != null && spanEnd != null) {
      canvas.drawRect(Rect.fromLTRB(x(spanStart!), 0, x(spanEnd!), size.height), Paint()..color = AraColors.selectionTint);
    }
    final ref = Paint()
      ..color = AraColors.textDisabled
      ..strokeWidth = 1;
    for (var d = 0.0; d < size.width; d += 8) {
      canvas.drawLine(Offset(d, y(30)), Offset(math.min(d + 4, size.width), y(30)), ref);
    }
    final path = Path();
    for (var i = 0; i < curve.length; i++) {
      final p = Offset(i / (curve.length - 1) * size.width, y(curve[i]));
      i == 0 ? path.moveTo(p.dx, p.dy) : path.lineTo(p.dx, p.dy);
    }
    canvas.drawPath(path, Paint()
      ..color = AraColors.textPrimary
      ..style = PaintingStyle.stroke
      ..strokeWidth = 1.5);
    canvas.drawLine(Offset(x(now), 0), Offset(x(now), size.height), Paint()
      ..color = AraColors.accentInfo
      ..strokeWidth = 1.5);
  }

  @override
  bool shouldRepaint(_AltitudePainter old) => true;
}
