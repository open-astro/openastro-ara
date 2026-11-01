import 'package:flutter/material.dart';

import 'dart:math' as math;

import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/frame_histogram.dart';
import '../../models/guider_status.dart';
import '../../state/app_shell_state.dart';
import '../../state/guider/guide_step_state.dart';
import '../../state/guider/guider_state.dart';
import '../../state/imaging/last_frame_state.dart';
import '../../state/imaging/live_view_state.dart';
import '../../state/night_mode_state.dart';
import '../../state/settings/settings_nav.dart';
import '../../state/simple_mode_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../widgets/command_palette.dart';
import '../../widgets/emergency_stop_button.dart';
import '../../widgets/equipment/equipment_status_chip.dart';
import '../../widgets/equipment_chip.dart'
    show EquipmentChipCompact, EquipmentChipHideDisconnected;
import '../../widgets/imaging/frame_viewer.dart';
import '../../widgets/imaging/guiding_strip.dart';
import '../../widgets/imaging/histogram_strip.dart' show frameHistogramProvider;
import '../../widgets/sky_atlas/stellarium_view.dart';
import '../library/image_library_screen.dart';
import '../shell_listeners.dart';
import 'simple_focus_panel.dart';
import 'simple_preview_panel.dart';

/// Inset from the frame edge and between panels.
const double kSimpleInset = 16;

/// Height of the status strip and of the bottom stats panels.
const double kSimpleBarHeight = 56;

/// Width of the left and right rails.
const double kSimpleRailWidth = 72;

/// The status pill's widest (top-left under the strip); Info starts right of
/// it.
const double kSimpleStatusMaxWidth = 300;

/// Side of the compact Stop at the bottom right.
const double kSimpleStopSize = 60;

/// The modes, in the decided order.
enum SimpleModeScreen { preview, focus, polarAlign, target, run }

extension on SimpleModeScreen {
  String get label => switch (this) {
    SimpleModeScreen.preview => 'Preview',
    SimpleModeScreen.polarAlign => 'Polar align',
    SimpleModeScreen.focus => 'Focus',
    SimpleModeScreen.target => 'Target',
    SimpleModeScreen.run => 'Run',
  };

  IconData get icon => switch (this) {
    SimpleModeScreen.preview => Icons.visibility_outlined,
    SimpleModeScreen.focus => Icons.center_focus_strong,
    SimpleModeScreen.polarAlign => Icons.explore_outlined,
    SimpleModeScreen.target => Icons.travel_explore,
    SimpleModeScreen.run => Icons.playlist_play,
  };

  /// The full-UI tab a mode opens until its Simple Mode screen exists
  /// (brief: Preview, Polar align and Focus open Live; Target opens Planning).
  // shortcut: Run always opens Sequence; the brief wants Live while a run is
  // imaging — switch on the sequence state when the Run screen is built.
  int get fullUiTab => switch (this) {
    SimpleModeScreen.preview ||
    SimpleModeScreen.polarAlign ||
    SimpleModeScreen.focus => kLiveTabIndex,
    SimpleModeScreen.target => kPlanningTabIndex,
    SimpleModeScreen.run => kRunTabIndex,
  };
}

/// Leave Simple Mode for the full UI, showing [tab].
void leaveSimpleMode(WidgetRef ref, int tab) {
  ref.read(selectedTabIndexProvider.notifier).select(tab);
  ref.read(simpleModeProvider.notifier).set(false);
}

/// The one popover open beside a rail, if any.
enum _Popover { none, modes, settings }

/// Simple Mode (ALP-4810): the latest frame fills the window and only the
/// shutter is always out. A right rail holds the mode (tap for the five
/// modes), a settings chip, the shutter and Stop; a left rail holds Info and
/// the Library; guiding and the histogram run along the bottom once they
/// have data; a small status sits top-left. Settings and modes open
/// beside the rail and close with a tap on the image. Tapping the image with
/// nothing open hides everything (the immersive view).
///
/// Preview and Focus so far; the other modes open their full-UI tab, and so
/// does any tab jump made from here (Settings, a device dot, ⌘K).
class SimpleShell extends ConsumerStatefulWidget {
  const SimpleShell({
    super.key,
    this.planetarium = const StellariumView(bare: true),
  });

  /// Shown behind the panels until there is a frame; tests pass a stand-in
  /// for the webview.
  final Widget planetarium;

  @override
  ConsumerState<SimpleShell> createState() => _SimpleShellState();
}

class _SimpleShellState extends ConsumerState<SimpleShell> {
  bool _clean = false;
  bool _info = false;

  /// The planetarium on demand (the left rail's Sky), over any frame.
  bool _sky = false;
  _Popover _popover = _Popover.none;
  SimpleModeScreen _mode = SimpleModeScreen.preview;

  /// Ties the popovers to the right rail's top-left corner.
  final _railLink = LayerLink();

  // The system status bar (clock, battery, Wi-Fi) is hidden while Simple
  // Mode shows and comes back for the full UI; an edge swipe still reveals
  // it briefly (sticky). A no-op on desktop.
  // §61 — the shortcuts only see keys when focus is inside the shell, and
  // the root NightHotkey's autofocus wins first; take focus explicitly once
  // mounted (as AppShell does).
  final _focus = FocusNode(debugLabel: 'SimpleShell');

  @override
  void initState() {
    super.initState();
    SystemChrome.setEnabledSystemUIMode(SystemUiMode.immersiveSticky);
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) _focus.requestFocus();
    });
  }

  @override
  void dispose() {
    _focus.dispose();
    SystemChrome.setEnabledSystemUIMode(SystemUiMode.edgeToEdge);
    super.dispose();
  }

  // ⌘, / Ctrl+, opens Settings, which (for now) returns to the full UI.
  void _openSettings() =>
      ref.read(selectedTabIndexProvider.notifier).select(kOptionsTabIndex);

  void _toggle(_Popover p) =>
      setState(() => _popover = _popover == p ? _Popover.none : p);

  /// A tap on the image closes what's open, or else toggles the immersive
  /// view (only once there is a frame to look at).
  void _tapImage(bool hasFrame) => setState(() {
    if (_popover != _Popover.none) {
      _popover = _Popover.none;
    } else if (hasFrame) {
      _clean = !_clean;
    }
  });

  @override
  Widget build(BuildContext context) {
    ref.listen(selectedTabIndexProvider, (previous, next) {
      ref.read(simpleModeProvider.notifier).set(false);
    });
    final hasFrame =
        ref.watch(lastCapturedFrameIdProvider) != null ||
        ref.watch(liveViewControllerProvider);
    final showSky = _sky || !hasFrame;
    final panels = !_clean || showSky;
    final motion = MediaQuery.disableAnimationsOf(context)
        ? Duration.zero
        : const Duration(milliseconds: 220);
    const railTop = kSimpleInset * 2 + kSimpleBarHeight;
    const besideRail = kSimpleInset * 2 + kSimpleRailWidth;
    return Theme(
      data: simpleTheme(Theme.of(context)),
      child: Scaffold(
        backgroundColor: AraColors.bgPrimary,
        body: ShellListeners(
          child: CallbackShortcuts(
            bindings: <ShortcutActivator, VoidCallback>{
              const SingleActivator(LogicalKeyboardKey.keyK, meta: true): () =>
                  showCommandPalette(context),
              const SingleActivator(
                LogicalKeyboardKey.keyK,
                control: true,
              ): () =>
                  showCommandPalette(context),
              const SingleActivator(LogicalKeyboardKey.comma, meta: true):
                  _openSettings,
              const SingleActivator(LogicalKeyboardKey.comma, control: true):
                  _openSettings,
            },
            child: Focus(
              focusNode: _focus,
              child: SafeArea(
                child: Stack(
                  children: [
                    Positioned.fill(
                      child: GestureDetector(
                        key: const Key('simple-frame'),
                        behavior: HitTestBehavior.opaque,
                        onTap: () => _tapImage(!showSky),
                        child: !showSky
                            ? const FrameViewer()
                            // The sky: asked for, or standing in until there
                            // is a frame.
                            : KeyedSubtree(
                                key: const Key('simple-planetarium'),
                                child: widget.planetarium,
                              ),
                      ),
                    ),
                    if (!hasFrame)
                      const Positioned.fill(
                        child: IgnorePointer(
                          child: Center(
                            child: Text(
                              'Tap the shutter to take a frame',
                              style: TextStyle(
                                fontSize: 20,
                                letterSpacing: 2,
                                color: Color(0x80E0E0E0),
                              ),
                            ),
                          ),
                        ),
                      ),
                    // The camera's status, top-left under the strip; it stays
                    // in the immersive view and moves up into the corner.
                    AnimatedPositioned(
                      left: kSimpleInset,
                      top: panels ? railTop : kSimpleInset,
                      duration: motion,
                      curve: Curves.easeOut,
                      child: const SimpleStatusPill(),
                    ),
                    _Fade(
                      visible: panels,
                      child: Stack(
                        children: [
                          const Positioned(
                            left: kSimpleInset,
                            right: kSimpleInset,
                            top: kSimpleInset,
                            height: kSimpleBarHeight,
                            child: _StatusStrip(),
                          ),
                          // Info: the last frame's facts as one line under the
                          // strip, at the right edge; it starts right of the
                          // status pill's slot so the two never meet.
                          if (_info)
                            const Positioned(
                              left: kSimpleInset * 2 + kSimpleStatusMaxWidth,
                              right: kSimpleInset,
                              top: railTop,
                              child: Align(
                                alignment: Alignment.centerRight,
                                child: SimpleInfoPanel(),
                              ),
                            ),
                          // Guiding and the histogram along the bottom, each
                          // once it has data.
                          const Positioned(
                            left: kSimpleInset,
                            right: besideRail,
                            bottom: kSimpleInset,
                            height: kSimpleBarHeight,
                            child: _StatsRow(),
                          ),
                          Positioned(
                            left: kSimpleInset,
                            top: railTop,
                            bottom: railTop,
                            width: kSimpleRailWidth,
                            child: Align(
                              alignment: Alignment.centerLeft,
                              child: _LeftRail(
                                info: _info,
                                sky: showSky,
                                onInfo: () => setState(() => _info = !_info),
                                // With no frame there is only the sky.
                                onSky: hasFrame
                                    ? () => setState(() => _sky = !_sky)
                                    : null,
                              ),
                            ),
                          ),
                          // The right rail: only as tall as its controls and
                          // centred between the strip and Stop. Its popover
                          // (modes or settings) follows the rail's top-left
                          // corner, so opening one never moves the rail.
                          Positioned(
                            right: kSimpleInset,
                            top: railTop,
                            bottom: kSimpleInset * 2 + kSimpleStopSize,
                            width: kSimpleRailWidth,
                            child: Center(
                              child: CompositedTransformTarget(
                                link: _railLink,
                                child: _RightRail(
                                  mode: _mode,
                                  popover: _popover,
                                  onModes: () => _toggle(_Popover.modes),
                                  onSettings: () => _toggle(_Popover.settings),
                                ),
                              ),
                            ),
                          ),
                          // An open popover, else the mode's own panel, in the
                          // one slot beside the rail.
                          if (_popover != _Popover.none ||
                              _mode == SimpleModeScreen.focus)
                            Positioned(
                              left: 0,
                              top: 0,
                              child: CompositedTransformFollower(
                                link: _railLink,
                                targetAnchor: Alignment.topLeft,
                                followerAnchor: Alignment.topRight,
                                offset: const Offset(-kSimpleInset, 0),
                                child: switch (_popover) {
                                  _Popover.modes => _ModeGrid(
                                    current: _mode,
                                    onPicked: (m) => setState(() {
                                      _popover = _Popover.none;
                                      _mode = m;
                                    }),
                                  ),
                                  _Popover.settings => const SizedBox(
                                    width: 280,
                                    child: SimpleSettingsPopover(),
                                  ),
                                  _Popover.none => const SimpleFocusPanel(),
                                },
                              ),
                            ),
                          // Stop on its own at the bottom, under the rail.
                          const Positioned(
                            right:
                                kSimpleInset +
                                (kSimpleRailWidth - kSimpleStopSize) / 2,
                            bottom: kSimpleInset,
                            width: kSimpleStopSize,
                            height: kSimpleStopSize,
                            child: EmergencyStopButton(compact: true),
                          ),
                        ],
                      ),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// Fades the panels out for the immersive view (220 ms, instant with reduced
/// motion) and stops them taking taps while hidden.
class _Fade extends StatelessWidget {
  const _Fade({required this.visible, required this.child});

  final bool visible;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    final reduce = MediaQuery.disableAnimationsOf(context);
    return Positioned.fill(
      child: IgnorePointer(
        ignoring: !visible,
        child: AnimatedOpacity(
          opacity: visible ? 1 : 0,
          duration: reduce ? Duration.zero : const Duration(milliseconds: 220),
          curve: Curves.easeOut,
          child: child,
        ),
      ),
    );
  }
}

/// One floating panel: overlay scrim, 10 px corners, no border.
class SimplePanel extends StatelessWidget {
  const SimplePanel({super.key, required this.child, this.padding});

  final Widget child;
  final EdgeInsetsGeometry? padding;

  @override
  Widget build(BuildContext context) => Container(
    padding: padding,
    decoration: BoxDecoration(
      color: AraColors.overlayScrim,
      borderRadius: BorderRadius.circular(10),
    ),
    child: child,
  );
}

/// Device dots (connected gear only), night mode and ⋯ (search, Settings,
/// Show all options).
class _StatusStrip extends ConsumerWidget {
  const _StatusStrip();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final night = switch (ref.watch(nightModeProvider)) {
      AsyncData(:final value) => value,
      _ => false,
    };
    return SimplePanel(
      padding: const EdgeInsets.only(left: 8, right: 4),
      child: Row(
        children: [
          // Only gear that's connected; a fault still shows.
          const Expanded(
            child: Align(
              alignment: Alignment.centerLeft,
              child: EquipmentChipCompact(
                compact: true,
                child: SingleChildScrollView(
                  scrollDirection: Axis.horizontal,
                  child: EquipmentChipHideDisconnected(
                    child: TopEquipmentChips(),
                  ),
                ),
              ),
            ),
          ),
          IconButton(
            icon: Icon(
              night ? Icons.nights_stay : Icons.nights_stay_outlined,
              size: 22,
              color: night ? AraColors.accentInfo : null,
            ),
            tooltip: night ? 'Night mode on' : 'Night mode',
            onPressed: () => ref.read(nightModeProvider.notifier).set(!night),
          ),
          PopupMenuButton<String>(
            key: const Key('simple-more'),
            tooltip: 'More',
            icon: const Icon(Icons.more_horiz, size: 22),
            position: PopupMenuPosition.under,
            shape: const RoundedRectangleBorder(
              borderRadius: BorderRadius.all(Radius.circular(10)),
            ),
            onSelected: (v) => switch (v) {
              'search' => showCommandPalette(context),
              'settings' =>
                ref
                    .read(selectedTabIndexProvider.notifier)
                    .select(kOptionsTabIndex),
              _ => leaveSimpleMode(ref, SimpleModeScreen.preview.fullUiTab),
            },
            itemBuilder: (_) => const [
              PopupMenuItem(
                value: 'search',
                height: 44,
                child: Text('Search settings', style: kSimpleText),
              ),
              PopupMenuItem(
                value: 'settings',
                height: 44,
                child: Text('Settings', style: kSimpleText),
              ),
              PopupMenuItem(
                value: 'all',
                height: 44,
                child: Text('Show all options', style: kSimpleText),
              ),
            ],
          ),
        ],
      ),
    );
  }
}

/// One labelled rail button: an icon over a short caption.
class _RailButton extends StatelessWidget {
  const _RailButton({
    super.key,
    required this.icon,
    required this.label,
    required this.onTap,
    this.selected = false,
  });

  final IconData icon;
  final String label;
  final VoidCallback onTap;
  final bool selected;

  @override
  Widget build(BuildContext context) => Semantics(
    button: true,
    selected: selected,
    label: label,
    child: Material(
      color: selected ? const Color(0x401976D2) : Colors.transparent,
      borderRadius: BorderRadius.circular(8),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(8),
        child: SizedBox(
          width: 60,
          height: 56,
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              Icon(
                icon,
                size: 24,
                color: selected
                    ? AraColors.textPrimary
                    : AraColors.textSecondary,
              ),
              const SizedBox(height: 2),
              Text(
                label,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: AraText.caption.copyWith(
                  color: selected
                      ? AraColors.textPrimary
                      : AraColors.textSecondary,
                ),
              ),
            ],
          ),
        ),
      ),
    ),
  );
}

class _LeftRail extends StatelessWidget {
  const _LeftRail({
    required this.info,
    required this.sky,
    required this.onInfo,
    required this.onSky,
  });

  final bool info;
  final bool sky;
  final VoidCallback onInfo;
  final VoidCallback? onSky;

  @override
  Widget build(BuildContext context) => SimplePanel(
    padding: const EdgeInsets.symmetric(vertical: AraSpace.s8, horizontal: 6),
    child: Column(
      mainAxisSize: MainAxisSize.min,
      spacing: 4,
      children: [
        _RailButton(
          key: const Key('simple-sky'),
          icon: Icons.public,
          label: 'Sky',
          selected: sky,
          onTap: onSky ?? () {},
        ),
        _RailButton(
          key: const Key('simple-info'),
          icon: Icons.info_outline,
          label: 'Info',
          selected: info,
          onTap: onInfo,
        ),
        _RailButton(
          key: const Key('simple-library'),
          icon: Icons.photo_library_outlined,
          label: 'Library',
          onTap: () => Navigator.of(context).push(
            MaterialPageRoute<void>(builder: (_) => const ImageLibraryScreen()),
          ),
        ),
      ],
    ),
  );
}

/// The mode (tap for the five) on top of the shutter, the settings chip
/// under it; Stop sits on its own below.
class _RightRail extends ConsumerWidget {
  const _RightRail({
    required this.mode,
    required this.popover,
    required this.onModes,
    required this.onSettings,
  });

  final SimpleModeScreen mode;
  final _Popover popover;
  final VoidCallback onModes;
  final VoidCallback onSettings;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final modesOpen = popover == _Popover.modes;
    return SimplePanel(
      padding: const EdgeInsets.symmetric(vertical: AraSpace.s8, horizontal: 6),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        spacing: AraSpace.s8,
        children: [
          // The mode on top of the shutter; the settings chip under it.
          Semantics(
            button: true,
            label: 'Mode: ${mode.label}',
            child: InkWell(
              key: const Key('simple-mode'),
              onTap: onModes,
              borderRadius: BorderRadius.circular(8),
              child: SizedBox(
                width: 60,
                height: 48,
                child: Column(
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    Text(
                      mode.label,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: kSimpleText.copyWith(
                        fontWeight: FontWeight.w600,
                        color: modesOpen ? AraColors.accentInfo : null,
                      ),
                    ),
                    Icon(
                      modesOpen ? Icons.expand_less : Icons.expand_more,
                      size: 18,
                      color: modesOpen
                          ? AraColors.accentInfo
                          : AraColors.textSecondary,
                    ),
                  ],
                ),
              ),
            ),
          ),
          const SimpleShutter(),
          _RailButton(
            key: const Key('simple-settings'),
            icon: Icons.tune,
            label: simpleSettingsSummary(ref),
            selected: popover == _Popover.settings,
            onTap: onSettings,
          ),
        ],
      ),
    );
  }
}

/// The five modes as a grid beside the rail. Preview and Focus are built; the
/// others open their full-UI tab for now.
class _ModeGrid extends ConsumerWidget {
  const _ModeGrid({required this.current, required this.onPicked});

  final SimpleModeScreen current;
  final ValueChanged<SimpleModeScreen> onPicked;

  static const _built = {SimpleModeScreen.preview, SimpleModeScreen.focus};

  @override
  Widget build(BuildContext context, WidgetRef ref) => SimplePanel(
    padding: const EdgeInsets.all(AraSpace.s8),
    child: SizedBox(
      width: 300,
      child: Wrap(
        spacing: AraSpace.s8,
        runSpacing: AraSpace.s8,
        children: [
          for (final m in SimpleModeScreen.values)
            SizedBox(
              width: 138,
              height: 52,
              child: Material(
                color: m == current
                    ? AraColors.selectionBg
                    : kSimpleTileFill,
                borderRadius: BorderRadius.circular(8),
                child: InkWell(
                  borderRadius: BorderRadius.circular(8),
                  onTap: () {
                    if (_built.contains(m)) {
                      onPicked(m);
                    } else {
                      onPicked(current);
                      leaveSimpleMode(ref, m.fullUiTab);
                    }
                  },
                  child: Padding(
                    padding: const EdgeInsets.symmetric(horizontal: 12),
                    child: Row(
                      spacing: AraSpace.s8,
                      children: [
                        Icon(m.icon, size: 20, color: AraColors.textPrimary),
                        Flexible(
                          child: Text(
                            m.label,
                            style: kSimpleText,
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
              ),
            ),
        ],
      ),
    ),
  );
}

class _GuidingBar extends ConsumerWidget {
  const _GuidingBar();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final status = ref.watch(guiderStatusProvider).asData?.value;
    final steps = ref.watch(guideStepsProvider);
    final guiding = status?.runtimeState == GuiderRuntimeState.guiding;
    // Nothing to show until the guider has produced a step or is guiding.
    if (!guiding && steps.isEmpty) return const SizedBox.shrink();
    return SimplePanel(
      padding: const EdgeInsets.symmetric(horizontal: AraSpace.s16),
      child: Row(
        children: [
          Flexible(
            child: Column(
              mainAxisAlignment: MainAxisAlignment.center,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                const Text('Guiding', style: kSimpleText),
                Text(
                  guiding
                      ? 'RMS ${GuidingStrip.fmt(status?.rmsTotalArcsec, '″')}'
                      : GuidingStrip.stateLabel(status),
                  style: AraText.caption,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                ),
              ],
            ),
          ),
          const SizedBox(width: 14),
          Expanded(
            child: SizedBox(
              height: 32,
              child: CustomPaint(
                key: const Key('simple-guide-trace'),
                painter: _TracePainter(steps),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// The last minute or so of RA (blue) and Dec (red) guide error, centred on
/// zero and scaled to the largest excursion shown.
class _TracePainter extends CustomPainter {
  _TracePainter(List<GuideStep> steps)
    : _steps = steps.length > _kShown
          ? steps.sublist(steps.length - _kShown)
          : steps;

  static const _kShown = 60;
  final List<GuideStep> _steps;

  @override
  void paint(Canvas canvas, Size size) {
    final mid = size.height / 2;
    canvas.drawLine(
      Offset(0, mid),
      Offset(size.width, mid),
      Paint()..color = AraColors.border,
    );
    if (_steps.length < 2) return;
    var peak = 1.0;
    for (final s in _steps) {
      for (final v in [s.raPx, s.decPx]) {
        if (v != null && v.abs() > peak) peak = v.abs();
      }
    }
    void line(double? Function(GuideStep) value, Color color) {
      final path = Path();
      var started = false;
      for (var i = 0; i < _steps.length; i++) {
        final v = value(_steps[i]);
        if (v == null) continue;
        final p = Offset(size.width * i / (_kShown - 1), mid - v / peak * mid);
        started ? path.lineTo(p.dx, p.dy) : path.moveTo(p.dx, p.dy);
        started = true;
      }
      canvas.drawPath(
        path,
        Paint()
          ..color = color
          ..style = PaintingStyle.stroke
          ..strokeWidth = 1.5,
      );
    }

    line((s) => s.raPx, AraColors.accentInfo);
    line((s) => s.decPx, AraColors.accentError);
  }

  @override
  bool shouldRepaint(_TracePainter old) => old._steps != _steps;
}

/// Guiding and the histogram along the bottom. Each panel waits for data, and
/// whichever shows takes the row (both: guiding gets the larger share).
class _StatsRow extends ConsumerWidget {
  const _StatsRow();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final status = ref.watch(guiderStatusProvider).asData?.value;
    final guiding =
        status?.runtimeState == GuiderRuntimeState.guiding ||
        ref.watch(guideStepsProvider).isNotEmpty;
    // The histogram is for saved frames (the daemon analyses those).
    final id = ref.watch(liveViewControllerProvider)
        ? null
        : ref.watch(lastCapturedFrameIdProvider);
    final histogram = id == null
        ? null
        : ref.watch(frameHistogramProvider(id)).asData?.value;
    // The two share the row equally (same size), each capped so a lone one
    // is a panel, not a bar across the row.
    return Row(
      spacing: kSimpleInset,
      children: [
        if (guiding)
          Flexible(
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 520),
              child: const _GuidingBar(),
            ),
          ),
        if (histogram != null)
          Flexible(
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 520),
              child: _HistogramBar(histogram),
            ),
          ),
      ],
    );
  }
}

class _HistogramBar extends StatelessWidget {
  const _HistogramBar(this.h);

  final FrameHistogram h;

  @override
  Widget build(BuildContext context) => SimplePanel(
    padding: const EdgeInsets.symmetric(horizontal: AraSpace.s16),
    child: Row(
      children: [
        Flexible(
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const Text('Histogram', style: kSimpleText),
              Text(
                'Median ${h.median.round()}',
                style: AraText.caption,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
              ),
            ],
          ),
        ),
        const SizedBox(width: 14),
        Expanded(
          child: SizedBox(
            height: 36,
            child: CustomPaint(
              key: const Key('simple-histogram'),
              painter: _HistogramPainter(h.bins),
            ),
          ),
        ),
      ],
    ),
  );
}

/// The span of [bins] worth drawing: from the first to the last bin holding
/// at least 0.1 % of the peak, padded 10 % each side. A frame uses a sliver of
/// the 16-bit range, so plotting it all draws a spike.
@visibleForTesting
(int, int) histogramRange(List<int> bins) {
  if (bins.isEmpty) return (0, 0);
  final peak = bins.reduce(math.max);
  if (peak == 0) return (0, bins.length - 1);
  final floor = peak / 1000;
  var lo = bins.indexWhere((b) => b >= floor);
  var hi = bins.lastIndexWhere((b) => b >= floor);
  final pad = ((hi - lo) * 0.1).ceil();
  lo = math.max(0, lo - pad);
  hi = math.min(bins.length - 1, hi + pad);
  return (lo, hi);
}

/// The frame's histogram as a filled curve over the range it uses,
/// square-root scaled so the faint tail stays visible next to the sky peak.
class _HistogramPainter extends CustomPainter {
  _HistogramPainter(this.bins);

  final List<int> bins;

  @override
  void paint(Canvas canvas, Size size) {
    if (bins.isEmpty) return;
    final peak = math.sqrt(bins.reduce(math.max).toDouble());
    if (peak == 0) return;
    final (lo, hi) = histogramRange(bins);
    final span = math.max(1, hi - lo);
    final path = Path()..moveTo(0, size.height);
    for (var i = lo; i <= hi; i++) {
      final x = size.width * (i - lo) / span;
      path.lineTo(x, size.height - math.sqrt(bins[i]) / peak * size.height);
    }
    path
      ..lineTo(size.width, size.height)
      ..close();
    canvas.drawPath(
      path,
      Paint()..color = AraColors.textPrimary.withAlpha(140),
    );
  }

  @override
  bool shouldRepaint(_HistogramPainter old) => old.bins != bins;
}

/// One segment of a Simple Mode switch (the mode switcher, Loop / Single):
/// 40 px tall with square-ish corners, Selection Blue when selected.
class SimpleSegment extends StatelessWidget {
  const SimpleSegment({
    super.key,
    required this.label,
    required this.selected,
    required this.radius,
    this.onTap,
    this.alignment = Alignment.center,
  });

  final String label;
  final Alignment alignment;
  final bool selected;
  final double radius;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) => Semantics(
    button: true,
    selected: selected,
    child: Material(
      color: selected ? AraColors.selectionBg : Colors.transparent,
      borderRadius: BorderRadius.circular(radius),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(radius),
        child: Container(
          height: 40,
          padding: const EdgeInsets.symmetric(horizontal: 14),
          alignment: alignment,
          child: Text(
            label,
            maxLines: 1,
            style: kSimpleText.copyWith(
              color: selected ? AraColors.selectionFg : AraColors.textSecondary,
            ),
          ),
        ),
      ),
    ),
  );
}

/// Simple Mode's density (DESIGN.md "Two Densities"): 10 px corners on
/// buttons and fields instead of Material's pills, 44 px minimum targets,
/// fields as borderless tiles.
ThemeData simpleTheme(ThemeData base) {
  const shape = RoundedRectangleBorder(
    borderRadius: BorderRadius.all(Radius.circular(10)),
  );
  final buttons = ButtonStyle(
    shape: const WidgetStatePropertyAll(shape),
    minimumSize: const WidgetStatePropertyAll(Size(0, 44)),
  );
  // Underline-style (not outline) so the label sits inside the tile instead
  // of straddling its top edge.
  const tile = UnderlineInputBorder(
    borderRadius: BorderRadius.all(Radius.circular(10)),
    borderSide: BorderSide.none,
  );
  return base.copyWith(
    filledButtonTheme: FilledButtonThemeData(
      style: buttons.merge(base.filledButtonTheme.style),
    ),
    textButtonTheme: TextButtonThemeData(
      style: buttons.merge(base.textButtonTheme.style),
    ),
    outlinedButtonTheme: OutlinedButtonThemeData(
      style: buttons.merge(base.outlinedButtonTheme.style),
    ),
    inputDecorationTheme: base.inputDecorationTheme.copyWith(
      // 48 px tiles (DESIGN.md layout C), label inside.
      isDense: true,
      contentPadding: const EdgeInsets.fromLTRB(12, 8, 12, 8),
      filled: true,
      fillColor: kSimpleTileFill,
      border: tile,
      enabledBorder: tile,
      focusedBorder: tile.copyWith(
        borderSide: const BorderSide(color: AraColors.accentInfo),
      ),
    ),
  );
}

/// The tile fill behind Simple Mode's fields and switches (Hairline at 55 %).
const kSimpleTileFill = Color(0x8C404040);
