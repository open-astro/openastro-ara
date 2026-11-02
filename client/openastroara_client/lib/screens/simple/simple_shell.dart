import 'package:flutter/material.dart';

import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/frame_histogram.dart';
import '../../models/guider_status.dart';
import '../../models/polar_align.dart';
import '../../state/app_shell_state.dart';
import '../../state/guider/guide_step_state.dart';
import '../../state/guider/guider_state.dart';
import '../../state/imaging/last_frame_state.dart';
import '../../state/imaging/live_view_state.dart';
import '../../state/night_mode_state.dart';
import '../../state/polar_align/polar_align_state.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../state/sky_atlas/planetarium_selection_state.dart';
import '../../state/settings/settings_nav.dart';
import '../../state/simple_messages_state.dart';
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
import 'simple_autopilot.dart';
import 'simple_focus_panel.dart';
import 'simple_message_pills.dart';
import 'simple_polar_panel.dart';
import 'simple_plan_panel.dart';
import 'simple_preview_panel.dart';
import 'simple_safety_alarm.dart';

/// Inset from the frame edge and between panels.
const double kSimpleInset = 16;

/// Height of the status strip and of the bottom stats panels.
const double kSimpleBarHeight = 56;

/// Width of the left and right rails.
const double kSimpleRailWidth = 72;

/// The status pill's widest (top-left under the strip); Info starts right of
/// it.
const double kSimpleStatusMaxWidth = 300;

/// The message pills' widest (they share the top row with the status pill).
const double kSimpleMessagesMaxWidth = 420;

/// The Autopilot strip's or offer's row before it is measured.
const double kSimpleAutopilotRowHeight = 56;

/// Side of the compact Stop at the bottom right.
const double kSimpleStopSize = 60;

/// The modes, in the decided order.
enum SimpleModeScreen { autopilot, preview, focus, polarAlign, plan }

extension on SimpleModeScreen {
  String get label => switch (this) {
    SimpleModeScreen.autopilot => 'Autopilot',
    SimpleModeScreen.preview => 'Preview',
    SimpleModeScreen.polarAlign => 'Polar align',
    SimpleModeScreen.focus => 'Focus',
    SimpleModeScreen.plan => 'Plan',
  };

  /// The name on the 60 px rail, where "Polar align" doesn't fit.
  String get railLabel => switch (this) {
    SimpleModeScreen.polarAlign => 'P.A.',
    SimpleModeScreen.autopilot => 'Auto',
    _ => label,
  };

  IconData get icon => switch (this) {
    SimpleModeScreen.autopilot => Icons.rocket_launch_outlined,
    SimpleModeScreen.preview => Icons.visibility_outlined,
    SimpleModeScreen.focus => Icons.center_focus_strong,
    SimpleModeScreen.polarAlign => Icons.explore_outlined,
    SimpleModeScreen.plan => Icons.playlist_play,
  };

  /// The full-UI tab a mode opens until its Simple Mode screen exists
  /// (brief: Preview, Polar align and Focus open Live).
  // shortcut: Run always opens Sequence; the brief wants Live while a run is
  // imaging — switch on the sequence state when the Run screen is built.
  int get fullUiTab => switch (this) {
    SimpleModeScreen.autopilot ||
    SimpleModeScreen.preview ||
    SimpleModeScreen.polarAlign ||
    SimpleModeScreen.focus => kLiveTabIndex,
    SimpleModeScreen.plan => kRunTabIndex,
  };
}

/// Leave Simple Mode for the full UI, showing [tab].
void leaveSimpleMode(WidgetRef ref, int tab) {
  ref.read(selectedTabIndexProvider.notifier).select(tab);
  ref.read(simpleModeProvider.notifier).set(false);
}

/// The one popover open beside a rail, if any. [settings] is the chip's: the
/// current mode's options (Preview's exposure, Focus's panel).
enum _Popover { none, modes, settings, queue }

/// Simple Mode (ALP-4810): the latest frame fills the window and only the
/// shutter is always out. A right rail holds the mode (tap for the five
/// modes), a settings chip, the shutter and Stop; a left rail holds Info and
/// the Library; guiding and the histogram run along the bottom once they
/// have data; a small status sits top-left. Settings and modes open
/// beside the rail and close with a tap on the image. Tapping the image with
/// nothing open hides everything (the immersive view).
///
/// All five modes stay here; any tab jump made from here (Settings, a
/// device dot, ⌘K) opens the full UI.
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
  bool _system = false;

  /// The planetarium on demand (the left rail's Sky), over any frame.
  bool _sky = false;
  _Popover _popover = _Popover.none;
  SimpleModeScreen _mode = SimpleModeScreen.preview;

  /// Plan's sheet open from the queue panel: to add a target (also over a
  /// run in progress), or on queued target [_editing]'s rows.
  bool _adding = false;
  int? _editing;

  /// The queue panel's Add target and edit: the sheet over everything.
  void _openSheet({int? edit}) => setState(() {
    if (edit != null) {
      ref
          .read(simpleRunPlanProvider.notifier)
          .load(ref.read(simpleRunProvider).targets[edit].steps);
    }
    _editing = edit;
    _popover = _Popover.none;
    _adding = true;
  });

  // Leaving an edit drops its rows: the sheet plans the next target afresh.
  void _closeSheet() {
    if (_editing != null) ref.invalidate(simpleRunPlanProvider);
    _editing = null;
    _adding = false;
  }

  /// The rails' and the Autopilot row's heights, measured after each frame:
  /// the message pills start under the Autopilot row (its words wrap on a
  /// narrow window) and stop above the left rail; the right rail's panels
  /// hang from its top and stop above the plan squares.
  double _leftRailHeight = 0;
  double _rightRailHeight = 0;
  double _autopilotRowHeight = kSimpleAutopilotRowHeight;
  final _leftRailKey = GlobalKey();
  final _rightRailKey = GlobalKey();
  final _autopilotRowKey = GlobalKey();

  void _measure() => WidgetsBinding.instance.addPostFrameCallback((_) {
    final rail = _leftRailKey.currentContext?.size?.height;
    final right = _rightRailKey.currentContext?.size?.height;
    final row = _autopilotRowKey.currentContext?.size?.height;
    if (!mounted) return;
    if ((rail != null && rail != _leftRailHeight) ||
        (right != null && right != _rightRailHeight) ||
        (row != null && row != _autopilotRowHeight)) {
      setState(() {
        _leftRailHeight = rail ?? _leftRailHeight;
        _rightRailHeight = right ?? _rightRailHeight;
        _autopilotRowHeight = row ?? _autopilotRowHeight;
      });
    }
  });

  /// The Autopilot offer was dismissed (until Simple Mode opens again).
  bool _offerDismissed = false;

  /// What shows: the picked mode, or Autopilot's current step.
  SimpleModeScreen _shown(AutopilotStep? step) =>
      _mode != SimpleModeScreen.autopilot
      ? _mode
      : switch (step) {
          AutopilotStep.focus => SimpleModeScreen.focus,
          AutopilotStep.polarAlign => SimpleModeScreen.polarAlign,
          AutopilotStep.plan => SimpleModeScreen.plan,
          null => SimpleModeScreen.preview,
        };

  void _startAutopilot() {
    setState(() {
      _mode = SimpleModeScreen.autopilot;
      _popover = _Popover.settings;
    });
    ref.read(autopilotProvider.notifier).start();
  }

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
    _stopDisarm?.cancel();
    _focus.dispose();
    SystemChrome.setEnabledSystemUIMode(SystemUiMode.edgeToEdge);
    super.dispose();
  }

  // ⌘, / Ctrl+, opens Settings, which (for now) returns to the full UI.
  void _openSettings() =>
      ref.read(selectedTabIndexProvider.notifier).select(kOptionsTabIndex);

  /// The first ⌘. / Ctrl+. asks; a second within 3 s stops (the keyboard's
  /// stand-in for the shutter's 2 s hold, as the Emergency Stop does).
  int? _stopAsk;
  Timer? _stopDisarm;

  void _stopRun() {
    if (_shown(ref.read(autopilotProvider)) != SimpleModeScreen.plan) return;
    final phase = simpleRunPhase(
      ref.read(simpleRunProvider),
      ref.read(sequenceRunStateProvider).value,
    );
    if (phase != SimpleRunPhase.imaging &&
        phase != SimpleRunPhase.pausing &&
        phase != SimpleRunPhase.paused) {
      return;
    }
    final messages = ref.read(simpleMessagesProvider.notifier);
    _stopDisarm?.cancel();
    if (_stopAsk != null) {
      messages.dismiss(_stopAsk!);
      _stopAsk = null;
      ref.read(simpleRunProvider.notifier).stop();
      return;
    }
    final mac = const {
      TargetPlatform.macOS,
      TargetPlatform.iOS,
    }.contains(Theme.of(context).platform);
    _stopAsk = messages.show(
      SimpleMessageKind.result,
      'Press ${mac ? '⌘.' : 'Ctrl+.'} again to stop the run',
    );
    _stopDisarm = Timer(const Duration(seconds: 3), () {
      if (_stopAsk != null) messages.dismiss(_stopAsk!);
      _stopAsk = null;
    });
  }

  void _toggle(_Popover p) =>
      setState(() => _popover = _popover == p ? _Popover.none : p);

  /// A tap on the image closes what's open, or else toggles the immersive
  /// view (only once there is a frame to look at).
  void _tapImage(bool hasFrame) => setState(() {
    if (_popover != _Popover.none || _adding) {
      _popover = _Popover.none;
      _closeSheet();
    } else if (hasFrame) {
      _clean = !_clean;
    }
  });

  @override
  Widget build(BuildContext context) {
    ref.listen(selectedTabIndexProvider, (previous, next) {
      ref.read(simpleModeProvider.notifier).set(false);
    });
    // Autopilot opens each step on its panel, and ends on the plan.
    ref.listen(autopilotProvider, (previous, next) {
      if (_mode != SimpleModeScreen.autopilot) return;
      setState(() {
        if (next == null) _mode = SimpleModeScreen.plan;
        _popover = next == null ? _Popover.none : _Popover.settings;
      });
    });
    final pilot = ref.watch(autopilotProvider);
    final shown = _shown(pilot);
    // Polar align shows its own camera's frames while the routine runs.
    final polarFrame =
        shown == SimpleModeScreen.polarAlign &&
        simplePolarActive(ref.watch(polarAlignLiveProvider));
    final hasFrame =
        polarFrame ||
        ref.watch(lastCapturedFrameIdProvider) != null ||
        ref.watch(liveViewControllerProvider);
    final showSky = _sky || !hasFrame;
    final panels = !_clean || showSky;
    // Every mode reads the sequencer (the plan squares, the status pill), so
    // opening Simple Mode adopts a run in progress; only Plan acts on it.
    final runActive =
        shown == SimpleModeScreen.plan &&
        (ref.watch(sequenceRunStateProvider).value?.state?.isActive ?? false);
    final planSquares =
        simpleRunPhase(
          ref.watch(simpleRunProvider),
          ref.watch(sequenceRunStateProvider).value,
        ) !=
        SimpleRunPhase.none;
    const railTop = kSimpleInset * 2 + kSimpleBarHeight;
    final width = MediaQuery.sizeOf(context).width;
    final autopilotRow =
        _mode == SimpleModeScreen.autopilot ||
        (!_offerDismissed &&
            _mode == SimpleModeScreen.preview &&
            autopilotOffered(ref));
    // Messages share the top row with the status pill, which keeps at most
    // half of it; they start under the Autopilot strip when it shows.
    final statusRoom = math.min(
      kSimpleStatusMaxWidth,
      (width - kSimpleInset * 3) / 2,
    );
    final messagesWidth = math.min(
      kSimpleMessagesMaxWidth,
      width - kSimpleInset * 3 - statusRoom,
    );
    final messagesTop = autopilotRow
        ? railTop + _autopilotRowHeight + AraSpace.s12
        : railTop;
    _measure();
    final height =
        MediaQuery.sizeOf(context).height -
        MediaQuery.paddingOf(context).vertical;
    final leftRailTop = _leftRailHeight == 0
        ? height
        : railTop + (height - railTop * 2 - _leftRailHeight) / 2;
    // The right rail's panels run from its top down to the plan squares (or
    // the bottom row), scrolling inside when taller.
    final rightRailTop =
        railTop +
        (height -
                railTop -
                kSimpleInset * 2 -
                kSimpleStopSize -
                _rightRailHeight) /
            2;
    final popoverMaxHeight = _rightRailHeight == 0
        ? double.infinity
        : height -
              kSimpleInset * 3 -
              kSimpleBarHeight -
              (planSquares ? kSimplePlanSquareHeight + kSimpleInset : 0) -
              rightRailTop;
    final queuePanel = SimpleRunPanel(
      onAdd: () => _openSheet(),
      onEdit: (i) => _openSheet(edit: i),
    );
    final runSheet =
        shown == SimpleModeScreen.plan &&
        ((_popover == _Popover.settings && !runActive) || _adding);
    final sel = ref.watch(planetariumSelectionProvider);
    final motion = MediaQuery.disableAnimationsOf(context)
        ? Duration.zero
        : const Duration(milliseconds: 220);
    return Theme(
      data: simpleTheme(Theme.of(context)),
      child: Scaffold(
        backgroundColor: AraColors.bgPrimary,
        body: Stack(
          children: [
            ShellListeners(
              quiet: true,
              child: CallbackShortcuts(
                bindings: <ShortcutActivator, VoidCallback>{
                  const SingleActivator(
                    LogicalKeyboardKey.keyK,
                    meta: true,
                  ): () =>
                      showCommandPalette(context),
                  const SingleActivator(
                    LogicalKeyboardKey.keyK,
                    control: true,
                  ): () =>
                      showCommandPalette(context),
                  const SingleActivator(LogicalKeyboardKey.comma, meta: true):
                      _openSettings,
                  const SingleActivator(
                    LogicalKeyboardKey.comma,
                    control: true,
                  ): _openSettings,
                  // Hold-to-stop for keyboards: ⌘. / Ctrl+. twice stops a run.
                  const SingleActivator(LogicalKeyboardKey.period, meta: true):
                      _stopRun,
                  const SingleActivator(
                    LogicalKeyboardKey.period,
                    control: true,
                  ): _stopRun,
                },
                child: Focus(
                  focusNode: _focus,
                  child: SafeArea(
                    child: Stack(
                      children: [
                        Positioned.fill(
                          // Screen readers get the immersive toggle by name.
                          child: Semantics(
                            button: !showSky,
                            label: showSky
                                ? null
                                : _clean
                                ? 'Show controls'
                                : 'Hide controls',
                            child: GestureDetector(
                              key: const Key('simple-frame'),
                              behavior: HitTestBehavior.opaque,
                              onTap: () => _tapImage(!showSky),
                              child: !showSky
                                  ? (polarFrame
                                        ? const SimplePolarFrame()
                                        : const FrameViewer())
                                  // The sky: asked for, or standing in until there
                                  // is a frame.
                                  : KeyedSubtree(
                                      key: const Key('simple-planetarium'),
                                      child: widget.planetarium,
                                    ),
                            ),
                          ),
                        ),
                        if (!hasFrame)
                          Positioned.fill(
                            child: IgnorePointer(
                              child: Center(
                                child: Text(
                                  'Tap the shutter to take a frame',
                                  style: TextStyle(
                                    fontSize: 20,
                                    letterSpacing: 2,
                                    color: AraColors.textPrimary.withValues(
                                      alpha: 0.5,
                                    ),
                                  ),
                                ),
                              ),
                            ),
                          ),
                        // The message pills, top-left under the strip (under the
                        // Autopilot strip when it shows), down to the left rail;
                        // they stay in the immersive view.
                        AnimatedPositioned(
                          left: kSimpleInset,
                          top: panels ? messagesTop : kSimpleInset,
                          width: messagesWidth,
                          duration: motion,
                          curve: Curves.easeOut,
                          child: SimpleMessagePills(
                            maxHeight: panels
                                ? leftRailTop - messagesTop - kSimpleInset
                                : double.infinity,
                          ),
                        ),
                        // The camera's status, top-right under the strip (messages
                        // have the top-left), at most half the row; it stays in
                        // the immersive view and moves up into the corner.
                        AnimatedPositioned(
                          right: kSimpleInset,
                          top: panels ? railTop : kSimpleInset,
                          duration: motion,
                          curve: Curves.easeOut,
                          child: ConstrainedBox(
                            constraints: BoxConstraints(
                              maxWidth: (width - kSimpleInset * 3) / 2,
                            ),
                            child: const SimpleStatusPill(),
                          ),
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
                              // Info and System: fact tiles hugging the bottom-left
                              // corner, above the bottom row; System's row sits
                              // above Info's.
                              if (_info || _system)
                                Positioned(
                                  left: kSimpleInset,
                                  bottom: kSimpleInset * 2 + kSimpleBarHeight,
                                  child: Column(
                                    crossAxisAlignment:
                                        CrossAxisAlignment.start,
                                    spacing: AraSpace.s8,
                                    children: [
                                      if (_system) const SimpleSystemPanel(),
                                      if (_info) const SimpleInfoPanel(),
                                    ],
                                  ),
                                ),
                              // Guiding and the histogram along the bottom, each
                              // once it has data: 16 px from the edge and from Stop.
                              const Positioned(
                                left: kSimpleInset,
                                right:
                                    kSimpleInset * 2 +
                                    (kSimpleRailWidth + kSimpleStopSize) / 2,
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
                                  child: KeyedSubtree(
                                    key: _leftRailKey,
                                    child: _LeftRail(
                                      info: _info,
                                      system: _system,
                                      sky: showSky,
                                      onInfo: () =>
                                          setState(() => _info = !_info),
                                      onSystem: () =>
                                          setState(() => _system = !_system),
                                      // With no frame there is only the sky.
                                      onSky: hasFrame
                                          ? () => setState(() => _sky = !_sky)
                                          : null,
                                    ),
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
                                      key: _rightRailKey,
                                      mode: shown,
                                      named: _mode == SimpleModeScreen.autopilot
                                          ? SimpleModeScreen.autopilot
                                          : null,
                                      popover: _popover,
                                      onModes: () => _toggle(_Popover.modes),
                                      onSettings: () =>
                                          _toggle(_Popover.settings),
                                    ),
                                  ),
                                ),
                              ),
                              // Plan's sheet and Night done: a sheet centred over
                              // the image; while imaging, its panel sits beside
                              // the rail like any mode's.
                              // Between the status pill and the bottom row (the
                              // plan squares step aside while it shows); a tall
                              // sheet scrolls inside.
                              if (runSheet)
                                Positioned(
                                  left: 0,
                                  right: 0,
                                  top:
                                      railTop +
                                      kSimpleStatusHeight +
                                      kSimpleInset,
                                  bottom: kSimpleInset * 2 + kSimpleBarHeight,
                                  child: Center(
                                    child: SimpleRunSheet(
                                      editing: _editing,
                                      onShowSky: () =>
                                          setState(() => _sky = true),
                                      onPickInSky: () => setState(() {
                                        _sky = true;
                                        _popover = _Popover.none;
                                        _closeSheet();
                                      }),
                                      onCreated: () => setState(() {
                                        _popover = _Popover.none;
                                        _closeSheet();
                                      }),
                                      onLibrary: () => Navigator.of(context)
                                          .push(
                                            MaterialPageRoute<void>(
                                              builder: (_) =>
                                                  const ImageLibraryScreen(),
                                            ),
                                          ),
                                    ),
                                  ),
                                )
                              else if (_popover != _Popover.none)
                                Positioned(
                                  left: 0,
                                  top: 0,
                                  child: CompositedTransformFollower(
                                    link: _railLink,
                                    targetAnchor: Alignment.topLeft,
                                    followerAnchor: Alignment.topRight,
                                    offset: const Offset(-kSimpleInset, 0),
                                    child: ConstrainedBox(
                                      constraints: BoxConstraints(
                                        maxHeight: popoverMaxHeight,
                                      ),
                                      child: SingleChildScrollView(
                                        child: _popover == _Popover.queue
                                            ? queuePanel
                                            : _popover == _Popover.modes
                                            ? _ModeGrid(
                                                current: _mode,
                                                // Picking a mode only switches it; the
                                                // chip opens its panel (user, 2026-11-02).
                                                onPicked: (m) {
                                                  if (m ==
                                                      SimpleModeScreen
                                                          .autopilot) {
                                                    return _startAutopilot();
                                                  }
                                                  setState(() {
                                                    _mode = m;
                                                    _popover = _Popover.none;
                                                  });
                                                  // Picking a mode leaves Autopilot.
                                                  ref
                                                      .read(
                                                        autopilotProvider
                                                            .notifier,
                                                      )
                                                      .stop();
                                                },
                                              )
                                            : switch (shown) {
                                                SimpleModeScreen.focus =>
                                                  const SimpleFocusPanel(),
                                                SimpleModeScreen.polarAlign =>
                                                  const SimplePolarPanel(),
                                                SimpleModeScreen.plan =>
                                                  queuePanel,
                                                _ => const SizedBox(
                                                  width: 280,
                                                  child:
                                                      SimpleSettingsPopover(),
                                                ),
                                              },
                                      ),
                                    ),
                                  ),
                                ),
                              // Autopilot's steps, or the offer to start it, centred
                              // under the strip.
                              if (autopilotRow)
                                Positioned(
                                  left: 0,
                                  right: 0,
                                  top: railTop,
                                  child: Center(
                                    child: KeyedSubtree(
                                      key: _autopilotRowKey,
                                      child: _mode == SimpleModeScreen.autopilot
                                          ? const AutopilotStrip()
                                          : AutopilotOffer(
                                              onStart: _startAutopilot,
                                              onDismiss: () => setState(
                                                () => _offerDismissed = true,
                                              ),
                                            ),
                                    ),
                                  ),
                                ),
                              // The plan squares hug the bottom-right corner, just
                              // above the bottom row, growing left over the right
                              // half (the Info tiles have the left).
                              if (shown == SimpleModeScreen.plan && !runSheet)
                                Positioned(
                                  left: width / 2,
                                  right: kSimpleInset,
                                  bottom: kSimpleInset * 2 + kSimpleBarHeight,
                                  child: Align(
                                    alignment: Alignment.bottomRight,
                                    child: SimpleRunTracker(
                                      onTap: () => _toggle(_Popover.queue),
                                    ),
                                  ),
                                ),
                              // The sky's pick, offered to Plan.
                              if (showSky && sel != null && !runSheet)
                                Positioned(
                                  left: 0,
                                  right: 0,
                                  bottom: kSimpleInset * 2 + 40,
                                  child: Center(
                                    child: _SkyPick(
                                      selection: sel,
                                      onImage: () => setState(() {
                                        _mode = SimpleModeScreen.plan;
                                        // Over a run, the sheet adds to its queue.
                                        _popover = runActive
                                            ? _Popover.none
                                            : _Popover.settings;
                                        _adding = runActive;
                                        _sky = false;
                                      }),
                                    ),
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
            // Over everything, outside the safe area: the one popup kept.
            const SimpleSafetyAlarm(),
          ],
        ),
      ),
    );
  }
}

/// The object picked in the sky, over it: its name, type and altitude, and
/// Image this, which opens Plan's sheet with it.
class _SkyPick extends StatelessWidget {
  const _SkyPick({required this.selection, required this.onImage});

  final PlanetariumSelection selection;
  final VoidCallback onImage;

  @override
  Widget build(BuildContext context) => SimplePanel(
    padding: const EdgeInsets.fromLTRB(16, 10, 10, 10),
    child: Row(
      mainAxisSize: MainAxisSize.min,
      spacing: AraSpace.s16,
      children: [
        Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              selection.name,
              style: kSimpleText.copyWith(fontWeight: FontWeight.w600),
            ),
            Text(
              [
                if (selection.kind.isNotEmpty) selection.kind,
                simpleTargetStatus(selection),
              ].join(' · '),
              style: kSimpleText.copyWith(
                fontSize: 12,
                color: selection.belowHorizon
                    ? AraColors.accentBusy
                    : AraColors.textSecondary,
              ),
            ),
          ],
        ),
        FilledButton(
          key: const Key('simple-sky-image-this'),
          onPressed: onImage,
          child: const Text('Image this'),
        ),
      ],
    ),
  );
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

/// Device dots (connected gear only), then Search, Night mode, Show all
/// options and Settings, in the full UI's top-bar order.
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
            icon: const Icon(Icons.search, size: 22),
            tooltip: 'Search settings',
            onPressed: () => showCommandPalette(context),
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
          // The full UI's Simple Mode button, mirrored: the way back.
          IconButton(
            icon: const Icon(Icons.fit_screen, size: 22),
            tooltip: 'Show all options',
            onPressed: () =>
                leaveSimpleMode(ref, SimpleModeScreen.preview.fullUiTab),
          ),
          IconButton(
            icon: const Icon(Icons.settings_outlined, size: 22),
            tooltip: 'Settings',
            onPressed: () => ref
                .read(selectedTabIndexProvider.notifier)
                .select(kOptionsTabIndex),
          ),
        ],
      ),
    );
  }
}

/// One labelled rail button: an icon over a short caption, or (no icon) a
/// caption of up to two lines.
class _RailButton extends StatelessWidget {
  const _RailButton({
    super.key,
    this.icon,
    required this.label,
    required this.onTap,
    this.selected = false,
    this.tall = false,
  });

  final IconData? icon;

  /// The settings chip: tall enough for the icon over two lines (P.A.'s
  /// setup, Plan's time), in every mode so the rail never changes size.
  final bool tall;
  final String label;
  final VoidCallback onTap;
  final bool selected;

  @override
  Widget build(BuildContext context) => Semantics(
    button: true,
    selected: selected,
    label: label,
    child: Material(
      color: selected ? AraColors.selectionTint : Colors.transparent,
      borderRadius: BorderRadius.circular(8),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(8),
        child: SizedBox(
          width: 60,
          height: tall ? 72 : 56,
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              if (icon != null) ...[
                Icon(
                  icon,
                  size: 24,
                  color: selected
                      ? AraColors.textPrimary
                      : AraColors.textSecondary,
                ),
                const SizedBox(height: 2),
              ],
              Text(
                label,
                maxLines: icon == null || tall ? 2 : 1,
                textAlign: TextAlign.center,
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

/// The rails are a fixed-width instrument cluster: their labels follow the
/// user's text size up to 1.3x (the panels follow it all the way).
Widget _railText(Widget rail) =>
    MediaQuery.withClampedTextScaling(maxScaleFactor: 1.3, child: rail);

class _LeftRail extends StatelessWidget {
  const _LeftRail({
    required this.info,
    required this.system,
    required this.sky,
    required this.onInfo,
    required this.onSystem,
    required this.onSky,
  });

  final bool info;
  final bool system;
  final bool sky;
  final VoidCallback onInfo;
  final VoidCallback onSystem;
  final VoidCallback? onSky;

  @override
  Widget build(BuildContext context) => _railText(
    SimplePanel(
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
            key: const Key('simple-system'),
            icon: Icons.memory,
            label: 'System',
            selected: system,
            onTap: onSystem,
          ),
          _RailButton(
            key: const Key('simple-library'),
            icon: Icons.photo_library_outlined,
            label: 'Library',
            onTap: () => Navigator.of(context).push(
              MaterialPageRoute<void>(
                builder: (_) => const ImageLibraryScreen(),
              ),
            ),
          ),
        ],
      ),
    ),
  );
}

/// The mode (tap for the five) on top of the shutter, the settings chip
/// under it; Stop sits on its own below.
class _RightRail extends ConsumerWidget {
  const _RightRail({
    super.key,
    required this.mode,
    this.named,
    required this.popover,
    required this.onModes,
    required this.onSettings,
  });

  final SimpleModeScreen mode;

  /// Overrides the mode named on the rail (Autopilot shows its own).
  final SimpleModeScreen? named;
  final _Popover popover;
  final VoidCallback onModes;
  final VoidCallback onSettings;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final modesOpen = popover == _Popover.modes;
    return _railText(
      SimplePanel(
        padding: const EdgeInsets.symmetric(
          vertical: AraSpace.s8,
          horizontal: 6,
        ),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          spacing: AraSpace.s8,
          children: [
            // The mode on top of the shutter; the settings chip under it.
            Semantics(
              button: true,
              label: 'Mode: ${(named ?? mode).label}',
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
                        (named ?? mode).railLabel,
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
            switch (mode) {
              SimpleModeScreen.focus => const SimpleFocusShutter(),
              SimpleModeScreen.polarAlign => const SimplePolarShutter(),
              SimpleModeScreen.plan => const SimpleRunShutter(),
              _ => const SimpleShutter(),
            },
            _RailButton(
              key: const Key('simple-settings'),
              icon: Icons.tune,
              tall: true,
              label: _chipLabel(ref),
              selected: popover == _Popover.settings,
              onTap: onSettings,
            ),
          ],
        ),
      ),
    );
  }

  // In Autopilot the chip names the step whose settings it shows ("Focus"
  // over "Auto"), so they don't read as random.
  String _chipLabel(WidgetRef ref) {
    final label = switch (mode) {
      SimpleModeScreen.focus => simpleFocusSummary(ref),
      SimpleModeScreen.plan => simpleRunChip(
        ref.watch(simpleRunProvider),
        ref.watch(simpleRunPlanProvider),
      ),
      SimpleModeScreen.polarAlign => simplePolarSummary(
        ref.watch(simplePolarSettingsProvider).value ??
            const PolarAlignSettings(),
      ),
      _ => simpleSettingsSummary(ref),
    };
    return named == null
        ? label
        : '${mode.railLabel}\n${label.split('\n').first}';
  }
}

/// The five modes as a grid beside the rail.
class _ModeGrid extends ConsumerWidget {
  const _ModeGrid({required this.current, required this.onPicked});

  final SimpleModeScreen current;
  final ValueChanged<SimpleModeScreen> onPicked;

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
                color: m == current ? AraColors.selectionBg : kSimpleTileFill,
                borderRadius: BorderRadius.circular(8),
                child: InkWell(
                  borderRadius: BorderRadius.circular(8),
                  onTap: () => onPicked(m),
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
    // A lone one fills the row; both split it equally, guiding on the left.
    return Row(
      spacing: kSimpleInset,
      children: [
        if (guiding) const Expanded(child: _GuidingBar()),
        if (histogram != null) Expanded(child: _HistogramBar(histogram)),
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
/// 44 px tall (the brief's touch minimum) with square-ish corners, Selection
/// Blue when selected.
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
          height: 44,
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
