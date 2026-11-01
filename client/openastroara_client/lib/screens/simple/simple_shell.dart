import 'dart:io' show Platform;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/app_shell_state.dart';
import '../../models/guider_status.dart';
import '../../state/guider/guide_step_state.dart';
import '../../state/guider/guider_state.dart';
import '../../state/imaging/capture_progress_state.dart';
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
import '../../widgets/equipment_chip.dart' show EquipmentChipCompact;
import '../../widgets/imaging/frame_viewer.dart';
import '../../widgets/imaging/guiding_strip.dart';
import '../../widgets/settings_gear_button.dart';
import '../../widgets/sky_atlas/stellarium_view.dart';
import '../shell_listeners.dart';
import 'simple_preview_panel.dart';

/// Panel inset from the frame edge and between panels (DESIGN.md layout C).
const double kSimpleInset = 16;

/// Height of the status strip and of every bottom-row panel.
const double kSimpleBarHeight = 56;

/// Width of the right column.
const double kSimpleColumnWidth = 168;

/// The modes on the switcher, in the decided order.
enum SimpleModeScreen { preview, polarAlign, focus, target, run }

extension on SimpleModeScreen {
  String get label => switch (this) {
    SimpleModeScreen.preview => 'Preview',
    SimpleModeScreen.polarAlign => 'Polar align',
    SimpleModeScreen.focus => 'Focus',
    SimpleModeScreen.target => 'Target',
    SimpleModeScreen.run => 'Run',
  };

  /// The full-UI tab "Show all options" opens for this mode (brief: Preview,
  /// Polar align and Focus open Live; Target opens Planning).
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

/// Simple Mode (ALP-4810, layout C): the latest frame fills the window and
/// the controls float over it on translucent panels — a status strip on top,
/// the Preview column on the right, guiding, the mode switcher and Emergency
/// Stop along the bottom. Tapping the frame hides every panel except
/// Emergency Stop.
///
/// Slice 1 builds Preview only; the other modes open their full-UI tab. Any
/// tab jump made from here (the gear, a device dot, ⌘K) also returns to the
/// full UI, since Settings has no Simple Mode surface yet.
class SimpleShell extends ConsumerStatefulWidget {
  const SimpleShell({
    super.key,
    this.planetarium = const StellariumView(bare: true),
  });

  /// Shown behind the panels until there is a frame (brief: "No rig / no
  /// frame: tonight's planetarium"); tests pass a stand-in for the webview.
  final Widget planetarium;

  @override
  ConsumerState<SimpleShell> createState() => _SimpleShellState();
}

class _SimpleShellState extends ConsumerState<SimpleShell> {
  bool _clean = false;

  @override
  Widget build(BuildContext context) {
    ref.listen(selectedTabIndexProvider, (previous, next) {
      ref.read(simpleModeProvider.notifier).set(false);
    });
    // Until a frame exists (none taken, no loop running) the sky stands in
    // for it, so there is no clean view to toggle either.
    final hasFrame =
        ref.watch(lastCapturedFrameIdProvider) != null ||
        ref.watch(liveViewControllerProvider);
    final panels = !_clean || !hasFrame;
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
            },
            child: Focus(
              autofocus: true,
              child: SafeArea(
                child: Stack(
                  children: [
                    if (hasFrame)
                      Positioned.fill(
                        child: GestureDetector(
                          key: const Key('simple-frame'),
                          behavior: HitTestBehavior.opaque,
                          onTap: () => setState(() => _clean = !_clean),
                          child: const FrameViewer(),
                        ),
                      )
                    else
                      // Full bleed like a frame, so the panels keep their
                      // places; the sky simply stands in for the image.
                      Positioned.fill(
                        key: const Key('simple-planetarium'),
                        child: widget.planetarium,
                      ),
                    _Fade(
                      visible: panels,
                      child: Stack(
                        children: [
                          Positioned(
                            left: kSimpleInset,
                            right: kSimpleInset,
                            top: kSimpleInset,
                            height: kSimpleBarHeight,
                            child: _StatusStrip(),
                          ),
                          Positioned(
                            right: kSimpleInset,
                            top: kSimpleInset * 2 + kSimpleBarHeight,
                            bottom: kSimpleInset * 2 + kSimpleBarHeight,
                            width: kSimpleColumnWidth,
                            child: SimplePreviewPanel(),
                          ),
                          // The bottom row: guiding up to 340 px, then the
                          // switcher; an equal empty slot on the right (under
                          // Emergency Stop) keeps the switcher centred, and
                          // both slots shrink alike on a narrow window.
                          Positioned(
                            left: kSimpleInset,
                            right: kSimpleInset,
                            bottom: kSimpleInset,
                            height: kSimpleBarHeight,
                            child: Row(
                              children: [
                                Flexible(
                                  child: Align(
                                    alignment: Alignment.centerLeft,
                                    child: ConstrainedBox(
                                      constraints: BoxConstraints(
                                        maxWidth: 340,
                                      ),
                                      child: _GuidingBar(),
                                    ),
                                  ),
                                ),
                                SizedBox(width: kSimpleInset),
                                _ModeSwitcher(),
                                SizedBox(width: kSimpleInset),
                                Flexible(child: SizedBox(width: 340)),
                              ],
                            ),
                          ),
                        ],
                      ),
                    ),
                    // Emergency Stop never hides, not even in the clean view.
                    // Ring included, the same box as the column above it
                    // and the bottom-row panels beside it.
                    const Positioned(
                      right: kSimpleInset,
                      bottom: kSimpleInset,
                      width: kSimpleColumnWidth,
                      height: kSimpleBarHeight,
                      child: EmergencyStopButton(simple: true),
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

/// Fades the panels out for the clean view (220 ms, instant with reduced
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

class _StatusStrip extends ConsumerWidget {
  const _StatusStrip();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final shortcut = Theme.of(context).platform == TargetPlatform.macOS
        ? '⌘K'
        : 'Ctrl+K';
    final night = switch (ref.watch(nightModeProvider)) {
      AsyncData(:final value) => value,
      _ => false,
    };
    return SimplePanel(
      padding: const EdgeInsets.only(left: 8, right: 4),
      child: Row(
        children: [
          // Device dots only: the names would crowd the strip.
          const Flexible(
            child: EquipmentChipCompact(
              compact: true,
              child: SingleChildScrollView(
                scrollDirection: Axis.horizontal,
                child: TopEquipmentChips(),
              ),
            ),
          ),
          const SizedBox(
            height: 24,
            child: VerticalDivider(width: 24, color: AraColors.border),
          ),
          const _ActivityLabel(),
          const Spacer(),
          TextButton(
            onPressed: () =>
                leaveSimpleMode(ref, SimpleModeScreen.preview.fullUiTab),
            child: const Text('Show all options'),
          ),
          IconButton(
            icon: const Icon(Icons.search, size: 22),
            tooltip: 'Search settings ($shortcut)',
            onPressed: () => showCommandPalette(context),
          ),
          IconButton(
            icon: Icon(
              night ? Icons.nights_stay : Icons.nights_stay_outlined,
              size: 22,
              color: night ? AraColors.accentInfo : null,
            ),
            tooltip: night
                ? 'Night mode on (${Platform.isMacOS ? '⌘' : 'Ctrl+'}N)'
                : 'Night mode (${Platform.isMacOS ? '⌘' : 'Ctrl+'}N)',
            onPressed: () => ref.read(nightModeProvider.notifier).set(!night),
          ),
          const SettingsGearButton(),
        ],
      ),
    );
  }
}

/// What the camera is doing, in the strip after the device dots.
class _ActivityLabel extends ConsumerWidget {
  const _ActivityLabel();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final looping = ref.watch(liveViewControllerProvider);
    final capturing = ref.watch(captureProgressProvider).isCapturing;
    final (text, color) = looping
        ? ('Looping', AraColors.accentConnected)
        : capturing
        ? ('Taking a frame', AraColors.accentConnected)
        : ('Ready', AraColors.textSecondary);
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Icon(Icons.circle, size: 8, color: color),
        const SizedBox(width: AraSpace.s8),
        Text(text, style: kSimpleText),
      ],
    );
  }
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

class _ModeSwitcher extends ConsumerWidget {
  const _ModeSwitcher();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    return SimplePanel(
      padding: const EdgeInsets.all(AraSpace.s4),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        spacing: 2,
        children: [
          for (final m in SimpleModeScreen.values)
            SimpleSegment(
              label: m.label,
              selected: m == SimpleModeScreen.preview,
              radius: 7,
              // Not built yet in Simple Mode: open its full-UI tab.
              onTap: m == SimpleModeScreen.preview
                  ? null
                  : () => leaveSimpleMode(ref, m.fullUiTab),
            ),
        ],
      ),
    );
  }
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
  });

  final String label;
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
          alignment: Alignment.center,
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
