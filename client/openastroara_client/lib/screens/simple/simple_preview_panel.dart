import 'package:flutter/material.dart';
import 'package:flutter/scheduler.dart' show Ticker;
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/imaging/capture_progress_state.dart';
import '../../state/imaging/exposure_state.dart';
import '../../state/imaging/last_frame_state.dart';
import '../../state/imaging/live_view_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../state/equipment/camera_state.dart';
import '../../widgets/imaging/exposure_controls_panel.dart' show FilterDropdown;
import '../../widgets/imaging/histogram_strip.dart' show frameHistogramProvider;
import '../imaging_actions.dart';
import 'simple_shell.dart'
    show SimplePanel, SimpleSegment, kSimpleStatusMaxWidth, kSimpleTileFill;

/// Simple Mode body text: 14 px minimum (brief), tabular for live numbers.
const kSimpleText = TextStyle(
  fontSize: 14,
  color: AraColors.textPrimary,
  fontFeatures: [FontFeature.tabularFigures()],
);

/// Single takes a saved frame (the daemon registers every Take One); Loop
/// runs live view, whose frames are not saved.
enum PreviewShot { loop, single }

final previewShotProvider = NotifierProvider<_PreviewShot, PreviewShot>(
  _PreviewShot.new,
);

class _PreviewShot extends Notifier<PreviewShot> {
  @override
  PreviewShot build() => PreviewShot.single;
  void set(PreviewShot v) => state = v;
}

/// "5 s · 100": the settings chip's caption on the rail.
String simpleSettingsSummary(WidgetRef ref) {
  final p = ref.watch(exposureControllerProvider);
  return '${formatExposure(p.exposure)} · ${p.gain}';
}

/// The last frame's facts as one line under the strip, turned on from the
/// left rail's Info:
/// HFR, stars, exposure · filter, gain · offset and clipped highlights.
class SimpleInfoPanel extends ConsumerWidget {
  const SimpleInfoPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final looping = ref.watch(liveViewControllerProvider);
    final id = ref.watch(lastCapturedFrameIdProvider);
    final params = ref.watch(exposureControllerProvider);
    // Stars and HFR exist for saved frames only (the daemon analyses those).
    final analysed = (looping || id == null)
        ? null
        : ref.watch(frameHistogramProvider(id)).asData?.value;
    // The frame's own header values, not the controls' (they differ when the
    // shot came from a sequence).
    final gainOffset = analysed?.gain == null
        ? '—'
        : '${analysed!.gain} · ${analysed.offset ?? '—'}';
    final clipped = analysed == null
        ? '—'
        : '${(analysed.highClipFraction * 100).toStringAsFixed(analysed.highClipFraction < 0.001 ? 0 : 2)} %';
    final facts = [
      ('HFR', analysed?.hfr?.toStringAsFixed(2) ?? '—'),
      ('Stars', analysed?.stars?.toString() ?? '—'),
      ('Exposure', '${formatExposure(params.exposure)} · ${params.filterSlot}'),
      ('Gain · offset', gainOffset),
      ('Clipped', clipped),
    ];
    return SimplePanel(
      padding: const EdgeInsets.symmetric(horizontal: AraSpace.s16),
      child: SizedBox(
        height: 40,
        child: SingleChildScrollView(
          scrollDirection: Axis.horizontal,
          child: Row(
            spacing: AraSpace.s16,
            children: [
              for (final (label, value) in facts)
                Text.rich(
                  TextSpan(
                    children: [
                      TextSpan(text: '$label ', style: AraText.caption),
                      TextSpan(text: value, style: kSimpleText),
                    ],
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }
}

/// True from the tap on the square until the daemon has answered the abort:
/// the abort is a round trip to the rig (about a second), and the shutter
/// and status should answer the tap, not the reply.
final _stoppingProvider = NotifierProvider<_Stopping, bool>(_Stopping.new);

class _Stopping extends Notifier<bool> {
  @override
  bool build() => false;
  void set(bool v) => state = v;
}

/// Cancels the shot in flight with immediate feedback. The capture state
/// itself still waits for the daemon (cancelCapture keeps tracking the shot
/// if the abort didn't go through, so its frame isn't orphaned).
Future<void> _stop(BuildContext context, WidgetRef ref) async {
  final stopping = ref.read(_stoppingProvider.notifier);
  stopping.set(true);
  try {
    await cancelCapture(context, ref);
  } finally {
    stopping.set(false);
  }
}

/// The mode's settings, opened from the rail's chip: Exposure and Gain side
/// by side (picked from lists), Filter, and Loop / Single.
class SimpleSettingsPopover extends ConsumerWidget {
  const SimpleSettingsPopover({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final params = ref.watch(exposureControllerProvider);
    final ctrl = ref.read(exposureControllerProvider.notifier);
    final shot = ref.watch(previewShotProvider);
    final looping = ref.watch(liveViewControllerProvider);
    final caps = ref.watch(cameraStatusProvider).asData?.value?.capabilities;
    // Named gains (a camera with gain modes) are picked by index.
    final namedGains = caps?.gains ?? const <String>[];
    return SimplePanel(
      padding: const EdgeInsets.all(AraSpace.s12),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        spacing: AraSpace.s8,
        children: [
          const Text(
            'Preview settings',
            style: TextStyle(
              fontSize: 14,
              fontWeight: FontWeight.w600,
              color: AraColors.textPrimary,
            ),
          ),
          Row(
            spacing: AraSpace.s8,
            children: [
              Expanded(
                child: _PresetTile<Duration>(
                  key: const Key('simple-exposure'),
                  label: 'Exposure',
                  value: params.exposure,
                  options: exposurePresets(
                    minSec: caps?.minExposureSec ?? 0,
                    maxSec: caps?.maxExposureSec ?? 0,
                  ),
                  format: formatExposure,
                  onSelected: ctrl.setExposure,
                ),
              ),
              Expanded(
                child: _PresetTile<int>(
                  key: const Key('simple-gain'),
                  label: 'Gain',
                  value: params.gain,
                  options: namedGains.isNotEmpty
                      ? [for (var i = 0; i < namedGains.length; i++) i]
                      : gainPresets(
                          min: caps?.minGain ?? 0,
                          max: caps?.maxGain ?? 0,
                        ),
                  format: (g) => namedGains.isNotEmpty && g < namedGains.length
                      ? namedGains[g]
                      : '$g',
                  onSelected: ctrl.setGain,
                ),
              ),
            ],
          ),
          FilterDropdown(
            value: params.filterSlot,
            onChanged: ctrl.setFilterSlot,
          ),
          // Switching while a loop runs would strand it with no stop.
          Container(
            decoration: BoxDecoration(
              color: kSimpleTileFill,
              borderRadius: BorderRadius.circular(8),
            ),
            child: Row(
              children: [
                for (final (v, label) in const [
                  (PreviewShot.loop, 'Loop'),
                  (PreviewShot.single, 'Single'),
                ])
                  Expanded(
                    child: SimpleSegment(
                      label: label,
                      selected: shot == v,
                      radius: 8,
                      onTap: looping
                          ? null
                          : () => ref.read(previewShotProvider.notifier).set(v),
                    ),
                  ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

/// The shutter on the rail: takes a frame (Single) or starts and stops the
/// loop (Loop); while a shot runs its square stops it.
class SimpleShutter extends ConsumerWidget {
  const SimpleShutter({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final shot = ref.watch(previewShotProvider);
    final looping = ref.watch(liveViewControllerProvider);
    final capturing = ref.watch(captureProgressProvider).isCapturing;
    final stopping = ref.watch(_stoppingProvider);
    return _Shutter(
      running: (looping || capturing) && !stopping,
      onPressed: stopping
          ? null
          : switch (shot) {
              PreviewShot.loop => () => toggleLiveView(context, ref, !looping),
              // The square stops the shot in flight, like a loop.
              PreviewShot.single =>
                capturing
                    ? () => _stop(context, ref)
                    : () => takeOne(context, ref),
            },
    );
  }
}

/// "5 s", "0.5 s", "2 min" — how the exposure reads on its tile and list.
String formatExposure(Duration d) {
  final ms = d.inMilliseconds;
  if (ms >= 60000 && ms % 60000 == 0) return '${ms ~/ 60000} min';
  if (ms % 1000 == 0) return '${ms ~/ 1000} s';
  return '${(ms / 1000).toString()} s';
}

const _kExposurePresetsMs = [
  100, 200, 500, 1000, 2000, 3000, 5000, 10000, 15000, 20000, 30000, //
  45000, 60000, 90000, 120000, 180000, 240000, 300000, 600000,
];

/// The exposure list, trimmed to the camera's reported range (0 = not
/// reported, no limit on that side).
@visibleForTesting
List<Duration> exposurePresets({
  required double minSec,
  required double maxSec,
}) => [
  for (final ms in _kExposurePresetsMs)
    if ((minSec <= 0 || ms >= minSec * 1000) &&
        (maxSec <= 0 || ms <= maxSec * 1000))
      Duration(milliseconds: ms),
];

const _kGainPresets = [
  0,
  50,
  100,
  120,
  150,
  200,
  250,
  300,
  350,
  400,
  450,
  500,
  600,
];

/// The gain list, trimmed to the camera's reported range (max 0 = not
/// reported: the usual presets up to 300).
@visibleForTesting
List<int> gainPresets({required int min, required int max}) => [
  for (final g in _kGainPresets)
    if (max <= 0 ? g <= 300 : g >= min && g <= max) g,
];

/// A labelled 48 px tile that opens a list to pick from. The current value
/// stays listed even when it isn't a preset (set in the full UI, say).
class _PresetTile<T> extends StatelessWidget {
  const _PresetTile({
    super.key,
    required this.label,
    required this.value,
    required this.options,
    required this.format,
    required this.onSelected,
  });

  final String label;
  final T value;
  final List<T> options;
  final String Function(T) format;
  final ValueChanged<T> onSelected;

  @override
  Widget build(BuildContext context) {
    final items = options.contains(value) ? options : [value, ...options];
    return PopupMenuButton<T>(
      tooltip: label,
      initialValue: value,
      onSelected: onSelected,
      position: PopupMenuPosition.under,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.all(Radius.circular(10)),
      ),
      constraints: const BoxConstraints(minWidth: 96, maxHeight: 360),
      itemBuilder: (_) => [
        for (final o in items)
          PopupMenuItem<T>(
            value: o,
            height: 44,
            child: Text(format(o), style: kSimpleText),
          ),
      ],
      child: Container(
        height: 48,
        padding: const EdgeInsets.symmetric(horizontal: 10),
        decoration: BoxDecoration(
          color: kSimpleTileFill,
          borderRadius: BorderRadius.circular(10),
        ),
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              label,
              style: AraText.caption,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
            ),
            Text(
              format(value),
              style: kSimpleText,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
            ),
          ],
        ),
      ),
    );
  }
}

/// The round capture button: a white disc to shoot, a square while a loop or
/// exposure runs.
class _Shutter extends StatelessWidget {
  const _Shutter({required this.running, required this.onPressed});

  final bool running;
  final VoidCallback? onPressed;

  @override
  Widget build(BuildContext context) {
    final enabled = onPressed != null;
    final fg = enabled ? AraColors.textPrimary : AraColors.textDisabled;
    return Semantics(
      button: true,
      label: running ? 'Stop' : 'Take a frame',
      child: InkResponse(
        key: const Key('simple-shutter'),
        onTap: onPressed,
        radius: 36,
        child: Container(
          width: 60,
          height: 60,
          decoration: BoxDecoration(
            shape: BoxShape.circle,
            border: Border.all(color: fg, width: 3),
          ),
          alignment: Alignment.center,
          child: Container(
            width: running ? 22 : 40,
            height: running ? 22 : 40,
            decoration: BoxDecoration(
              color: fg,
              shape: running ? BoxShape.rectangle : BoxShape.circle,
              borderRadius: running ? BorderRadius.circular(4) : null,
            ),
          ),
        ),
      ),
    );
  }
}

/// What the camera is doing, as a small pill top-left on the image (like a
/// camera's REC light): looping, taking a frame with its countdown and bar,
/// stopping, frame ready, or why it failed. Nothing while idle. It sits
/// outside the panels, so it stays in the immersive view.
class SimpleStatusPill extends ConsumerStatefulWidget {
  const SimpleStatusPill({super.key});

  @override
  ConsumerState<SimpleStatusPill> createState() => _SimpleStatusPillState();
}

class _SimpleStatusPillState extends ConsumerState<SimpleStatusPill>
    with SingleTickerProviderStateMixin {
  late final Ticker _ticker = createTicker((_) => setState(() {}));

  @override
  void dispose() {
    _ticker.dispose();
    super.dispose();
  }

  /// The bar and remaining time are elapsed-based and the daemon's progress
  /// poll is slow, so redraw every frame while a shot is active — a smooth
  /// bar instead of one that freezes, or steps 4×/s.
  void _syncTick(bool active) {
    if (active && !_ticker.isActive) {
      _ticker.start();
    } else if (!active && _ticker.isActive) {
      _ticker.stop();
    }
  }

  @override
  Widget build(BuildContext context) {
    final p = ref.watch(captureProgressProvider);
    final stopping = ref.watch(_stoppingProvider);
    final looping = ref.watch(liveViewControllerProvider);
    _syncTick(p.isActive);
    final exposing = !stopping && p.phase == CapturePhase.exposing;
    final (String? label, String? detail, Color dot) = stopping
        ? ('Stopping…', null, AraColors.textSecondary)
        : switch (p.phase) {
            CapturePhase.exposing => (
              'Taking a frame',
              p.exposureRemaining == null
                  ? 'Starting…'
                  // Whole seconds, rounded up: "1 s left" until it's done.
                  : '${(p.exposureRemaining!.inMilliseconds / 1000).ceil()} s left',
              AraColors.accentConnected,
            ),
            CapturePhase.downloading => (
              'Downloading…',
              null,
              AraColors.accentConnected,
            ),
            CapturePhase.done => (
              'Frame ready',
              null,
              AraColors.accentConnected,
            ),
            CapturePhase.failed => (
              'Capture failed',
              p.error,
              AraColors.accentError,
            ),
            CapturePhase.idle =>
              looping
                  ? ('Looping', null, AraColors.accentConnected)
                  : (null, null, AraColors.textSecondary),
          };
    if (label == null) return const SizedBox.shrink();
    return ConstrainedBox(
      constraints: const BoxConstraints(maxWidth: kSimpleStatusMaxWidth),
      child: SimplePanel(
        padding: const EdgeInsets.symmetric(horizontal: AraSpace.s16),
        child: SizedBox(
          height: 48,
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(Icons.circle, size: 10, color: dot),
              const SizedBox(width: AraSpace.s8),
              // One text, so the label keeps its room and only the detail's
              // tail is cut (two Flexibles halved the space: "Taking a fr…").
              Flexible(
                child: Text.rich(
                  TextSpan(
                    text: label,
                    children: [
                      if (detail != null) ...[
                        const WidgetSpan(child: SizedBox(width: AraSpace.s8)),
                        TextSpan(
                          text: detail,
                          style: const TextStyle(color: AraColors.textSecondary),
                        ),
                      ],
                    ],
                  ),
                  style: kSimpleText,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                ),
              ),
              if (exposing) ...[
                const SizedBox(width: AraSpace.s12),
                SizedBox(
                  width: 60,
                  child: ClipRRect(
                    borderRadius: BorderRadius.circular(2),
                    child: LinearProgressIndicator(
                      value: (p.displayProgressPct ?? 0) / 100,
                      minHeight: 4,
                      backgroundColor: kSimpleTileFill,
                    ),
                  ),
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}
