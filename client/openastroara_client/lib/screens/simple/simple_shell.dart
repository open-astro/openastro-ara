import 'package:lat_lng_to_timezone/lat_lng_to_timezone.dart' as tz_map;

import 'simple_search.dart';

import 'package:flutter/cupertino.dart';
import 'package:flutter/material.dart';

import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/diagnostics/diagnostics_state.dart';
import '../../state/library/live_library_state.dart' show libraryFilterProvider;
import '../../state/time_sync_state.dart' show timeSyncApiProvider;
import '../../state/sky_atlas/tonight_sky_state.dart'
    show planningSettingsBootstrapProvider;
import '../../models/frame_histogram.dart';
import '../../models/guider_status.dart';
import '../../models/discovered_device.dart';
import '../../services/equipment_device_api.dart' show describeEquipmentError;
import '../../state/settings/equipment_connection_state.dart'
    show EquipmentDeviceType;
import '../../widgets/equipment/alpaca_chooser_dialog.dart';
import '../../services/equipment_driver_api.dart';
import '../../models/polar_align.dart';
import '../../state/app_shell_state.dart';
import '../../state/launch_gate_state.dart';
import '../../state/profile_management_state.dart';
import '../../state/saved_server_state.dart';
import '../../state/guider/guide_step_state.dart';
import '../../state/guider/guider_state.dart';
import '../../state/imaging/last_frame_state.dart';
import '../../state/imaging/live_view_frame_state.dart';
import '../../state/imaging/live_view_state.dart';
import '../../state/night_mode_state.dart';
import '../../state/polar_align/polar_align_state.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../state/sky_atlas/planetarium_selection_state.dart';
import '../../state/settings/cooling_fan_state.dart';
import '../../state/settings/settings_nav.dart';
import '../../state/simple_messages_state.dart';
import '../../state/simple_mode_state.dart';
import '../../util/app_licenses.dart';
import '../../state/simple_preflight_state.dart';
export '../../state/simple_preflight_state.dart' show SimpleModeScreen, SimpleModeScreenLabels;

import 'dart:ui' show ImageFilter;

import '../../app_version.dart';
import '../../l10n/l10n.dart';
import '../../state/app_locale_state.dart';
import '../../services/plate_solve_database_api.dart';
import '../../services/server_maintenance_api.dart';
import '../../models/equipment_device_status.dart';
import '../../state/equipment/camera_state.dart';
import '../../state/equipment/dome_state.dart';
import '../../state/equipment/equipment_device_state.dart';
import '../../state/equipment/filter_wheel_state.dart';
import '../../state/equipment/flat_panel_state.dart';
import '../../state/equipment/focuser_state.dart';
import '../../state/equipment/mount_state.dart';
import '../../models/mount_status.dart' show MountCapabilities;
import '../../state/equipment/rotator_state.dart';
import '../../state/equipment/weather_state.dart';
import '../../state/ws/ws_providers.dart';
import '../../state/settings/optics_settings_state.dart';
import '../../state/settings/site_settings_state.dart';
import '../../util/gps_site_fill.dart';
import '../../util/friendly_error.dart';
import '../../services/profile_api.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../util/reduce_motion.dart';
import '../../widgets/ara_mark.dart';
import '../../widgets/emergency_stop_button.dart';
import '../../widgets/equipment/equipment_glyphs.dart';
import '../../state/equipment/safety_monitor_state.dart';
import '../../models/switch_device.dart';
import '../../state/equipment/switch_state.dart';
import '../../state/faults/faults_state.dart';
import '../../widgets/status_indicator.dart' show StatusDot, StatusLevel;
import '../../widgets/equipment/equipment_status_chip.dart';
import '../../widgets/equipment/guider_chip.dart'
    show guiderChipLevel, guiderStatusLevel;
import '../../widgets/equipment_chip.dart'
    show EquipmentChipCompact, EquipmentChipHideDisconnected;
import '../../widgets/imaging/frame_viewer.dart';
import '../../widgets/imaging/guiding_strip.dart';
import '../../widgets/imaging/histogram_strip.dart' show frameHistogramProvider;
import '../../widgets/sky_atlas/stellarium_view.dart';
import '../library/image_library_screen.dart';
import '../settings/settings_shell.dart';
import '../shell_listeners.dart';
import 'simple_preflight.dart';
import 'simple_device_windows.dart';
import 'simple_rig_storage.dart';
import 'simple_focus_panel.dart';
import '../../state/focus/autofocus_live_state.dart' show autofocusLiveProvider;
import '../../widgets/focus/focusing_pane.dart' show MainFocusCard;
import '../../state/focus/guide_focus_state.dart' show guideFocusProvider;
import '../../state/settings/phd2_settings_state.dart'
    show phd2SettingsProvider;
import '../../state/focus/bahtinov_focus_state.dart'
    show MainFocusMethod, bahtinovFocusProvider;
import 'simple_message_pills.dart';
import 'simple_guide_panel.dart'
    show
        SimpleGuidePanel,
        SimpleGuideShutter,
        simpleGuideActive,
        simpleGuideSummary;
import 'simple_polar_panel.dart';
import '../album/sky_album_screen.dart';
import 'simple_plan_panel.dart';
import 'simple_preview_panel.dart';
import 'simple_wallpaper.dart';

/// Inset from the frame edge and between panels.
const double kSimpleInset = 16;

/// Height of the status strip and of the bottom stats panels.
const double kSimpleBarHeight = 56;

/// Width of the left and right rails.
const double kSimpleRailWidth = 72;

/// A device window's width (at 1x text), and the widest rail menu's (the
/// mode grid with its padding): together they say whether both fit.
const double kSimpleDeviceWindowWidth = 360;
const double kSimpleWidestPopover = 316;

/// The status pill's widest (top-left under the strip); Info starts right of
/// it.
const double kSimpleStatusMaxWidth = 300;

/// The message pills' widest (they share the top row with the status pill).
const double kSimpleMessagesMaxWidth = 420;

/// The Preflight strip's or offer's row before it is measured.
const double kSimplePreflightRowHeight = 56;

/// Side of the compact Stop at the bottom right.
const double kSimpleStopSize = 60;

/// The modes, in the decided order.

/// The one popover open beside a rail, if any. [settings] is the chip's: the
/// current mode's options (Preview's exposure, Focus's panel).
/// [menu] is the top bar's Settings menu.
enum _Popover {
  none,
  modes,
  settings,
  queue,
  menu,
  search,
  ara;

  /// Opens in the device window's top-left corner, so the two never stay up
  /// together, at any width (#1674).
  bool get topLeft => this == search || this == ara;
}

/// Simple Mode (ALP-4810): the latest frame fills the window and only the
/// shutter is always out. A right rail holds the mode (tap for the five
/// modes), a settings chip, the shutter and Stop; a left rail holds Info and
/// the Library; guiding and the histogram run along the bottom once they
/// have data; a small status sits top-left. Settings and modes open
/// beside the rail and close with a tap on the image. Tapping the image with
/// nothing open hides everything (the immersive view).
///
/// All five modes stay here: Settings (the gear's menu, ⌘, a device dot, Open
/// Storage) opens over Simple Mode. The way out is the strip's Full UI button.
class SimpleShell extends ConsumerStatefulWidget {
  const SimpleShell({
    super.key,
    this.planetarium = const StellariumView(bare: true),
  });

  /// Shown only when Sky is asked for, over a frame or over the wallpaper;
  /// tests pass a stand-in for the webview.
  final Widget planetarium;

  @override
  ConsumerState<SimpleShell> createState() => _SimpleShellState();
}

class _SimpleShellState extends ConsumerState<SimpleShell> {
  bool _clean = false;
  bool _info = false;

  /// The planetarium on demand (the left rail's Sky), over any frame.
  bool _sky = false;
  bool _hadFrame = false;
  _Popover _popover = _Popover.none;

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
    // A discard question can't outlive the edit it was about: its Discard
    // would replay a stale tap.
    if (_discardAsk case final id?) {
      ref.read(simpleMessagesProvider.notifier).dismiss(id);
      _discardAsk = null;
    }
  }

  /// The rails' and the Preflight row's heights, measured after each frame:
  /// the message pills start under the Preflight row (its words wrap on a
  /// narrow window) and stop above the left rail; the right rail's panels
  /// hang from its top and stop above the plan squares.
  double _leftRailHeight = 0;
  double _rightRailHeight = 0;
  double _simplePreflightRowHeight = kSimplePreflightRowHeight;
  double _statusHeight = 0;
  double _messagesHeight = 0;
  final _messagesKey = GlobalKey();
  double _statusStackHeight = 0;
  final _statusKey = GlobalKey();
  final _leftRailKey = GlobalKey();
  final _rightRailKey = GlobalKey();
  final _simplePreflightRowKey = GlobalKey();

  void _measure() => WidgetsBinding.instance.addPostFrameCallback((_) {
    final rail = _leftRailKey.currentContext?.size?.height;
    final right = _rightRailKey.currentContext?.size?.height;
    final row = _simplePreflightRowKey.currentContext?.size?.height;
    final messages = _messagesKey.currentContext?.size?.height ?? 0;
    final status = _statusKey.currentContext?.size?.height ?? 0;
    if (!mounted) return;
    if ((rail != null && rail != _leftRailHeight) ||
        (right != null && right != _rightRailHeight) ||
        (row != null && row != _simplePreflightRowHeight) ||
        messages != _messagesHeight ||
        status != _statusStackHeight) {
      setState(() {
        _statusStackHeight = status;
        _leftRailHeight = rail ?? _leftRailHeight;
        _rightRailHeight = right ?? _rightRailHeight;
        _simplePreflightRowHeight = row ?? _simplePreflightRowHeight;
        _messagesHeight = messages;
      });
    }
  });

  int? _dewPill;

  /// #1564 — one warning for each time dew opens; a reconnect's snapshot counts.
  Future<void> _syncDewPill(
    DiagnosticsSnapshot? previous,
    DiagnosticsSnapshot next,
  ) async {
    const dew = 'dew_formation';
    final open = next.openTypes.contains(dew);
    final messages = ref.read(simpleMessagesProvider.notifier);
    if (open && !(previous?.openTypes.contains(dew) ?? false)) {
      // Awaited: at startup the list may not have loaded yet.
      final devices = await ref
          .read(switchListProvider.future)
          .catchError((Object _) => const <SwitchDevice>[]);
      // Dew may have cleared during the wait.
      if (!mounted || !ref.read(diagnosticsStateProvider).openTypes.contains(dew)) {
        return;
      }
      final switchUp = devices.any(
        (d) => d.connectionState == SwitchConnectionState.connected,
      );
      _dewPill = messages.show(
        SimpleMessageKind.warning,
        context.l10n.simpleDewWarning,
        actions: [
          if (switchUp)
            SimpleMessageAction(
              context.l10n.shellSwitches,
              () => _openFromPill(SimpleStatusOrigin.switches),
            ),
        ],
      );
    } else if (!open && _dewPill != null) {
      messages.dismiss(_dewPill!);
      _dewPill = null;
    }
  }

  void _setStatusHeight(double height) {
    if (mounted && height != _statusHeight) {
      setState(() => _statusHeight = height);
    }
  }

  void _startSimplePreflight() {
    setState(() => _popover = _Popover.settings);
    ref.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.preflight);
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
    // Brings the live view notifier up, so a loop the daemon still runs is
    // adopted (the Loop shows as running and its square stops it).
    ref.read(liveViewFrameProvider);
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) _focus.requestFocus();
    });
    // A frame late: this also fires from initState, and a pill changes a provider.
    ref.listenManual(diagnosticsStateProvider, (previous, next) {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) unawaited(_syncDewPill(previous, next));
      });
    }, fireImmediately: true);
    ref.listenManual(coolingFanAskDueProvider, (_, due) {
      if (due) unawaited(_askCoolingFan());
    }, fireImmediately: true);
    ref.listenManual(mountProvider, (previous, next) {
      final caps = next.value?.capabilities;
      final was = previous?.value?.capabilities;
      if (caps != null &&
          (was == null || previous?.value?.deviceId != next.value?.deviceId)) {
        _opticsFromMount(caps);
      }
    }, fireImmediately: true);
  }

  /// The profile owns the telescope; a value it doesn't know (0) is filled
  /// from what the connected mount reports and saved to the profile.
  Future<void> _opticsFromMount(MountCapabilities caps) async {
    if (caps.apertureMm == null && caps.focalLengthMm == null) return;
    final api = ref.read(profileApiProvider);
    if (api == null) return;
    final optics = ref.read(opticsSettingsProvider.notifier);
    try {
      // The profile's own values first: never fill over one not loaded yet.
      await optics.hydrateFromServer(api);
      if (!mounted) return;
      final known = ref.read(opticsSettingsProvider);
      var changed = false;
      if (known.apertureMm <= 0 && caps.apertureMm != null) {
        optics.setApertureMm(caps.apertureMm!);
        changed = true;
      }
      if (known.focalLengthMm <= 0 && caps.focalLengthMm != null) {
        optics.setFocalLengthMm(caps.focalLengthMm!);
        changed = true;
      }
      if (changed) await optics.persistToServer(api);
    } catch (e) {
      debugPrint('[simple] telescope from the mount: $e');
    }
  }

  late final _onTop = ref.read(simpleShellOnTopProvider.notifier);
  late SimpleMessagesNotifier _messages;

  // A frame late: providers can't change while the tree builds or unmounts.
  void _publishOnTop(bool onTop) => WidgetsBinding.instance
      .addPostFrameCallback((_) => _onTop.set(onTop));

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _publishOnTop(ModalRoute.isCurrentOf(context) ?? true);
    _messages = ref.read(simpleMessagesProvider.notifier);
  }

  @override
  void dispose() {
    _publishOnTop(false);
    final dewPill = _dewPill;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      _messages.dismissQuestions();
      // Its button belongs to this shell too, and the next one shows its own.
      if (dewPill != null) _messages.dismiss(dewPill);
    });
    _stopDisarm?.cancel();
    _focus.dispose();
    SystemChrome.setEnabledSystemUIMode(SystemUiMode.edgeToEdge);
    _guiderPoll?.cancel();
    super.dispose();
  }

  /// The full Settings page over Simple Mode, on whatever panel was picked
  /// last. Devices open their windows (#1662), Logs and Storage sit in Rig
  /// (#1659, #1660); any other panel still lands here.
  void _openSettings() {
    setState(() => _popover = _Popover.none);
    unawaited(
      Navigator.of(context)
          .push(MaterialPageRoute<void>(builder: (_) => const _SettingsPage())),
    );
  }

  /// The device window open (from its dot), by its Settings panel id, or
  /// null. Separate from [_popover]: it sits top-left and stays open beside a
  /// rail menu when they don't overlap.
  String? _device;

  /// The devices with a window of their own, by their Settings panel id.
  static const _deviceWindows = {
    'eq.camera': SimpleCameraWindow(),
    'eq.mount': SimpleMountWindow(),
    'eq.focuser': SimpleFocuserWindow(),
    'eq.filterwheel': SimpleFilterWheelWindow(),
    'eq.rotator': SimpleRotatorWindow(),
    'eq.switch': SimpleSwitchesWindow(),
    'eq.flat': SimpleFlatPanelWindow(),
    'eq.weather': SimpleWeatherWindow(),
    'eq.safety': SimpleSafetyMonitorWindow(),
    'eq.dome': SimpleDomeWindow(),
    'eq.guider': SimpleGuiderWindow(),
  };

  /// Whether the camera window and a rail menu fit side by side: the window
  /// on the left, the widest menu left of the rail, with the insets.
  bool _sideBySide(BuildContext context) {
    final scale = simpleRailScale(context);
    return MediaQuery.sizeOf(context).width >=
        kSimpleDeviceWindowWidth * scale +
            kSimpleWidestPopover * scale +
            kSimpleRailWidth * scale * 2 +
            kSimpleInset * 5;
  }

  /// Set while a jump to the Settings tab is turned back (see build).
  bool _restoringTab = false;

  /// The first ⌘. / Ctrl+. asks; a second within 3 s stops (the keyboard's
  /// stand-in for the shutter's 2 s hold, as the Emergency Stop does).
  int? _stopAsk;
  Timer? _stopDisarm;

  void _stopRun() {
    if (ref.read(simpleShowingProvider) != SimpleModeScreen.plan) return;
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
      SimpleMessageKind.question,
      context.l10n.shellStopAgain(mac ? '⌘.' : 'Ctrl+.'),
    );
    _stopDisarm = Timer(const Duration(seconds: 3), () {
      if (_stopAsk != null) messages.dismiss(_stopAsk!);
      _stopAsk = null;
    });
  }

  SimpleModeScreen? _lastShown;

  // Simple Mode re-reads the guider every 2 s while it is doing anything:
  // nothing else polls it here, and some changes (a stop that lands seconds
  // after the ask) push nothing (rig, 2026-11-04).
  Timer? _guiderPoll;

  void _syncGuiderPoll(bool active) {
    if (active) {
      _guiderPoll ??= Timer.periodic(
        const Duration(seconds: 2),
        (_) => ref.read(guiderStatusProvider.notifier).refresh(),
      );
    } else {
      _guiderPoll?.cancel();
      _guiderPoll = null;
    }
  }

  /// Leaving Manual before Finish asks for the mask to come off (DESIGN.md
  /// Focus; ALP-4982), here once Focus has gone.
  void _askMaskOff() {
    if (!mounted) return;
    final bahtinov = ref.read(bahtinovFocusProvider);
    if (!bahtinov.status.hasMeasurement || bahtinov.finished) return;
    final l10n = context.l10n;
    ref
        .read(simpleMessagesProvider.notifier)
        .show(
          SimpleMessageKind.question,
          l10n.bahtinovMaskQuestion,
          detail: l10n.bahtinovMaskQuestionDetail,
          actions: [
            SimpleMessageAction(l10n.bahtinovNotYet, () {}),
            SimpleMessageAction(
              l10n.bahtinovMaskRemoved,
              () =>
                  unawaited(ref.read(bahtinovFocusProvider.notifier).finish()),
            ),
          ],
        );
  }

  /// #1700 — a cooling camera and a switch, no fan chosen: a pill asks once.
  Future<void> _askCoolingFan() async {
    if (!await claimCoolingFanAsk(ref.read) || !mounted) return;
    final l10n = context.l10n;
    ref
        .read(simpleMessagesProvider.notifier)
        .show(
          SimpleMessageKind.question,
          l10n.coolingFanAsk,
          origin: SimpleStatusOrigin.camera,
          actions: [
            SimpleMessageAction(l10n.coolingFanNotNow, () {}),
            SimpleMessageAction(
              l10n.coolingFanChoose,
              () => _openFromPill(SimpleStatusOrigin.camera),
            ),
          ],
        );
  }

  /// A tap on the status pill: what it is about, open (user, 2026-11-04).
  void _openFromPill(SimpleStatusOrigin origin) {
    final window = switch (origin) {
      SimpleStatusOrigin.mount => 'eq.mount',
      SimpleStatusOrigin.camera => 'eq.camera',
      SimpleStatusOrigin.focuser => 'eq.focuser',
      SimpleStatusOrigin.filterWheel => 'eq.filterwheel',
      SimpleStatusOrigin.rotator => 'eq.rotator',
      SimpleStatusOrigin.switches => 'eq.switch',
      SimpleStatusOrigin.flatPanel => 'eq.flat',
      SimpleStatusOrigin.dome => 'eq.dome',
      SimpleStatusOrigin.safetyMonitor => 'eq.safety',
      SimpleStatusOrigin.guiderCamera => 'eq.guider',
      _ => null,
    };
    if (window != null) {
      setState(() {
        _clean = false;
        _device = window;
        if (_popover.topLeft || !_sideBySide(context)) {
          _popover = _Popover.none;
        }
      });
      return;
    }
    final mode = switch (origin) {
      SimpleStatusOrigin.plan => SimpleModeScreen.plan,
      SimpleStatusOrigin.guide => SimpleModeScreen.guide,
      SimpleStatusOrigin.focus => SimpleModeScreen.focus,
      SimpleStatusOrigin.polar => SimpleModeScreen.polarAlign,
      _ => SimpleModeScreen.preview,
    };
    // A Preflight on that step keeps going; anything else switches mode.
    if (ref.read(simpleShowingProvider) != mode) _pickMode(mode);
    setState(() {
      _clean = false;
      _popover = _Popover.settings;
    });
  }

  void _pickMode(SimpleModeScreen m) {
    if (m == SimpleModeScreen.preflight) return _startSimplePreflight();
    setState(() => _popover = _Popover.none);
    // Picking a mode leaves Preflight.
    ref.read(simpleShowingProvider.notifier).pick(m);
  }

  /// The Settings sheet's section to open on (a search hit names one).
  String _menuSection = 'Rig';

  /// Counts Open Storage jumps: the Rig page scrolls to Storage and the new
  /// key rebuilds the sheet when it is already open.
  int _revealStorage = 0;

  /// Open Storage's one scroll: only the sheet it opens jumps to Storage.
  bool _revealStoragePending = false;

  void _openMenu({String section = 'Rig'}) {
    _menuSection = section;
    _toggle(_Popover.menu);
  }

  /// A search hit, opened where it lives in Simple Mode.
  void _openHit(SimpleSearchHit hit) {
    setState(() => _popover = _Popover.none);
    switch (hit) {
      case SimpleSkyHit(:final suggestion):
        // As Plan's search: the sky, pointed at it, its card offering Image this.
        setState(() => _sky = true);
        ref
            .read(planetariumSelectionProvider.notifier)
            .set(simplePickDso(ref, suggestion.dso));
        ref.read(planetariumSearchProvider.notifier).search(suggestion.label);
      case SimpleSettingHit(section: 'Guide'):
        _pickMode(SimpleModeScreen.guide);
      case SimpleSettingHit(:final section):
        _openMenu(section: section);
      case SimpleModeHit(:final mode):
        _pickMode(mode);
      case SimpleImageHit(:final target):
        ref.read(libraryFilterProvider.notifier).setQuery(target);
        unawaited(
          Navigator.of(context).push(
            MaterialPageRoute<void>(builder: (_) => const ImageLibraryScreen()),
          ),
        );
    }
  }

  /// The Sky Album; a card's action closes it and opens Plan's sheet with
  /// the card's object picked, as Image this does from the sky. Nothing is
  /// queued.
  void _openAlbum() => unawaited(
    Navigator.of(context).push(
      MaterialPageRoute<void>(
        builder: (albumContext) => SkyAlbumScreen(
          onImageTarget: (d) {
            // Read while the album still watches the ranker (it auto-disposes).
            final sel = simplePickDso(ref, d);
            Navigator.of(albumContext).pop();
            ref.read(planetariumSelectionProvider.notifier).set(sel);
            _imagePick();
          },
        ),
      ),
    ),
  );

  /// Image this, from the sky's card or the album: Plan's sheet on the
  /// selection.
  void _imagePick() => setState(() {
    final shown = ref.read(simpleShowingProvider);
    final runActive =
        shown == SimpleModeScreen.plan &&
        (ref.read(sequenceRunStateProvider).value?.state?.isActive ?? false);
    // Plan shows already in Preflight's plan step; from any other screen the
    // pick goes to Plan and leaves Preflight.
    if (shown != SimpleModeScreen.plan) {
      ref.read(simpleShowingProvider.notifier).pick(SimpleModeScreen.plan);
    }
    // An edit left open is over: the sheet plans the pick.
    _closeSheet();
    // Over a run, the sheet adds to its queue.
    _popover = runActive ? _Popover.none : _Popover.settings;
    _adding = runActive;
    _sky = false;
  });

  int? _discardAsk;

  /// A tap that would close the sheet over an edit with unsaved rows asks
  /// first (#1572): returns true when it asked, [go] being the tap to carry
  /// on with after Discard.
  bool _askDiscard(VoidCallback go) {
    final e = _editing;
    final targets = ref.read(simpleRunProvider).targets;
    if (!_adding || e == null || e >= targets.length) return false;
    final saved = targets[e].steps;
    final rows = ref.read(simpleRunPlanProvider);
    // Save stores the gain too: compared with what the sheet opened with.
    final gain = ref.read(simpleRunGainProvider);
    final dirty =
        gain != ref.read(simpleRunGainProvider.notifier).seeded ||
        rows.length != saved.length ||
        [
          for (var i = 0; i < rows.length; i++)
            rows[i].filterName != saved[i].filterName ||
                rows[i].exposureSeconds != saved[i].exposureSeconds ||
                rows[i].frameCount != saved[i].frameCount,
        ].any((d) => d);
    if (!dirty) return false;
    final messages = ref.read(simpleMessagesProvider.notifier);
    if (ref.read(simpleMessagesProvider).any((m) => m.id == _discardAsk)) {
      return true;
    }
    final l10n = context.l10n;
    _discardAsk = messages.show(
      SimpleMessageKind.question,
      l10n.shellDiscardEditQuestion(targets[e].name),
      // The pill fills its last action: the safe answer, not the destructive one.
      actions: [
        SimpleMessageAction(l10n.shellDiscard, go),
        SimpleMessageAction(l10n.shellKeepEditing, () {}),
      ],
    );
    return true;
  }

  void _leaveForFullUi({bool discarded = false}) {
    if (!discarded && _askDiscard(() => _leaveForFullUi(discarded: true))) {
      return;
    }
    ref.read(simpleModeProvider.notifier).set(false);
  }

  void _toggle(_Popover p, {bool discarded = false}) {
    if (!discarded && _askDiscard(() => _toggle(p, discarded: true))) return;
    setState(() => _toggleNow(p));
  }

  void _toggleNow(_Popover p) {
    // Settings covers everything; a rail menu that would cover the camera
    // window closes it.
    if (p == _Popover.menu || p.topLeft || !_sideBySide(context)) {
      _device = null;
    }
    // The sheet opened from the queue's Add target / edit outranks every
    // popover, so a rail tap closes it first (it used to stay up for good on
    // a touchscreen, user 2026-11-03); the chip, which opens that sheet, just
    // closes it.
    if (_adding) {
      _closeSheet();
      _popover = p == _Popover.settings ? _Popover.none : p;
      return;
    }
    _popover = _popover == p ? _Popover.none : p;
  }

  /// A tap on the image closes what's open, or else toggles the immersive
  /// view (only once there is a frame to look at).
  /// Whether any window is open (a device window, a rail menu, Plan's sheet
  /// or the Info tiles): a tap on the background closes them all.
  bool get _anyOpen =>
      _device != null || _popover != _Popover.none || _adding || _info;

  void _tapImage(bool hasFrame, {bool discarded = false}) {
    if (!discarded && _askDiscard(() => _tapImage(hasFrame, discarded: true))) {
      return;
    }
    setState(() {
      if (_anyOpen) {
        _device = null;
        _info = false;
        _popover = _Popover.none;
        _closeSheet();
      } else if (hasFrame) {
        _clean = !_clean;
      }
    });
  }

  @override
  Widget build(BuildContext context) {
    // The profile's site and telescope (Rig, Polar align's hemisphere, plate
    // solving): otherwise only the full UI's panels load them.
    ref.watch(planningSettingsBootstrapProvider);
    // A jump to Settings (a device dot, Open Storage) opens it over Simple
    // Mode and puts the tab back, so the next jump is a change again; any
    // other tab jump still leaves for the full UI.
    ref.listen(selectedTabIndexProvider, (previous, next) {
      if (_restoringTab) return;
      if (next == kOptionsTabIndex) {
        _restoringTab = true;
        ref
            .read(selectedTabIndexProvider.notifier)
            .select(previous ?? kLiveTabIndex);
        _restoringTab = false;
        // A device opens its own window and Storage opens Rig with its block
        // in view; the other panels get the full Settings page over Simple
        // Mode.
        final panel = ref.read(selectedSettingsPanelProvider);
        if (_deviceWindows.containsKey(panel)) {
          // The dot again closes it; another device's dot swaps it.
          setState(() {
            _device = _device == panel ? null : panel;
            // It would cover the open rail menu, or sit under Settings.
            if (_device != null &&
                (_popover == _Popover.menu ||
                    _popover.topLeft ||
                    !_sideBySide(context))) {
              _popover = _Popover.none;
            }
          });
        } else if (panel == 'session.storage') {
          setState(() {
            _device = null;
            _menuSection = 'Rig';
            _revealStorage++;
            _revealStoragePending = true;
            _popover = _Popover.menu;
          });
          WidgetsBinding.instance.addPostFrameCallback(
            (_) => _revealStoragePending = false,
          );
        } else {
          _openSettings();
        }
        return;
      }
      ref.read(simpleModeProvider.notifier).set(false);
    });
    // Fetched once per rig, so the Ara sheet has its versions on its first frame.
    ref.listen(daemonVersionsProvider, (_, _) {});
    // Preflight opens each step on its panel, and ends on the plan.
    ref.listen(simplePreflightProvider, (previous, next) {
      if (previous == null && next == null) return;
      setState(() {
        _popover = next == null ? _Popover.none : _Popover.settings;
      });
    });
    // A routine that fails says why on the left, fading like any error
    // (user, 2026-11-04: not a sticky status pill).
    ref.listen(polarAlignLiveProvider, (prev, next) {
      if (prev?.phase == PolarAlignStates.failed ||
          next.phase != PolarAlignStates.failed) {
        return;
      }
      ref
          .read(simpleMessagesProvider.notifier)
          .show(
            SimpleMessageKind.error,
            context.l10n.polarPhaseFailed,
            detail: next.errorMessage ?? next.errorReason,
            origin: SimpleStatusOrigin.polar,
          );
    });
    // Only a failure seen happening: Ara keeps its last run until it restarts,
    // so the first read after a launch or reconnect is old news (ALP-4998).
    ref.listen(autofocusLiveProvider.select((l) => (hydrated: l.hydrated, run: l.run)), (prev, live) {
      final next = live.run;
      if (prev == null || !prev.hydrated || prev.run.isFailed || !next.isFailed) {
        return;
      }
      ref
          .read(simpleMessagesProvider.notifier)
          .show(
            SimpleMessageKind.error,
            context.l10n.focusCardFailed,
            detail: MainFocusCard.failureText(context.l10n, next),
            origin: SimpleStatusOrigin.focus,
          );
    });
    final pilot = ref.watch(simplePreflightProvider);
    final shown = ref.watch(simpleShowingProvider);
    _syncGuiderPoll(
      ref.watch(
        guiderStatusProvider.select((s) => simpleGuideActive(s.value)),
      ),
    );
    // Leaving Focus takes its panel's mask question away: a pill asks.
    if (_lastShown == SimpleModeScreen.focus &&
        shown != SimpleModeScreen.focus) {
      WidgetsBinding.instance.addPostFrameCallback((_) => _askMaskOff());
    }
    _lastShown = shown;
    // Polar align shows its own camera's frames while the routine runs.
    final polarFrame =
        shown == SimpleModeScreen.polarAlign &&
        simplePolarActive(ref.watch(polarAlignLiveProvider));
    // Focus can show its run's frame, measured stars circled, when asked.
    // Manual shows the Bahtinov readout's crop, spikes and all, there.
    // Preview on the guide camera shows the frames the guider gives it.
    final focusFrame = shown == SimpleModeScreen.preview
        ? (simplePreviewGuide(ref)
              ? ref.watch(guideFocusProvider.select((l) => l.frame))
              : null)
        : shown != SimpleModeScreen.focus
        ? null
        : ref.watch(simpleFocusScopeProvider) == SimpleFocusScope.guide
        ? ref.watch(guideFocusProvider.select((l) => l.frame))
        : simpleFocusMethod(ref) == MainFocusMethod.bahtinov
        ? ref.watch(bahtinovFocusProvider.select((l) => l.frame))
        : ref.watch(simpleFocusStarsShownProvider)
        ? ref.watch(autofocusLiveProvider.select((l) => l.frame))
        : null;
    // On the guide camera only its own frames count: the main camera's last
    // one would read as the guide camera's.
    final guidePreview = simpleShowsGuidePreview(ref);
    final hasFrame =
        polarFrame ||
        focusFrame != null ||
        (!guidePreview &&
            (ref.watch(lastCapturedFrameIdProvider) != null ||
                ref.watch(liveViewControllerProvider)));
    // The first frame takes the screen back from a Sky opened before it.
    if (hasFrame && !_hadFrame) _sky = false;
    _hadFrame = hasFrame;
    final showSky = _sky || !hasFrame;
    // No frame and no sky asked for: the wallpaper (#1602).
    final wallpaper = !_sky && !hasFrame;
    final panels = !_clean || showSky;
    // Every mode reads the sequencer (the plan squares, the status pill), so
    // opening Simple Mode adopts a run in progress; only Plan acts on it.
    // Selects, not the whole run state: progress frames arrive every few
    // seconds and would rebuild the whole screen.
    final runActive =
        shown == SimpleModeScreen.plan &&
        ref.watch(
          sequenceRunStateProvider.select(
            (s) => s.value?.state?.isActive ?? false,
          ),
        );
    final run = ref.watch(simpleRunProvider);
    final planSquares =
        ref.watch(
          sequenceRunStateProvider.select((s) => simpleRunPhase(run, s.value)),
        ) !=
        SimpleRunPhase.none;
    const railTop = kSimpleInset * 2 + kSimpleBarHeight;
    final railWidth = kSimpleRailWidth * simpleRailScale(context);
    // Stop grows with the rails, so its label fits at the user's text size.
    final stopSize = kSimpleStopSize * simpleRailScale(context);
    // The bottom row (guiding, histogram) grows with the text like the
    // rails, so its two lines of labels fit at the user's size.
    final bottomRow = kSimpleBarHeight * simpleRailScale(context);
    // What sits above the bottom row drops to the inset while it is empty
    // (ALP-4935).
    final statsShown = _statsShown(ref);
    final aboveRow = statsShown ? kSimpleInset * 2 + bottomRow : kSimpleInset;
    // What reaches the right edge stays clear of Stop, which sits over
    // everything (iPad, 2026-11-04: Settings ran under it).
    final aboveStop = math.max(aboveRow, kSimpleInset * 2 + stopSize);
    final width = MediaQuery.sizeOf(context).width;
    // Preflight's step strip, under the status strip; its offer sits at the
    // bottom (user, 2026-11-03).
    final simplePreflightRow = pilot != null;
    // Messages share the top row with the status pill, which keeps at most
    // half of it; they start under the Preflight strip when it shows. On a
    // window too narrow to share (a phone) they take the whole row, under a
    // room kept for the status pill, so neither moves when the other comes.
    final sharedWidth =
        width -
        kSimpleInset * 3 -
        math.min(kSimpleStatusMaxWidth, (width - kSimpleInset * 3) / 2);
    final stacked = sharedWidth < 200;
    final messagesWidth = math.min(
      kSimpleMessagesMaxWidth,
      stacked ? width - kSimpleInset * 2 : sharedWidth,
    );
    final statusRow = stacked
        ? math.max(kSimpleStatusHeight, _statusStackHeight) + AraSpace.s8
        : 0.0;
    final messagesTop = simplePreflightRow
        ? railTop +
              math.max(statusRow, _simplePreflightRowHeight + AraSpace.s12)
        : railTop + statusRow;
    _measure();
    final height =
        MediaQuery.sizeOf(context).height -
        MediaQuery.paddingOf(context).vertical;
    final leftRailTop = _leftRailHeight == 0
        ? height
        : railTop + (height - railTop * 2 - _leftRailHeight) / 2;
    final rightRailTop =
        railTop +
        (height - railTop - kSimpleInset * 2 - stopSize - _rightRailHeight) / 2;
    final messagesRoom = panels
        ? leftRailTop - messagesTop - kSimpleInset
        : double.infinity;
    // Pills too tall for the room above the left rail move beside it, when
    // that leaves them a readable width; otherwise they paint over it. On a
    // phone they would run under the right rail beside the left one, so they
    // paint over the left rail and stop short of the right one.
    final railShift = railWidth + kSimpleInset;
    final pillsBesideRail =
        !stacked &&
        _messagesHeight > messagesRoom &&
        messagesWidth - railShift >= 240;
    final pillsOverRail =
        panels &&
        _messagesHeight > messagesRoom &&
        !pillsBesideRail &&
        _device == null &&
        _popover == _Popover.none &&
        !_adding;
    // The rail's popovers (a mode's panel, the modes, the queue) take the room
    // from under the status pill down to the screen's inset, over the bottom
    // row while open (they sit left of the rail, clear of Stop), above the
    // plan squares. Plan's sheet and Preflight's step panels start at its
    // top, 8 pt under the status pill; the rest sit level with the rail and
    // move up only when they don't fit (user, 2026-11-04).
    // Pills grow with large text; none showing still keeps a pill's room.
    final popoverTop =
        railTop +
        math.max(kSimpleStatusHeight, _statusHeight) +
        AraSpace.s8;
    final preflightPanel = pilot != null && _popover == _Popover.settings;
    // Plan's sheet and Preflight's step panels: centred, content-high, in the
    // space between the status pill and the bottom row's top (the screen's
    // inset without it), at least 8 pt from each (user, 2026-11-04).
    final betweenBottom =
        (statsShown ? kSimpleInset + bottomRow : kSimpleInset) + AraSpace.s8;
    final popoverBottom = planSquares
        ? aboveStop +
              kSimplePlanSquareHeight * simpleRailScale(context) +
              kSimpleInset
        : kSimpleInset;
    final queuePanel = SimpleRunPanel(
      onAdd: () => _openSheet(),
      onEdit: (i) => _openSheet(edit: i),
    );
    final runSheet =
        shown == SimpleModeScreen.plan &&
        ((_popover == _Popover.settings && !runActive) || _adding);
    final sheet = SimpleRunSheet(
      editing: _editing,
      onShowSky: () => setState(() => _sky = true),
      onPickInSky: () => setState(() {
        _sky = true;
        _popover = _Popover.none;
        _closeSheet();
      }),
      onCreated: () => setState(() {
        _popover = _Popover.none;
        _closeSheet();
      }),
    );
    final sel = ref.watch(planetariumSelectionProvider);
    final motion = reduceMotionOf(context)
        ? Duration.zero
        : const Duration(milliseconds: 220);
    // Both slots stay filled, and with one type, or the pills moving between
    // them would shift their siblings and rebuild them from scratch.
    final pillsLeft = kSimpleInset + (pillsBesideRail ? railShift : 0);
    final pillsTop = panels ? messagesTop : kSimpleInset + statusRow;
    // At the pills' own place: the slot they move into animates from here.
    final emptyPillSlot = AnimatedPositioned(
      duration: Duration.zero,
      left: pillsLeft,
      top: pillsTop,
      child: const SizedBox.shrink(),
    );
    final messagePills = AnimatedPositioned(
      left: pillsLeft,
      top: pillsTop,
      width:
          messagesWidth - (pillsBesideRail || pillsOverRail ? railShift : 0),
      duration: motion,
      curve: Curves.easeOut,
      child: NotificationListener<SizeChangedLayoutNotification>(
        onNotification: (_) {
          _measure();
          return true;
        },
        // Large text can outgrow the screen: above Stop the pills scroll.
        child: ConstrainedBox(
          constraints: BoxConstraints(
            maxHeight: math.max(
              kSimpleMessageHeight,
              height - pillsTop - aboveStop,
            ),
          ),
          child: SingleChildScrollView(
            child: SizeChangedLayoutNotifier(
              child: SimpleMessagePills(
                key: _messagesKey,
                onOpen: _openFromPill,
                maxHeight: messagesRoom,
              ),
            ),
          ),
        ),
      ),
    );
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
                  const SingleActivator(LogicalKeyboardKey.comma, meta: true):
                      _openMenu,
                  const SingleActivator(
                    LogicalKeyboardKey.comma,
                    control: true,
                  ): _openMenu,
                  const SingleActivator(
                    LogicalKeyboardKey.keyK,
                    meta: true,
                  ): () =>
                      _toggle(_Popover.search),
                  const SingleActivator(
                    LogicalKeyboardKey.keyK,
                    control: true,
                  ): () =>
                      _toggle(_Popover.search),
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
                    // One shared blur for every frosted panel over the image
                    // (iPad 6, 2026-11-05: one filter each cost ~30 ms a frame
                    // against 4 ms with none; see simpleFrost).
                    child: BackdropGroup(
                    child: Stack(
                      children: [
                        Positioned.fill(
                          // Screen readers get the immersive toggle by name.
                          child: Semantics(
                            button: !showSky,
                            label: showSky
                                ? null
                                : _clean
                                ? context.l10n.shellShowControls
                                : context.l10n.shellHideControls,
                            child: GestureDetector(
                              key: const Key('simple-frame'),
                              behavior: HitTestBehavior.opaque,
                              onTap: () => _tapImage(!showSky),
                              child: !showSky
                                  ? (focusFrame != null
                                        ? Image.memory(
                                            focusFrame,
                                            key: const Key(
                                              'simple-focus-frame',
                                            ),
                                            fit: BoxFit.contain,
                                            gaplessPlayback: true,
                                          )
                                        : polarFrame
                                        ? const SimplePolarFrame()
                                        : const FrameViewer())
                                  : wallpaper
                                  ? const SimpleWallpaper(
                                      key: Key('simple-wallpaper'),
                                    )
                                  // The sky, asked for.
                                  : KeyedSubtree(
                                      key: const Key('simple-planetarium'),
                                      child: widget.planetarium,
                                    ),
                            ),
                          ),
                        ),
                        // The planetarium takes taps itself, so while a
                        // window is open a layer over the background closes
                        // them all instead. It paints (1/255, unseen): on
                        // macOS a click reaches the native sky view unless
                        // Flutter has drawn over that spot (iOS asks Flutter
                        // first either way).
                        if (_anyOpen)
                          Positioned.fill(
                            child: GestureDetector(
                              key: const Key('simple-background-close'),
                              behavior: HitTestBehavior.opaque,
                              onTap: () => _tapImage(!showSky),
                              child: const ColoredBox(color: Color(0x01000000)),
                            ),
                          ),
                        if (!hasFrame)
                          Positioned.fill(
                            child: IgnorePointer(
                              child: Center(
                                child: Text(
                                  !guidePreview
                                      ? context.l10n.shellTapShutter
                                      : ref.watch(
                                          guideFocusProvider.select(
                                            (g) => g.status.active,
                                          ),
                                        )
                                      ? context.l10n.guideFocusStarting
                                      : context.l10n.previewGuideEmpty,
                                  style: TextStyle(
                                    fontSize: 20,
                                    letterSpacing: 2,
                                    // Dim but solid: 8:1 on a dark sky.
                                    color: AraColors.textSecondary,
                                  ),
                                ),
                              ),
                            ),
                          ),
                        // The message pills, top-left under the strip (under the
                        // Preflight strip when it shows), down to the left rail;
                        // they stay in the immersive view.
                        pillsOverRail ? emptyPillSlot : messagePills,
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
                            child: NotificationListener<SizeChangedLayoutNotification>(
                              onNotification: (_) {
                                _measure();
                                return true;
                              },
                              child: SizeChangedLayoutNotifier(
                                child: SimpleStatusPill(
                                  key: _statusKey,
                                  onFirstHeight: _setStatusHeight,
                                  onOpen: _openFromPill,
                                ),
                              ),
                            ),
                          ),
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
                                child: _StatusStrip(
                                  onAra: () => _toggle(_Popover.ara),
                                  onSearch: () => _toggle(_Popover.search),
                                  onSettings: () => _openMenu(),
                                  onDevices: () =>
                                      _openMenu(section: 'Devices'),
                                  onFullUi: _leaveForFullUi,
                                ),
                              ),
                              // Info: fact tiles hugging the bottom-left corner,
                              // above the bottom row.
                              if (_info)
                                AnimatedPositioned(
                                  // Keyed: each slides only from its own place.
                                  key: const ValueKey('info'),
                                  left: kSimpleInset,
                                  bottom: aboveRow,
                                  duration: motion,
                                  curve: Curves.easeOut,
                                  child: const SimpleInfoPanel(),
                                ),
                              // Guiding and the histogram along the bottom, each
                              // once it has data: 16 px from the edge and from Stop.
                              Positioned(
                                left: kSimpleInset,
                                right:
                                    kSimpleInset * 2 +
                                    (railWidth + stopSize) / 2,
                                bottom: kSimpleInset,
                                height: bottomRow,
                                child: const _StatsRow(),
                              ),
                              Positioned(
                                left: kSimpleInset,
                                top: railTop,
                                bottom: railTop,
                                width: railWidth,
                                child: Align(
                                  alignment: Alignment.centerLeft,
                                  child: KeyedSubtree(
                                    key: _leftRailKey,
                                    child: _LeftRail(
                                      info: _info,
                                      sky: _sky,
                                      onInfo: () =>
                                          setState(() => _info = !_info),
                                      onSky: () => setState(() => _sky = !_sky),
                                      onAlbum: _openAlbum,
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
                                bottom: kSimpleInset * 2 + stopSize,
                                width: railWidth,
                                child: Center(
                                  child: CompositedTransformTarget(
                                    link: _railLink,
                                    child: _RightRail(
                                      key: _rightRailKey,
                                      mode: shown,
                                      named: pilot != null
                                          ? SimpleModeScreen.preflight
                                          : null,
                                      popover: _popover,
                                      onModes: () => _toggle(_Popover.modes),
                                      onSettings: () =>
                                          _toggle(_Popover.settings),
                                    ),
                                  ),
                                ),
                              ),
                              // Plan's sheet and Night done: beside the rail;
                              // while imaging, its panel sits there like any
                              // mode's.
                              // Between the status pill and the bottom row (the
                              // plan squares step aside while it shows); a tall
                              // sheet scrolls inside.
                              if (runSheet)
                                Positioned(
                                  // Beside the rail like every mode's panel,
                                  // with the sheet's height (user, 2026-11-04;
                                  // it was centred over the image).
                                  left: kSimpleInset,
                                  right: kSimpleInset * 2 + railWidth,
                                  // Centred between the status pill and the
                                  // bottom row (user, 2026-11-04).
                                  top: popoverTop,
                                  bottom: betweenBottom,
                                  child: Align(
                                    alignment: Alignment.centerRight,
                                    child: sheet,
                                  ),
                                )
                              // The rail's own popovers; Settings and Search
                              // have their places of their own.
                              else if (const {
                                _Popover.modes,
                                _Popover.settings,
                                _Popover.queue,
                              }.contains(_popover))
                                Positioned(
                                  left: 0,
                                  right: kSimpleInset * 2 + railWidth,
                                  top: popoverTop,
                                  // Plan's bottom limit; the plan squares,
                                  // which its sheet moves, stay clear.
                                  bottom: planSquares && !preflightPanel
                                      ? popoverBottom
                                      : betweenBottom,
                                  child: CustomSingleChildLayout(
                                    delegate: _BesideRail(
                                      centre:
                                          preflightPanel ||
                                          (_popover == _Popover.settings &&
                                              shown == SimpleModeScreen.guide),
                                      preferredTop: math.max(
                                        0,
                                        rightRailTop - popoverTop,
                                      ),
                                    ),
                                    child: SingleChildScrollView(
                                        child: _popover == _Popover.queue
                                            ? queuePanel
                                            : _popover == _Popover.modes
                                            ? _ModeGrid(
                                                current: pilot != null
                                                    ? SimpleModeScreen.preflight
                                                    : shown,
                                                // Picking a mode only switches it; the
                                                // chip opens its panel (user, 2026-11-02).
                                                onPicked: _pickMode,
                                              )
                                            : switch (shown) {
                                                SimpleModeScreen.focus =>
                                                  const SimpleFocusPanel(),
                                                SimpleModeScreen.polarAlign =>
                                                  const SimplePolarPanel(),
                                                SimpleModeScreen.guide =>
                                                  const SimpleGuidePanel(),
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
                              // Preflight's steps, or the offer to start it, centred
                              // under the strip.
                              if (simplePreflightRow)
                                Positioned(
                                  left: 0,
                                  right: 0,
                                  top: railTop,
                                  child: Center(
                                    child: KeyedSubtree(
                                      key: _simplePreflightRowKey,
                                      child: const SimplePreflightStrip(),
                                    ),
                                  ),
                                ),
                              // The plan squares hug the bottom-right corner, just
                              // above the bottom row, growing left over the right
                              // half (the Info tiles have the left).
                              if (shown == SimpleModeScreen.plan && !runSheet)
                                AnimatedPositioned(
                                  key: const ValueKey('plan-squares'),
                                  left: width / 2,
                                  right: kSimpleInset,
                                  bottom: aboveStop,
                                  duration: motion,
                                  curve: Curves.easeOut,
                                  child: Align(
                                    alignment: Alignment.bottomRight,
                                    child: SimpleRunTracker(
                                      onTap: () => _toggle(_Popover.queue),
                                    ),
                                  ),
                                ),
                              // The sky's pick, offered to Plan.
                              if (_sky && sel != null && !runSheet)
                                Positioned(
                                  left: 0,
                                  right: 0,
                                  bottom: kSimpleInset * 2 + 40,
                                  child: Center(
                                    child: _SkyPick(
                                      selection: sel,
                                      onImage: _imagePick,
                                    ),
                                  ),
                                ),
                              // Settings: a sheet over the dimmed image, from the
                              // strip down to the bottom row; Stop stays above
                              // the veil, within reach.
                              // A device's window, under the strip's dots.
                              if (_device != null)
                                Positioned(
                                  left: _topLeftOverlayLeft(context),
                                  top: railTop,
                                  // Never past the bottom row: a tall window
                                  // (a many-slot filter wheel) scrolls.
                                  bottom: aboveRow,
                                  width:
                                      kSimpleDeviceWindowWidth *
                                      simpleRailScale(context),
                                  child: Align(
                                    alignment: Alignment.topLeft,
                                    child: SimplePanel(
                                      child: SingleChildScrollView(
                                        padding: const EdgeInsets.all(
                                          AraSpace.s16,
                                        ),
                                        child: _deviceWindows[_device]!,
                                      ),
                                    ),
                                  ),
                                ),
                              // Search drops from the strip, centred.
                              if (_popover == _Popover.search)
                                Positioned(
                                  top: railTop,
                                  left: 0,
                                  right: 0,
                                  bottom: aboveRow,
                                  child: Align(
                                    alignment: Alignment.topCenter,
                                    child: SizedBox(
                                      width: math.min(
                                        480.0,
                                        width - kSimpleInset * 2,
                                      ),
                                      child: SimpleSearchPanel(onHit: _openHit),
                                    ),
                                  ),
                                ),
                              if (_popover == _Popover.ara)
                                Positioned(
                                  top: kSimpleInset * 2 + kSimpleBarHeight,
                                  left: _topLeftOverlayLeft(context),
                                  child: _AraSheet(
                                    onClose: () => setState(
                                      () => _popover = _Popover.none,
                                    ),
                                  ),
                                ),
                              if (_popover == _Popover.menu) ...[
                                // Under the strip, which stays live (its
                                // gear closes Settings, the rest still work).
                                Positioned(
                                  left: 0,
                                  right: 0,
                                  bottom: 0,
                                  top: kSimpleInset + kSimpleBarHeight,
                                  child: GestureDetector(
                                    key: const Key('simple-menu-veil'),
                                    behavior: HitTestBehavior.opaque,
                                    onTap: () => setState(
                                      () => _popover = _Popover.none,
                                    ),
                                    child: const ColoredBox(
                                      color: Color(0x59000000), // 35 %
                                    ),
                                  ),
                                ),
                                Positioned(
                                  left: kSimpleInset,
                                  right: kSimpleInset,
                                  top: railTop,
                                  bottom: aboveStop,
                                  // Under the gear, as wide as its content
                                  // needs (user, 2026-11-04: window-wide was
                                  // mostly empty space).
                                  child: Align(
                                    alignment: Alignment.topRight,
                                    child: ConstrainedBox(
                                      constraints: BoxConstraints(
                                        // Grows with the text, like the rails.
                                        maxWidth:
                                            kSimpleSettingsMaxWidth *
                                            simpleRailScale(context),
                                      ),
                                      child: _SettingsSheet(
                                        key: ValueKey((
                                          _menuSection,
                                          _revealStorage,
                                        )),
                                        section: _menuSection,
                                        revealStorage: _revealStoragePending,
                                        onClose: () => setState(
                                          () => _popover = _Popover.none,
                                        ),
                                      ),
                                    ),
                                  ),
                                ),
                              ],
                              // Stop on its own at the bottom, under the rail.
                              Positioned(
                                right:
                                    kSimpleInset + (railWidth - stopSize) / 2,
                                bottom: kSimpleInset,
                                width: stopSize,
                                height: stopSize,
                                child: const EmergencyStopButton(compact: true),
                              ),
                            ],
                          ),
                        ),
                        // Above the rail only when it would cover them, and
                        // never above an open window or popover.
                        pillsOverRail ? messagePills : emptyPillSlot,
                      ],
                    ),
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
        // Wraps in a narrow window (or a longer language) so Image this
        // keeps its place.
        Flexible(
          child: Column(
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
                  simpleTargetStatus(selection, context.l10n),
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
        ),
        FilledButton(
          key: const Key('simple-sky-image-this'),
          onPressed: onImage,
          child: Text(context.l10n.shellImageThis),
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
    final reduce = reduceMotionOf(context);
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

/// One floating panel: overlay scrim, 10 px corners, a Hairline contour.
/// Every Simple Mode panel's look: the 90 % scrim over a blur of the image
/// behind ([AraBlur.panel]), first the Settings sheet's and the device
/// windows', now every panel's, for legibility (user, 2026-11-04).
class SimplePanel extends StatelessWidget {
  const SimplePanel({super.key, required this.child, this.padding});

  final Widget child;
  final EdgeInsetsGeometry? padding;

  @override
  Widget build(BuildContext context) => simpleFrost(
    Container(
      key: const Key('simple-frosted-surface'),
      padding: padding,
      decoration: const BoxDecoration(color: AraColors.overlayScrim),
      // On top, not a border, so the contour takes no room from the content.
      foregroundDecoration: BoxDecoration(
        border: Border.all(color: AraColors.border),
        borderRadius: BorderRadius.circular(10),
      ),
      child: child,
    ),
  );
}

/// The panel blur under [child], clipped to the panels' 10 px corners: for
/// the surfaces that draw their own scrim (pills, tiles, plan squares).
/// Grouped: the shell's [BackdropGroup] blurs the image once for all of them.
Widget simpleFrost(Widget child) => ClipRRect(
  borderRadius: BorderRadius.circular(10),
  child: AraBlur.panel == 0
      ? child
      : BackdropFilter.grouped(
          filter: ImageFilter.blur(
            sigmaX: AraBlur.panel,
            sigmaY: AraBlur.panel,
          ),
          child: child,
        ),
);

/// Device dots (connected gear only), then Night mode and the Settings menu.
/// A rail popover in its room: right-aligned, at [preferredTop] (level with
/// the rail) when it fits below, moved up as much as it needs when it doesn't;
/// or, with [centre], centred in the room (Preflight's step panels).
class _BesideRail extends SingleChildLayoutDelegate {
  const _BesideRail({required this.preferredTop, this.centre = false});

  final double preferredTop;
  final bool centre;

  // Height only: a panel wider than a phone-width window runs off its left
  // edge, as before (phone layout: ALP-4865).
  @override
  BoxConstraints getConstraintsForChild(BoxConstraints constraints) =>
      BoxConstraints(maxHeight: constraints.maxHeight);

  @override
  Offset getPositionForChild(Size size, Size childSize) {
    final room = math.max(0.0, size.height - childSize.height);
    return Offset(
      size.width - childSize.width,
      centre ? room / 2 : preferredTop.clamp(0, room),
    );
  }

  @override
  bool shouldRelayout(_BesideRail old) =>
      old.preferredTop != preferredTop || old.centre != centre;
}

/// Whether any top-bar chip shows: a device not disconnected, or a standing
/// fault (the chips' own rule).
bool _anyDeviceShown(WidgetRef ref) {
  if (!ref.watch(serverLinkUpProvider)) return false;
  if (ref.watch(activeFaultsProvider).byDeviceType.isNotEmpty) return true;
  return [
    equipmentChipLevel(ref.watch(cameraStatusProvider)),
    equipmentChipLevel(ref.watch(mountProvider)),
    equipmentChipLevel(ref.watch(focuserProvider)),
    equipmentChipLevel(ref.watch(filterWheelProvider)),
    equipmentChipLevel(ref.watch(rotatorProvider)),
    equipmentChipLevel(ref.watch(flatPanelProvider)),
    equipmentChipLevel(ref.watch(weatherProvider)),
    equipmentChipLevel(ref.watch(safetyMonitorProvider)),
    equipmentChipLevel(ref.watch(domeProvider)),
    guiderChipLevel(ref.watch(guiderStatusProvider)),
    switchChipLevel(
      ref.watch(switchListProvider),
      acting: ref.watch(switchActingProvider),
    ),
  ].any((l) => l != StatusLevel.disconnected);
}

class _StatusStrip extends ConsumerWidget {
  const _StatusStrip({
    required this.onAra,
    required this.onSearch,
    required this.onSettings,
    required this.onDevices,
    required this.onFullUi,
  });

  /// Opens Settings › Devices (the no-devices note).
  final VoidCallback onDevices;

  /// Opens the OpenAstro Ara sheet (the mark).
  final VoidCallback onAra;

  final VoidCallback onSearch;
  final VoidCallback onSettings;

  /// Leaves Simple Mode, the way the Full UI's button enters it.
  final VoidCallback onFullUi;

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
          // The brand is a control: it opens the OpenAstro Ara sheet (#1602).
          IconButton(
            key: const Key('simple-ara-mark'),
            icon: const AraMark(size: 30),
            tooltip: kAppName,
            onPressed: onAra,
          ),
          // A hairline sets the brand apart from the device dots, which
          // otherwise read as one more device (user, 2026-11-06).
          Container(
            key: const Key('simple-ara-divider'),
            width: 1,
            height: 28,
            margin: const EdgeInsets.only(left: AraSpace.s4, right: AraSpace.s12),
            color: AraColors.border,
          ),
          // Only gear that's connected; a fault still shows. With none of it
          // showing, a note says so and opens Devices.
          Expanded(
            child: Align(
              alignment: Alignment.centerLeft,
              child: _anyDeviceShown(ref)
                  ? const EquipmentChipCompact(
                      compact: true,
                      child: SingleChildScrollView(
                        scrollDirection: Axis.horizontal,
                        child: EquipmentChipHideDisconnected(
                          child: TopEquipmentChips(),
                        ),
                      ),
                    )
                  : TextButton(
                      key: const Key('simple-no-devices'),
                      onPressed: onDevices,
                      // No rig is amber (needs a look), not the link blue,
                      // which reads as connecting.
                      style: ref.watch(serverLinkUpProvider)
                          ? null
                          : TextButton.styleFrom(
                              foregroundColor: AraColors.accentWarning,
                            ),
                      child: Text(
                        ref.watch(serverLinkUpProvider)
                            ? context.l10n.shellNoDevices
                            : context.l10n.shellRigNotConnected,
                      ),
                    ),
            ),
          ),
          // Simple Mode's own search: the sky, its settings, its modes and
          // the Image Library; nothing it finds leads out.
          IconButton(
            key: const Key('simple-search-button'),
            icon: const Icon(Icons.search, size: 22),
            tooltip: context.l10n.shellSearch,
            onPressed: onSearch,
          ),
          IconButton(
            icon: Icon(
              night ? Icons.nights_stay : Icons.nights_stay_outlined,
              size: 22,
              color: night ? AraColors.accentInfo : null,
            ),
            tooltip: night
                ? context.l10n.shellNightModeOn
                : context.l10n.shellNightMode,
            onPressed: () => ref.read(nightModeProvider.notifier).set(!night),
          ),
          IconButton(
            key: const Key('simple-full-ui-button'),
            icon: const Icon(
              Icons.fit_screen,
              size: 22,
              color: AraColors.accentInfo,
            ),
            tooltip: context.l10n.shellFullUi,
            onPressed: onFullUi,
          ),
          IconButton(
            icon: const Icon(Icons.settings_outlined, size: 22),
            tooltip: context.l10n.shellSettings,
            onPressed: onSettings,
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

  /// Null: nothing to do, announced so; dim unless [selected] (Sky with no
  /// frame yet: the planetarium it opens is already open, so it shows lit).
  final VoidCallback? onTap;
  final bool selected;

  @override
  Widget build(BuildContext context) => Semantics(
    button: true,
    enabled: onTap != null,
    selected: selected,
    label: label,
    child: Material(
      color: selected ? AraColors.selectionTint : Colors.transparent,
      borderRadius: BorderRadius.circular(8),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(8),
        child: SizedBox(
          width: 60 * simpleRailScale(context),
          height: (tall ? 72 : 56) * simpleRailScale(context),
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              if (icon != null) ...[
                Icon(
                  icon,
                  size: 24,
                  color: onTap == null && !selected
                      ? AraColors.textDisabled
                      : selected
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
                  color: onTap == null && !selected
                      ? AraColors.textDisabled
                      : selected
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

/// How much the rails grow: none up to 1.1x text, then with the text, so
/// their labels follow the user's size all the way (WCAG 1.4.4). The 60 px
/// mode label ("Preview", 14 px semibold) is full from 1.1x.
/// Every Settings › Rig value (a wheel's, the elevation stepper's, the read-only
/// pixel size) is this wide, so they line up.
const double kSimpleRigControlWidth = 160;

/// Simple Mode's Settings sheet at its widest: the section list and a section
/// whose rows are a label and a [kSimpleRigControlWidth] value.
const double kSimpleSettingsMaxWidth = 640;

double simpleRailScale(BuildContext context) =>
    math.max(1, MediaQuery.textScalerOf(context).scale(1) / 1.1);

/// Names in reading order: an accent doesn't move a letter.
/// shortcut: folds en and pt-BR letters only, add a collator with a third locale.
int simpleCompareNames(String a, String b) {
  const accented = 'áàâãéêíóôõúüç';
  const plain = 'aaaaeeiooouuc';
  String key(String s) => s.toLowerCase().replaceAllMapped(
    RegExp('[$accented]'),
    (m) => plain[accented.indexOf(m[0]!)],
  );
  return key(a).compareTo(key(b));
}

/// Past 1.2x text a mode panel's side-by-side groups stack, so their labels
/// keep their full words (WCAG 1.4.4): pt-BR's "Principal" and "Contínuo"
/// were cut from 1.3x (impeccable audit, 2026-11-04).
bool simpleStackText(BuildContext context) =>
    MediaQuery.textScalerOf(context).scale(1) > 1.2;

class _LeftRail extends StatelessWidget {
  const _LeftRail({
    required this.info,
    required this.sky,
    required this.onInfo,
    required this.onSky,
    required this.onAlbum,
  });

  final bool info;
  final bool sky;
  final VoidCallback onInfo;
  final VoidCallback? onSky;
  final VoidCallback onAlbum;

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
          label: context.l10n.shellSky,
          selected: sky,
          onTap: onSky,
        ),
        _RailButton(
          key: const Key('simple-info'),
          icon: Icons.info_outline,
          label: context.l10n.shellInfo,
          selected: info,
          onTap: onInfo,
        ),
        _RailButton(
          key: const Key('simple-library'),
          icon: Icons.photo_library_outlined,
          label: context.l10n.shellLibrary,
          onTap: () => Navigator.of(context).push(
            MaterialPageRoute<void>(builder: (_) => const ImageLibraryScreen()),
          ),
        ),
        _RailButton(
          key: const Key('simple-album'),
          icon: albumIcon,
          label: context.l10n.shellAlbum,
          onTap: onAlbum,
        ),
      ],
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

  /// Overrides the mode named on the rail (Preflight shows its own).
  final SimpleModeScreen? named;
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
            label: context.l10n.shellModeSemantics(
              (named ?? mode).labelOf(context.l10n),
            ),
            child: InkWell(
              key: const Key('simple-mode'),
              onTap: onModes,
              borderRadius: BorderRadius.circular(8),
              child: SizedBox(
                width: 60 * simpleRailScale(context),
                height: 48 * simpleRailScale(context),
                child: Column(
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    // A name wider than the rail shrinks to fit, never cut.
                    FittedBox(
                      fit: BoxFit.scaleDown,
                      child: Text(
                        (named ?? mode).railLabelOf(context.l10n),
                        maxLines: 1,
                        style: kSimpleText.copyWith(
                          fontWeight: FontWeight.w600,
                          color: modesOpen ? AraColors.accentInfo : null,
                        ),
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
            SimpleModeScreen.guide => const SimpleGuideShutter(),
            SimpleModeScreen.plan => const SimpleRunShutter(),
            _ => const SimpleShutter(),
          },
          _RailButton(
            key: const Key('simple-settings'),
            icon: Icons.tune,
            tall: true,
            label: _chipLabel(context, ref),
            selected: popover == _Popover.settings,
            onTap: onSettings,
          ),
        ],
      ),
    );
  }

  // In Preflight the chip names the step whose settings it shows ("Focus"
  // over "Auto"), so they don't read as random.
  String _chipLabel(BuildContext context, WidgetRef ref) {
    final label = switch (mode) {
      SimpleModeScreen.focus => simpleFocusSummary(ref),
      SimpleModeScreen.guide => simpleGuideSummary(ref),
      SimpleModeScreen.plan => simpleRunChip(
        ref.watch(simpleRunProvider),
        ref.watch(simpleRunPlanProvider),
      ),
      SimpleModeScreen.polarAlign => simplePolarSummary(
        ref.watch(simplePolarSettingsProvider).value ??
            const PolarAlignSettings(),
        ref.context.l10n,
      ),
      _ => simpleSettingsSummary(context, ref),
    };
    return named == null
        ? label
        : '${mode.railLabelOf(context.l10n)}\n${label.split('\n').first}';
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
      width: 300 * simpleRailScale(context),
      child: Wrap(
        spacing: AraSpace.s8,
        runSpacing: AraSpace.s8,
        children: [
          for (final m in SimpleModeScreen.values)
            Semantics(
              selected: m == current,
              // 138 x 52 at 1x; wider with the text, and two lines for a
              // name that still needs them.
              child: ConstrainedBox(
                constraints: BoxConstraints.tightFor(
                  width: 138 * simpleRailScale(context),
                ).copyWith(minHeight: 52),
                child: Material(
                  color: m == current
                      ? AraColors.selectionBg
                      : AraColors.tileFill,
                  borderRadius: BorderRadius.circular(10),
                  child: InkWell(
                    key: Key('simple-mode-${m.name}'),
                    borderRadius: BorderRadius.circular(10),
                    onTap: () => onPicked(m),
                    child: Padding(
                      padding: const EdgeInsets.symmetric(horizontal: 12),
                      child: Row(
                        spacing: AraSpace.s8,
                        children: [
                          Icon(
                            m.icon,
                            size: 20,
                            color: m == current
                                ? AraColors.selectionFg
                                : AraColors.textPrimary,
                          ),
                          Flexible(
                            child: Text(
                              m.labelOf(context.l10n),
                              // White on the blue reads at 4.6:1.
                              style: kSimpleText.copyWith(
                                color: m == current
                                    ? AraColors.selectionFg
                                    : null,
                              ),
                              maxLines: 2,
                              overflow: TextOverflow.ellipsis,
                            ),
                          ),
                        ],
                      ),
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
      padding: const EdgeInsets.symmetric(
        horizontal: AraSpace.s16,
        vertical: AraSpace.s4,
      ),
      child: Row(
        children: [
          // The labels take what they need (a third at most); the graph fills
          // the rest of the panel, its whole height (iPad, 2026-11-04: it had
          // half the width and 32 pt).
          ConstrainedBox(
            constraints: BoxConstraints(
              maxWidth: MediaQuery.sizeOf(context).width / 3,
            ),
            child: Column(
              mainAxisAlignment: MainAxisAlignment.center,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                // "Guider", not "Guiding": it shows while the guider
                // loops or calibrates too (user, 2026-11-04).
                Text(context.l10n.deviceGuider, style: kSimpleText),
                Text(
                  guiding
                      ? context.l10n.shellGuidingRms(
                          GuidingStrip.fmt(status?.rmsTotalArcsec, '″'),
                        )
                      : GuidingStrip.stateLabel(context.l10n, status),
                  style: AraText.caption,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                ),
              ],
            ),
          ),
          const SizedBox(width: 14),
          Expanded(
            child: CustomPaint(
              key: const Key('simple-guide-trace'),
              painter: _TracePainter(steps),
              size: Size.infinite,
            ),
          ),
          const SizedBox(width: 10),
          // Named, and Dec dashed: the two lines aren't told apart by colour
          // alone (WCAG 1.4.1, the Brightness-First Rule).
          Column(
            mainAxisAlignment: MainAxisAlignment.center,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                context.l10n.shellRa,
                style: AraText.caption.copyWith(color: AraColors.accentInfo),
              ),
              Text(
                context.l10n.shellDec,
                style: AraText.caption.copyWith(color: AraColors.errorText),
              ),
            ],
          ),
        ],
      ),
    );
  }
}

/// The last minute or so of RA (blue, solid) and Dec (light red, dashed)
/// guide error, centred on zero and scaled to the largest excursion shown.
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
    void line(
      double? Function(GuideStep) value,
      Color color, {
      bool dashed = false,
    }) {
      final path = Path();
      var started = false;
      for (var i = 0; i < _steps.length; i++) {
        final v = value(_steps[i]);
        if (v == null) continue;
        final p = Offset(size.width * i / (_kShown - 1), mid - v / peak * mid);
        started ? path.lineTo(p.dx, p.dy) : path.moveTo(p.dx, p.dy);
        started = true;
      }
      final paint = Paint()
        ..color = color
        ..style = PaintingStyle.stroke
        ..strokeWidth = 1.5;
      if (!dashed) return canvas.drawPath(path, paint);
      for (final m in path.computeMetrics()) {
        for (var d = 0.0; d < m.length; d += 7) {
          canvas.drawPath(m.extractPath(d, math.min(d + 4, m.length)), paint);
        }
      }
    }

    line((s) => s.raPx, AraColors.accentInfo);
    line((s) => s.decPx, AraColors.errorText, dashed: true);
  }

  @override
  bool shouldRepaint(_TracePainter old) => old._steps != _steps;
}

// The guiding graph shows while the guider runs, never once it is stopped,
// whatever steps are left from before (user, 2026-11-04).
bool _guidingShown(WidgetRef ref) => ref.watch(
  guiderStatusProvider.select((s) => simpleGuideActive(s.asData?.value)),
);

// The histogram is for saved frames (the daemon analyses those).
String? _histogramFrame(WidgetRef ref) =>
    ref.watch(liveViewControllerProvider) || simpleShowsGuidePreview(ref)
    ? null
    : ref.watch(lastCapturedFrameIdProvider);

/// Whether the bottom row has anything to show.
bool _statsShown(WidgetRef ref) {
  if (_guidingShown(ref)) return true;
  final id = _histogramFrame(ref);
  return id != null &&
      ref.watch(
        frameHistogramProvider(id).select((h) => h.asData?.value != null),
      );
}

/// Guiding and the histogram along the bottom. Each panel waits for data, and
/// whichever shows takes the row (both: guiding gets the larger share).
class _StatsRow extends ConsumerWidget {
  const _StatsRow();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final guiding = _guidingShown(ref);
    final id = _histogramFrame(ref);
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
              Text(context.l10n.shellHistogram, style: kSimpleText),
              Text(
                context.l10n.shellHistogramMedian(h.median.round()),
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
    this.group,
    required this.selected,
    required this.radius,
    this.onTap,
    this.alignment = Alignment.center,
    this.padding = const EdgeInsets.symmetric(horizontal: 14),
  });

  final String label;

  /// What the row of segments chooses, for screen readers.
  final String? group;
  final Alignment alignment;

  /// Around the label; a list sets it to line its labels up.
  final EdgeInsetsGeometry padding;
  final bool selected;
  final double radius;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) => Semantics(
    button: true,
    label: group,
    // A segment that takes no tap is read as disabled, not as a button; the
    // selected one stays enabled, read as selected (a native segmented control).
    enabled: onTap != null || selected,
    selected: selected,
    child: Material(
      color: selected ? AraColors.selectionBg : Colors.transparent,
      borderRadius: BorderRadius.circular(radius),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(radius),
        child: Container(
          height: 44,
          padding: padding,
          alignment: alignment,
          child: Text(
            label,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: kSimpleText.copyWith(
              color: selected ? AraColors.selectionFg : AraColors.textSecondary,
            ),
          ),
        ),
      ),
    ),
  );
}

/// A row of [SimpleSegment]s on one tile: [options] as (value, label, id),
/// the selected one in the selection blue. A null [onPick] locks them, and
/// the picked one takes no tap. [expand]: the segments share the width; else
/// they hug their labels. Keys read '[keyPrefix]-id'.
class SimpleSegments<T> extends StatelessWidget {
  const SimpleSegments({
    super.key,
    required this.options,
    required this.selected,
    required this.onPick,
    this.keyPrefix,
    this.expand = true,
    this.group,
  });

  final List<(T, String, String)> options;
  final T? selected;
  final ValueChanged<T>? onPick;
  final String? keyPrefix;
  final bool expand;

  /// Read with each segment: what the row chooses.
  final String? group;

  @override
  Widget build(BuildContext context) => DecoratedBox(
    decoration: BoxDecoration(
      color: AraColors.tileFill,
      borderRadius: BorderRadius.circular(8),
    ),
    child: Row(
      mainAxisSize: expand ? MainAxisSize.max : MainAxisSize.min,
      children: [
        for (final (value, label, id) in options)
          () {
            final segment = SimpleSegment(
              key: keyPrefix == null ? null : Key('$keyPrefix-$id'),
              label: label,
              group: group,
              selected: value == selected,
              radius: 8,
              onTap: onPick == null || value == selected
                  ? null
                  : () => onPick!(value),
            );
            return expand ? Expanded(child: segment) : Flexible(child: segment);
          }(),
      ],
    ),
  );
}

/// Simple Mode's density (DESIGN.md "Two Densities"): 10 px corners on
/// buttons and fields instead of Material's pills, 44 px minimum targets,
/// fields as borderless tiles.
/// Simple Mode's green go buttons (Start Preflight, Done, Create and start):
/// dark text on the green, 6.3:1 (white is 2.8:1).
final kSimpleGoStyle = FilledButton.styleFrom(
  backgroundColor: AraColors.accentConnected,
  foregroundColor: AraColors.bgPrimary,
);

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
    // Desktop defaults to compact density, which takes buttons under 44 px.
    visualDensity: VisualDensity.standard,
    filledButtonTheme: FilledButtonThemeData(
      style: buttons.merge(base.filledButtonTheme.style),
    ),
    iconButtonTheme: IconButtonThemeData(
      style: const ButtonStyle(
        minimumSize: WidgetStatePropertyAll(Size(44, 44)),
      ).merge(base.iconButtonTheme.style),
    ),
    // The theme's primary (#1565C0) reads at 3:1 on a panel and 2:1 on a
    // tile; labels need 4.5:1 (WCAG 2.2 AA): text buttons in linkText,
    // outlined ones in white.
    textButtonTheme: TextButtonThemeData(
      style: buttons
          .merge(TextButton.styleFrom(foregroundColor: AraColors.linkText))
          .merge(base.textButtonTheme.style),
    ),
    outlinedButtonTheme: OutlinedButtonThemeData(
      style: buttons
          .merge(
            OutlinedButton.styleFrom(foregroundColor: AraColors.textPrimary),
          )
          .merge(base.outlinedButtonTheme.style),
    ),
    inputDecorationTheme: base.inputDecorationTheme.copyWith(
      // 48 px tiles (DESIGN.md layout C), label inside.
      isDense: true,
      contentPadding: const EdgeInsets.fromLTRB(12, 8, 12, 8),
      filled: true,
      fillColor: AraColors.tileFill,
      border: tile,
      enabledBorder: tile,
      focusedBorder: tile.copyWith(
        borderSide: const BorderSide(color: AraColors.accentInfo),
      ),
    ),
  );
}

/// Simple Mode's Settings (design/briefs/simple-mode-settings.md): a sheet
/// with its sections on the left and the picked one on the right, on the
/// 90 % panel scrim over a 5 px blur of the image.
class _SettingsSheet extends ConsumerStatefulWidget {
  const _SettingsSheet({
    super.key,
    this.section = 'Rig',
    required this.onClose,
    this.revealStorage = false,
  });

  /// The section it opens on.
  final String section;
  final VoidCallback onClose;

  /// Rig opens scrolled to its Storage block (Plan's Open Storage).
  final bool revealStorage;

  @override
  ConsumerState<_SettingsSheet> createState() => _SettingsSheetState();
}

class _SettingsSheetState extends ConsumerState<_SettingsSheet> {
  late String _section = widget.section;

  @override
  Widget build(BuildContext context) {
    // The devices with a window of their own, listed under Devices once the
    // rig knows them.
    // A device keeps the icon its dot has in the top bar.
    Widget glyph(IconData icon, Color c) => Icon(icon, size: 20, color: c);
    final l10n = context.l10n;
    // Only a device appearing or going rebuilds this, not each 1 s reading.
    // The id picks the section (and a search hit names it); the label shows.
    final sections = <(String, String, Widget Function(Color), bool)>[
      ('Rig', l10n.shellSectionRig, (c) => glyph(Icons.memory, c), false),
      (
        'Devices',
        l10n.shellSectionDevices,
        (c) => glyph(Icons.settings_input_component_outlined, c),
        false,
      ),
      if (ref.watch(cameraStatusProvider.select((v) => v.value != null)))
        (
          'Camera',
          l10n.shellSectionCamera,
          (c) => glyph(Icons.camera_alt, c),
          true,
        ),
      if (ref.watch(mountProvider.select((v) => v.value != null)))
        (
          'Mount',
          l10n.shellSectionMount,
          (c) => EquipmentGlyph.telescope(size: 20, color: c),
          true,
        ),
      if (ref.watch(focuserProvider.select((v) => v.value != null)))
        (
          'Focuser',
          l10n.shellSectionFocuser,
          (c) => glyph(Icons.center_focus_strong, c),
          true,
        ),
      if (ref.watch(filterWheelProvider.select((v) => v.value != null)))
        (
          'Filter wheel',
          l10n.shellFilterWheel,
          (c) => EquipmentGlyph.filterWheel(size: 20, color: c),
          true,
        ),
      if (ref.watch(rotatorProvider.select((v) => v.value != null)))
        (
          'Rotator',
          l10n.shellRotator,
          (c) => glyph(Icons.rotate_right, c),
          true,
        ),
      if (ref.watch(
        switchListProvider.select((v) => v.value?.isNotEmpty ?? false),
      ))
        ('Switches', l10n.shellSwitches, (c) => glyph(Icons.toggle_on, c), true),
      if (ref.watch(flatPanelProvider.select((v) => v.value != null)))
        (
          'Flat panel',
          l10n.shellFlatPanel,
          (c) => EquipmentGlyph.flatPanel(size: 20, color: c),
          true,
        ),
      if (ref.watch(weatherProvider.select((v) => v.value != null)))
        (
          'Weather',
          l10n.shellWeather,
          (c) => glyph(Icons.cloud_outlined, c),
          true,
        ),
      if (ref.watch(safetyMonitorProvider.select((v) => v.value != null)))
        (
          'Safety monitor',
          l10n.shellSafetyMonitor,
          (c) => glyph(Icons.verified_user_outlined, c),
          true,
        ),
      if (ref.watch(domeProvider.select((v) => v.value != null)))
        (
          'Dome',
          l10n.shellDome,
          (c) => EquipmentGlyph.dome(size: 20, color: c),
          true,
        ),
      if (ref.watch(guiderStatusProvider.select((v) => v.value != null)))
        (
          'Guider',
          l10n.shellSectionGuider,
          (c) => glyph(Icons.videocam_outlined, c),
          true,
        ),
    ];
    // A narrow window (Split View, a phone) keeps only the sections' icons.
    final narrow = MediaQuery.sizeOf(context).width < 600;
    return SimplePanel(
      key: const Key('simple-menu'),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          SizedBox(
            width: narrow ? 60 : 200 * simpleRailScale(context),
            child: ListView(
              padding: const EdgeInsets.all(AraSpace.s8),
              children: [
                for (final (name, title, icon, device) in sections)
                  Padding(
                    padding: EdgeInsets.only(
                      bottom: 4,
                      left: device && !narrow ? AraSpace.s16 : 0,
                    ),
                    child: Material(
                      color: _section == name
                          ? AraColors.selectionTint
                          : Colors.transparent,
                      borderRadius: BorderRadius.circular(8),
                      child: InkWell(
                        key: Key('simple-menu-nav-$name'),
                        borderRadius: BorderRadius.circular(8),
                        onTap: () => setState(() => _section = name),
                        child: Semantics(
                          selected: _section == name,
                          label: narrow ? title : null,
                          child: ConstrainedBox(
                            constraints: const BoxConstraints(minHeight: 44),
                            child: Padding(
                              padding: const EdgeInsets.symmetric(
                                horizontal: AraSpace.s12,
                                vertical: AraSpace.s8,
                              ),
                              child: Row(
                                spacing: AraSpace.s12,
                                children: [
                                  icon(
                                    _section == name
                                        ? AraColors.linkText
                                        : AraColors.textPrimary,
                                  ),
                                  if (!narrow)
                                    Expanded(
                                      child: Text(
                                        title,
                                        style: kSimpleText.copyWith(
                                          color: _section == name
                                              ? AraColors.linkText
                                              : null,
                                        ),
                                      ),
                                    ),
                                ],
                              ),
                            ),
                          ),
                        ),
                      ),
                    ),
                  ),
              ],
            ),
          ),
          const VerticalDivider(width: 1, color: AraColors.border),
          Expanded(
            child: SingleChildScrollView(
              padding: const EdgeInsets.all(AraSpace.s16),
              child: switch (_section) {
                'Rig' => _ThisRig(
                  onClose: widget.onClose,
                  revealStorage: widget.revealStorage,
                ),
                'Camera' => const SimpleCameraWindow(inSettings: true),
                'Mount' => const SimpleMountWindow(inSettings: true),
                'Focuser' => const SimpleFocuserWindow(inSettings: true),
                'Filter wheel' => const SimpleFilterWheelWindow(
                  inSettings: true,
                ),
                'Rotator' => const SimpleRotatorWindow(inSettings: true),
                'Switches' => const SimpleSwitchesWindow(inSettings: true),
                'Flat panel' => const SimpleFlatPanelWindow(inSettings: true),
                'Weather' => const SimpleWeatherWindow(inSettings: true),
                'Safety monitor' => const SimpleSafetyMonitorWindow(
                  inSettings: true,
                ),
                'Dome' => const SimpleDomeWindow(inSettings: true),
                'Guider' => const SimpleGuiderWindow(inSettings: true),
                _ => _Gear(onOpen: (id) => setState(() => _section = id)),
              },
            ),
          ),
        ],
      ),
    );
  }
}

/// Settings › Devices: the profile's devices with their state, and the only
/// place Simple Mode connects or disconnects one (a device's own window has
/// neither). Ara connects the profile's gear on its own; Connect asks the rig
/// for the device it remembers, never a chooser.
class _Gear extends ConsumerWidget {
  const _Gear({required this.onOpen});

  /// Opens a device's window (the ones that have one) in Settings.
  final ValueChanged<String> onOpen;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    if (!ref.watch(serverLinkUpProvider)) {
      return Column(
        key: const Key('simple-gear'),
        crossAxisAlignment: CrossAxisAlignment.start,
        spacing: AraSpace.s8,
        children: [
          Text(context.l10n.shellRigNotConnected, style: AraText.caption),
        ],
      );
    }
    final l10n = context.l10n;
    // Each row with its shown type, so the list sorts itself in any locale.
    final rows = <(String, Widget)>[
      (l10n.shellSectionCamera, _GearRow(
          id: 'camera',
          deviceType: EquipmentDeviceType.camera,
          type: l10n.shellSectionCamera,
          provider: cameraStatusProvider,
          onOpen: () => onOpen('Camera'),
        )),
      (l10n.shellSectionMount, _GearRow(
          id: 'mount',
          deviceType: EquipmentDeviceType.mount,
          type: l10n.shellSectionMount,
          provider: mountProvider,
          onOpen: () => onOpen('Mount'),
        )),
      (l10n.shellSectionFocuser, _GearRow(
          id: 'focuser',
          deviceType: EquipmentDeviceType.focuser,
          type: l10n.shellSectionFocuser,
          provider: focuserProvider,
          onOpen: () => onOpen('Focuser'),
        )),
      (l10n.shellSectionGuider, _GuiderGearRow(onOpen: () => onOpen('Guider'))),
      (l10n.shellFilterWheel, _GearRow(
          id: 'filterwheel',
          deviceType: EquipmentDeviceType.filterWheel,
          type: l10n.shellFilterWheel,
          provider: filterWheelProvider,
          onOpen: () => onOpen('Filter wheel'),
        )),
      (l10n.shellRotator, _GearRow(
          id: 'rotator',
          deviceType: EquipmentDeviceType.rotator,
          type: l10n.shellRotator,
          provider: rotatorProvider,
          onOpen: () => onOpen('Rotator'),
        )),
      (l10n.shellFlatPanel, _GearRow(
          id: 'flat',
          deviceType: EquipmentDeviceType.flatPanel,
          type: l10n.shellFlatPanel,
          provider: flatPanelProvider,
          onOpen: () => onOpen('Flat panel'),
        )),
      (l10n.shellSwitches, _SwitchGearRows(onOpen: () => onOpen('Switches'))),
      (l10n.shellDome, _GearRow(
          id: 'dome',
          deviceType: EquipmentDeviceType.dome,
          type: l10n.shellDome,
          provider: domeProvider,
          onOpen: () => onOpen('Dome'),
        )),
      (l10n.shellWeather, _GearRow(
          id: 'weather',
          deviceType: EquipmentDeviceType.weather,
          type: l10n.shellWeather,
          provider: weatherProvider,
          onOpen: () => onOpen('Weather'),
        )),
      (l10n.shellSafetyMonitor, _GearRow(
          id: 'safety',
          deviceType: EquipmentDeviceType.safetyMonitor,
          type: l10n.shellSafetyMonitor,
          provider: safetyMonitorProvider,
          onOpen: () => onOpen('Safety monitor'),
        )),
    ]..sort((a, b) => simpleCompareNames(a.$1, b.$1));
    return Column(
      key: const Key('simple-gear'),
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s8,
      children: [
        for (final row in rows) row.$2,
        const _AddGearRow(),
      ],
    );
  }
}

/// Devices this rig has none of yet: pick one through the Full UI's chooser.
class _AddGearRow extends ConsumerWidget {
  const _AddGearRow();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = context.l10n;
    (EquipmentDeviceType, String, Future<bool> Function(DiscoveredDevice))
    entry<T extends EquipmentDeviceStatus>(
      EquipmentDeviceType type,
      String label,
      AsyncNotifierProvider<EquipmentDeviceNotifier<T>, T?> provider,
    ) => (type, label, ref.read(provider.notifier).connect);
    final missing = [
      if (ref.watch(cameraStatusProvider).value == null)
        entry(EquipmentDeviceType.camera, l10n.shellSectionCamera, cameraStatusProvider),
      if (ref.watch(mountProvider).value == null)
        entry(EquipmentDeviceType.mount, l10n.shellSectionMount, mountProvider),
      if (ref.watch(focuserProvider).value == null)
        entry(EquipmentDeviceType.focuser, l10n.shellSectionFocuser, focuserProvider),
      if (ref.watch(filterWheelProvider).value == null)
        entry(EquipmentDeviceType.filterWheel, l10n.shellFilterWheel, filterWheelProvider),
      if (ref.watch(rotatorProvider).value == null)
        entry(EquipmentDeviceType.rotator, l10n.shellRotator, rotatorProvider),
      if (ref.watch(flatPanelProvider).value == null)
        entry(EquipmentDeviceType.flatPanel, l10n.shellFlatPanel, flatPanelProvider),
      if (ref.watch(domeProvider).value == null)
        entry(EquipmentDeviceType.dome, l10n.shellDome, domeProvider),
      if (ref.watch(weatherProvider).value == null)
        entry(EquipmentDeviceType.weather, l10n.shellWeather, weatherProvider),
      if (ref.watch(safetyMonitorProvider).value == null)
        entry(EquipmentDeviceType.safetyMonitor, l10n.shellSafetyMonitor, safetyMonitorProvider),
      if ((ref.watch(switchListProvider).value ?? const []).isEmpty)
        (
          EquipmentDeviceType.switchDevice,
          l10n.shellSwitches,
          ref.read(switchListProvider.notifier).connect,
        ),
    ];
    if (missing.isEmpty) return const SizedBox.shrink();
    return _gearTile(
      id: 'add',
      title: l10n.shellAddDevice,
      dot: StatusLevel.disconnected,
      word: missing.map((m) => m.$2).join(', '),
      verb: null,
      label: null,
      action: null,
      choose: (context, close) => _AddGearPanel(missing: missing, close: close),
      onOpen: null,
    );
  }
}

class _GearRow<T extends EquipmentDeviceStatus> extends ConsumerWidget {
  const _GearRow({
    required this.id,
    required this.type,
    required this.provider,
    required this.deviceType,
    this.onOpen,
  });

  /// The device's window, for the ones that have one.
  final VoidCallback? onOpen;

  final String id;
  final String type;
  final EquipmentDeviceType deviceType;
  final AsyncNotifierProvider<EquipmentDeviceNotifier<T>, T?> provider;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final status = ref.watch(provider).value;
    final notifier = ref.read(provider.notifier);
    final l10n = context.l10n;
    // Connect only reconnects what the rig remembers (#1710): Choose picks a
    // device, through the Full UI's chooser, in place.
    final choose = _deviceChooser(deviceType, type, notifier.connect);
    // Never set up on this rig: the Add a device row picks it.
    if (status == null) return const SizedBox.shrink();
    // The dot is the top bar chip's, so the two always agree.
    final dot = equipmentStatusLevel(status);
    final word = switch (status.connectionState) {
      EquipmentConnectionState.connected => l10n.shellConnected,
      EquipmentConnectionState.connecting => l10n.shellConnecting,
      EquipmentConnectionState.error => l10n.shellFailed,
      _ => l10n.shellNotConnected,
    };
    final (verb, label, action) = switch (status.connectionState) {
      EquipmentConnectionState.connected => (
        'disconnect',
        l10n.shellDisconnect,
        notifier.disconnect,
      ),
      EquipmentConnectionState.connecting => (null, null, null),
      EquipmentConnectionState.error => (
        'reconnect',
        l10n.shellReconnect,
        notifier.reconnect,
      ),
      _ => ('connect', l10n.shellConnect, notifier.reconnect),
    };
    return _gearTile(
      id: id,
      title: status.name.isEmpty
          ? type
          : l10n.shellGearTitle(type, status.name),
      dot: dot,
      word: word,
      verb: verb,
      label: label,
      action: action,
      choose: status.isConnected || status.isConnecting ? null : choose,
      onOpen: onOpen,
    );
  }
}

/// One row per switch device: their own state type, all opening the one
/// Switches window.
class _SwitchGearRows extends ConsumerWidget {
  const _SwitchGearRows({required this.onOpen});

  final VoidCallback onOpen;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final devices = ref.watch(switchListProvider).value ?? const [];
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s8,
      children: [for (final d in devices) _row(context, ref, d)],
    );
  }

  _GearChooser _choose(BuildContext context, WidgetRef ref) => _deviceChooser(
    EquipmentDeviceType.switchDevice,
    context.l10n.shellSwitches,
    ref.read(switchListProvider.notifier).connect,
  );

  Widget _row(BuildContext context, WidgetRef ref, SwitchDevice d) {
    final notifier = ref.read(switchListProvider.notifier);
    final l10n = context.l10n;
    Future<void> connect() => notifier.reconnectDevice(d.deviceId);
    // The dot follows the top bar chip's rule, for one device.
    final (dot, word, verb, label, action) = switch (d.connectionState) {
      SwitchConnectionState.connected => (
        StatusLevel.connected,
        l10n.shellConnected,
        'disconnect',
        l10n.shellDisconnect,
        () => notifier.disconnect(d.deviceId),
      ),
      SwitchConnectionState.connecting => (
        StatusLevel.changing,
        l10n.shellConnecting,
        null,
        null,
        null,
      ),
      SwitchConnectionState.error => (
        StatusLevel.error,
        l10n.shellFailed,
        'reconnect',
        l10n.shellReconnect,
        connect,
      ),
      _ => (
        StatusLevel.disconnected,
        l10n.shellNotConnected,
        'connect',
        l10n.shellConnect,
        connect,
      ),
    };
    return _gearTile(
      id: 'switch-${d.deviceId}',
      title: d.name.isEmpty
          ? l10n.shellSwitches
          : l10n.shellGearTitle(l10n.shellSwitches, d.name),
      dot: dot,
      word: word,
      verb: verb,
      label: label,
      action: action,
      choose:
          d.connectionState == SwitchConnectionState.connected ||
              d.connectionState == SwitchConnectionState.connecting
          ? null
          : _choose(context, ref),
      onOpen: onOpen,
    );
  }
}

/// The guider's row: its own state type, named by its camera as its window
/// is.
class _GuiderGearRow extends ConsumerWidget {
  const _GuiderGearRow({required this.onOpen});

  final VoidCallback onOpen;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(guiderStatusProvider);
    if (async.isLoading && !async.hasValue) return const SizedBox.shrink();
    // The rig forgets a disconnected guider (no status at all), so it stays
    // here as Not connected: Connect is the way back.
    final status = async.value;
    final notifier = ref.read(guiderStatusProvider.notifier);
    final camera = ref.watch(deviceDriverProvider('guidecamera')).value?.name;
    final l10n = context.l10n;
    // The dot is the top bar chip's, so the two always agree.
    final dot = guiderStatusLevel(status);
    final (word, verb, label, action) = switch (status?.connectionState) {
      GuiderConnectionState.connected => (
        l10n.shellConnected,
        'disconnect',
        l10n.shellDisconnect,
        notifier.disconnect,
      ),
      GuiderConnectionState.connecting => (
        l10n.shellConnecting,
        null,
        null,
        null,
      ),
      GuiderConnectionState.error => (
        l10n.shellFailed,
        'reconnect',
        l10n.shellReconnect,
        notifier.connect,
      ),
      _ => (
        l10n.shellNotConnected,
        'connect',
        l10n.shellConnect,
        notifier.connect,
      ),
    };
    return _gearTile(
      id: 'guider',
      title: camera == null
          ? l10n.shellSectionGuider
          : l10n.shellGearTitle(l10n.shellSectionGuider, camera),
      dot: dot,
      word: word,
      verb: verb,
      label: label,
      action: action,
      onOpen: status == null ? null : onOpen,
    );
  }
}

/// What a device row's Choose opens under the row; [close] folds it again.
typedef _GearChooser = Widget Function(BuildContext context, VoidCallback close);

/// The Full UI's device chooser, in place; [connect] takes the picked device.
_GearChooser _deviceChooser(
  EquipmentDeviceType type,
  String typeLabel,
  Future<bool> Function(DiscoveredDevice device) connect,
) => (context, close) => SimpleHostedText(
  child: AlpacaDeviceChooser(
  type: type,
  deviceTypeLabel: typeLabel,
  onClose: close,
  onPick: (device) async {
    final notify = userNotifier(context);
    final l10n = context.l10n;
    try {
      await connect(device);
    } catch (e) {
      notify(
        l10n.shellGearActionFailed(l10n.shellConnect, device.name),
        detail: describeEquipmentError(e),
        error: true,
      );
    }
  },
),
);

/// Add a device, in place: the types the rig has none of, then the chooser
/// for the one tapped.
class _AddGearPanel extends StatefulWidget {
  const _AddGearPanel({required this.missing, required this.close});

  final List<
    (EquipmentDeviceType, String, Future<bool> Function(DiscoveredDevice))
  >
  missing;
  final VoidCallback close;

  @override
  State<_AddGearPanel> createState() => _AddGearPanelState();
}

class _AddGearPanelState extends State<_AddGearPanel> {
  EquipmentDeviceType? _type;

  @override
  Widget build(BuildContext context) {
    final picked = widget.missing.where((m) => m.$1 == _type).firstOrNull;
    if (picked != null) {
      return _deviceChooser(
        picked.$1,
        picked.$2,
        picked.$3,
      )(context, widget.close);
    }
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        for (final m in widget.missing)
          TextButton(
            key: Key('simple-gear-add-${m.$1.name}'),
            style: TextButton.styleFrom(
              minimumSize: const Size(0, 44),
              alignment: Alignment.centerLeft,
            ),
            onPressed: () => setState(() => _type = m.$1),
            child: Text(m.$2, style: kSimpleText),
          ),
      ],
    );
  }
}

/// A device's row, with what its Choose opens folded out under it.
class _GearTile extends StatefulWidget {
  const _GearTile({required this.chooser, required this.row});

  final _GearChooser? chooser;

  /// The row; given what its Choose should call.
  final Widget Function(VoidCallback? toggle) row;

  @override
  State<_GearTile> createState() => _GearTileState();
}

class _GearTileState extends State<_GearTile> {
  bool _open = false;

  @override
  Widget build(BuildContext context) {
    final chooser = widget.chooser;
    final row = widget.row(
      chooser == null ? null : () => setState(() => _open = !_open),
    );
    if (!_open || chooser == null) return row;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s8,
      children: [
        row,
        Padding(
          padding: const EdgeInsets.symmetric(horizontal: AraSpace.s12),
          child: chooser(context, () => setState(() => _open = false)),
        ),
      ],
    );
  }
}

Widget _gearTile({
  required String id,
  required String title,
  required StatusLevel dot,
  required String word,
  // The button's English verb, for its key; [label] is what it shows.
  required String? verb,
  required String? label,
  required Future<void> Function()? action,
  required VoidCallback? onOpen,
  _GearChooser? choose,
}) => _GearTile(
  chooser: choose,
  row: (toggle) => _gearRow(
    id: id,
    title: title,
    dot: dot,
    word: word,
    verb: verb,
    label: label,
    action: action,
    onOpen: onOpen,
    toggle: toggle,
  ),
);

Widget _gearRow({
  required String id,
  required String title,
  required StatusLevel dot,
  required String word,
  required String? verb,
  required String? label,
  required Future<void> Function()? action,
  required VoidCallback? onOpen,
  required VoidCallback? toggle,
}) {
  // Ink, so the focus and press highlight paints over the fill, not under.
  final row = Ink(
    padding: const EdgeInsets.symmetric(
      horizontal: AraSpace.s12,
      vertical: AraSpace.s8,
    ),
    decoration: BoxDecoration(
      color: AraColors.tileFill,
      borderRadius: BorderRadius.circular(10),
    ),
    child: Row(
      spacing: AraSpace.s12,
      children: [
        StatusDot.of(dot),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(title, style: kSimpleText),
              Text(word, style: AraText.caption),
            ],
          ),
        ),
        if (label != null)
          Builder(
            builder: (context) => TextButton(
              key: Key('simple-gear-$id-$verb'),
              style: TextButton.styleFrom(minimumSize: const Size(0, 44)),
              // The device notifiers throw why a command failed: say so,
              // or a refused Connect looks like a dead button.
              onPressed: () async {
                final notify = userNotifier(context);
                final l10n = context.l10n;
                try {
                  await action!();
                } catch (e) {
                  notify(
                    l10n.shellGearActionFailed(label, title),
                    detail: describeEquipmentError(e),
                    error: true,
                  );
                }
              },
              child: Text(label),
            ),
          ),
        if (toggle != null)
          Builder(
            builder: (context) => TextButton(
              key: Key('simple-gear-$id-choose'),
              style: TextButton.styleFrom(minimumSize: const Size(0, 44)),
              onPressed: toggle,
              child: Text(context.l10n.shellChooseDevice),
            ),
          ),
        if (onOpen != null)
          const Icon(Icons.chevron_right, color: AraColors.textSecondary),
      ],
    ),
  );
  // Its own transparent Material: the fill and highlight paint here, over
  // the sheet's scrim. A device with a window opens it from its row.
  return Material(
    type: MaterialType.transparency,
    child: onOpen == null
        ? row
        : Semantics(
            button: true,
            // Focusable, so a keyboard opens the window too.
            child: InkWell(
              key: Key('simple-gear-$id'),
              borderRadius: BorderRadius.circular(10),
              onTap: onOpen,
              child: row,
            ),
          ),
  );
}

/// Settings › Rig: what belongs to no device. The rig's figures, the rig
/// and profile with the way to change them, the one setting Ara can't find out
/// (a permanent pier, asked once), the way out of Simple Mode, the version
/// and the logs.
class _ThisRig extends ConsumerWidget {
  const _ThisRig({required this.onClose, this.revealStorage = false});

  final VoidCallback onClose;
  final bool revealStorage;

  /// Apply a value and keep it in the profile; a failed save says so.
  static Future<void> _save(
    BuildContext context,
    WidgetRef ref,
    void Function() apply,
    Future<Object?> Function(ProfileApi api) persist,
  ) async {
    apply();
    final notify = userNotifier(context);
    final l10n = context.l10n;
    final api = ref.read(profileApiProvider);
    if (api == null) {
      // Shown, not kept: the next read from the rig puts the old value back.
      notify(
        l10n.shellCouldntSave,
        detail: l10n.shellRigNotConnected,
        error: true,
      );
      return;
    }
    try {
      await persist(api);
    } catch (e) {
      notify(
        l10n.shellCouldntSave,
        detail: friendlyError(l10n, e),
        error: true,
      );
    }
  }

  /// The site from this device (its GPS, or the rig's held fix): the same
  /// lookup as the full UI's Fill from GPS, with the time zone those
  /// coordinates fall in, saved at once; and this device's clock to the rig.
  static Future<void> _useLocation(BuildContext context, WidgetRef ref) async {
    final notify = userNotifier(context);
    final l10n = context.l10n;
    final clock = ref.read(timeSyncApiProvider);
    if (clock != null) {
      try {
        await clock.pushClientTime(DateTime.now().toUtc());
      } catch (e) {
        notify(
          l10n.shellCouldntSetClock,
          detail: friendlyError(l10n, e),
          error: true,
        );
      }
    }
    if (!context.mounted) return; // Rig closed while the clock was set
    final r = await fillSiteFromGps(ref);
    if (!context.mounted) return;
    if (!r.success) {
      notify(l10n.shellCouldntGetLocation, detail: r.message, error: true);
      return;
    }
    final site = ref.read(siteSettingsProvider.notifier);
    site.setLatitudeDeg(r.lat);
    site.setLongitudeDeg(r.lng);
    if (r.alt != null) site.setElevationM(r.alt!);
    site.setTimeZone(tz_map.latLngToTimezoneString(r.lat, r.lng));
    final api = ref.read(profileApiProvider);
    if (api == null) {
      notify(
        l10n.shellCouldntSaveSite,
        detail: l10n.shellRigNotConnected,
        error: true,
      );
      return;
    }
    try {
      await site.persistToServer(api);
    } catch (e) {
      notify(
        l10n.shellCouldntSaveSite,
        detail: friendlyError(l10n, e),
        error: true,
      );
      return;
    }
    notify(l10n.shellSiteFrom(r.sourceLabel));
  }

  /// The profile's guide optics. With [retry] (the guider connected): the
  /// rig fills unset ones a few guider round trips later, so one read then
  /// usually beats it: read again each second, up to ten times, until both
  /// are set.
  static Future<void> _readGuideOptics(
    BuildContext context,
    WidgetRef ref, {
    bool retry = true,
  }) async {
    for (var i = 0; i < (retry ? 10 : 1); i++) {
      if (i > 0) await Future<void>.delayed(const Duration(seconds: 1));
      if (!context.mounted) return;
      final api = ref.read(profileApiProvider);
      if (api == null) return;
      try {
        await ref.read(phd2SettingsProvider.notifier).hydrateFromServer(api);
      } catch (_) {
        // Unreachable for a moment: the next try reads again.
      }
      if (!context.mounted) return;
      final s = ref.read(phd2SettingsProvider);
      if (s.guideFocalLength > 0 && s.guidePixelSize > 0) return;
    }
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final server = ref.watch(activeServerProvider);
    final profiles = ref.watch(profileManagementProvider).value;
    final profile = profiles?.profiles
        .where((p) => p.id == profiles.activeId)
        .firstOrNull
        ?.name;
    final running = ref.watch(
      sequenceRunStateProvider.select((s) => s.value?.state?.isActive ?? false),
    );
    final version = ref.watch(appDisplayVersionProvider).value;
    final rig = ref.watch(daemonVersionsProvider).value;
    final stars = ref.watch(plateSolveDatabaseStatusProvider).value?.databases;
    final servers = rig?.alpacaServers ?? const <AlpacaServerVersion>[];
    // Name and Profile, each with its change button on the same line; set
    // close together (two facts, not two settings).
    final l10n = context.l10n;
    // A list-style setting: the platform's switch (DESIGN.md: segments stay
    // for the panels over the image).
    Widget toggle(
      String id,
      String label,
      String? hint,
      bool on,
      ValueChanged<bool> set,
    ) => MergeSemantics(
      // Read as one control: "Permanent pier, switch, off".
      child: Row(
        spacing: AraSpace.s12,
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(label, style: kSimpleText),
                if (hint != null) Text(hint, style: AraText.caption),
              ],
            ),
          ),
          Switch.adaptive(
            key: Key('simple-menu-$id'),
            value: on,
            onChanged: set,
          ),
        ],
      ),
    );
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s8,
      children: [
        const SimpleSystemPanel(expand: true),
        Column(
          children: [
            _ChangeRow(
              buttonKey: const Key('simple-rig-change-rig'),
              label: l10n.shellName,
              value: server == null ? '—' : server.mdnsName ?? server.hostname,
              change: l10n.shellChangeRig,
              onTap: () => _changeRigOrProfile(ref, onClose, rig: true),
            ),
            _ChangeRow(
              buttonKey: const Key('simple-rig-change-profile'),
              label: l10n.shellProfile,
              value: profile ?? '—',
              change: l10n.shellChangeProfile,
              onTap: () => _changeRigOrProfile(ref, onClose, rig: false),
            ),
          ],
        ),
        if (running)
          Text(
            l10n.shellPlanRunning,
            style: AraText.caption.copyWith(color: AraColors.accentBusy),
          ),
        // The telescope and the site: set once, kept in the profile (plate
        // solving, framing and the night's times use them).
        const Divider(height: AraSpace.s16),
        for (final (id, label, digits, value, set)
            in <(String, String, int?, double, void Function(double))>[
              // Aperture on one wheel of millimetres; focal length per digit.
              (
                'aperture',
                l10n.shellAperture,
                null,
                ref.watch(opticsSettingsProvider).apertureMm,
                ref.read(opticsSettingsProvider.notifier).setApertureMm,
              ),
              (
                'focal',
                l10n.shellFocalLength,
                4,
                ref.watch(opticsSettingsProvider).focalLengthMm,
                ref.read(opticsSettingsProvider.notifier).setFocalLengthMm,
              ),
            ])
          _RigWheelRow.millimetres(
            l10n,
            id: id,
            label: label,
            digits: digits,
            value: value,
            onSet: (v) => _save(
              context,
              ref,
              () => set(v),
              (api) => ref
                  .read(opticsSettingsProvider.notifier)
                  .persistToServer(api),
            ),
          ),
        // The guide scope: read from the guider when the profile left it
        // unset, kept in the profile (polar alignment solves with it).
        _GuideOpticsReader(
          child: _RigWheelRow.millimetres(
            l10n,
            id: 'guidefocal',
            label: l10n.shellGuideFocalLength,
            digits: 4,
            value: ref.watch(phd2SettingsProvider).guideFocalLength.toDouble(),
            onSet: (v) => _save(
              context,
              ref,
              () => ref
                  .read(phd2SettingsProvider.notifier)
                  .setGuideFocalLength(v.round()),
              // The save sends the whole section: re-read it first so a
              // pixel size the rig filled since Rig's read isn't sent back
              // as 0 (ALP-4982).
              (api) async {
                final guider = ref.read(phd2SettingsProvider.notifier);
                await guider.hydrateFromServer(api);
                guider.setGuideFocalLength(v.round());
                return guider.persistToServer(api);
              },
            ),
          ),
        ),
        ConstrainedBox(
          constraints: const BoxConstraints(minHeight: 44),
          child: Row(
            children: [
              Expanded(
                child: Text(l10n.shellGuidePixelSize, style: kSimpleText),
              ),
              // Read only (it comes from the guider), in the same tile as
              // the value above it: its button, never enabled, read as text.
              Builder(
                builder: (context) {
                  final pixel = switch (ref
                      .watch(phd2SettingsProvider)
                      .guidePixelSize) {
                    final p when p > 0 => '${p.toStringAsFixed(2)} µm',
                    _ => l10n.shellNotSet,
                  };
                  return Semantics(
                    excludeSemantics: true,
                    label: pixel,
                    child: TextButton(
                      key: const Key('simple-rig-guidepixel'),
                      style: TextButton.styleFrom(
                        backgroundColor: AraColors.tileFill,
                        disabledBackgroundColor: AraColors.tileFill,
                        minimumSize: const Size(kSimpleRigControlWidth, 44),
                      ),
                      onPressed: null,
                      child: Text(pixel, style: kSimpleText),
                    ),
                  );
                },
              ),
            ],
          ),
        ),
        for (final (id, label, value, set)
            in <(String, String, double, void Function(double))>[
              (
                'lat',
                l10n.shellLatitude,
                ref.watch(siteSettingsProvider).latitudeDeg,
                ref.read(siteSettingsProvider.notifier).setLatitudeDeg,
              ),
              (
                'lng',
                l10n.shellLongitude,
                ref.watch(siteSettingsProvider).longitudeDeg,
                ref.read(siteSettingsProvider.notifier).setLongitudeDeg,
              ),
            ])
          _RigWheelRow.coordinate(
            l10n,
            id: id,
            label: label,
            value: value,
            onSet: (v) => _save(
              context,
              ref,
              () => set(v),
              (api) =>
                  ref.read(siteSettingsProvider.notifier).persistToServer(api),
            ),
          ),
        _RigStepper(
          id: 'elevation',
          label: l10n.shellElevation,
          unit: 'm',
          value: ref.watch(siteSettingsProvider).elevationM,
          onSet: (v) => _save(
            context,
            ref,
            () => ref.read(siteSettingsProvider.notifier).setElevationM(v),
            (api) =>
                ref.read(siteSettingsProvider.notifier).persistToServer(api),
          ),
        ),
        Align(
          alignment: Alignment.centerLeft,
          child: TextButton.icon(
            key: const Key('simple-rig-use-location'),
            onPressed: () => _useLocation(context, ref),
            icon: const Icon(Icons.my_location, size: 18),
            label: Text(l10n.shellUseLocation(thisDeviceLabel(clientPlatform))),
          ),
        ),
        const Divider(height: AraSpace.s16),
        toggle(
          'pier',
          l10n.shellPermanentPier,
          l10n.shellPermanentPierHint,
          ref.watch(simplePermanentPierProvider).value ?? false,
          (v) => ref.read(simplePermanentPierProvider.notifier).set(v),
        ),
        // Each language in its own words; System follows the device.
        Wrap(
          spacing: AraSpace.s12,
          runSpacing: AraSpace.s8,
          alignment: WrapAlignment.spaceBetween,
          crossAxisAlignment: WrapCrossAlignment.center,
          children: [
            Text(l10n.shellLanguage, style: kSimpleText),
            DecoratedBox(
              decoration: BoxDecoration(
                color: AraColors.tileFill,
                borderRadius: BorderRadius.circular(8),
              ),
              // Wraps at large text sizes rather than overflow.
              child: Wrap(
                children: [
                  for (final (id, locale, name) in [
                    ('system', null, l10n.shellLanguageSystem),
                    ('en', const Locale('en'), 'English'),
                    ('pt', const Locale('pt', 'BR'), 'Português Brasileiro'),
                  ])
                    SimpleSegment(
                      key: Key('simple-menu-language-$id'),
                      label: name,
                      selected: ref.watch(appLocaleProvider) == locale,
                      radius: 8,
                      onTap: () =>
                          ref.read(appLocaleProvider.notifier).set(locale),
                    ),
                ],
              ),
            ),
          ],
        ),
        const Divider(height: AraSpace.s16),
        // Every version on the rig, for a bug report or a support question;
        // a part the rig can't read is left out.
        SimpleVersions(
          key: const Key('simple-versions'),
          rows: [
            for (final (name, v) in <(String, String?)>[
              (l10n.shellThisApp, version),
              (l10n.shellAraServer, rig?.daemonVersion),
              if (rig?.guiderVersion != null)
                (l10n.deviceGuider, rig!.guiderVersion),
              for (final a in servers)
                (
                  servers.length > 1
                      ? '${a.label} (${a.host}:${a.port})'
                      : a.label,
                  a.version,
                ),
              if (rig?.astapVersion != null) ('ASTAP', rig!.astapVersion),
              if (stars != null && stars.isNotEmpty)
                (
                  l10n.shellStarDatabase,
                  stars.map((d) => d.toUpperCase()).join(', '),
                ),
              if (rig != null && rig.osRelease.isNotEmpty)
                (
                  l10n.shellSystem,
                  [
                    rig.osRelease,
                    if (rig.boardModel != null) rig.boardModel!,
                  ].join(' · '),
                ),
            ])
              (name, v ?? '—'),
          ],
        ),
        const Divider(height: AraSpace.s16),
        SimpleStorageBlock(reveal: revealStorage),
        const Divider(height: AraSpace.s16),
        const SimpleLogsBlock(),
      ],
    );
  }
}

/// Settings over Simple Mode, closed with Back. Turning Simple Mode off in
/// it closes it onto the full UI.
class _SettingsPage extends ConsumerWidget {
  const _SettingsPage();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    ref.listen(simpleModeActiveProvider, (_, on) {
      if (!on) Navigator.of(context).maybePop();
    });
    return Scaffold(
      appBar: AppBar(title: Text(context.l10n.shellSettings)),
      body: const SettingsShell(),
    );
  }
}

/// Reads the profile's guide optics when Rig opens, and again each time the
/// guider connects (the rig fills unset ones then); draws [child].
class _GuideOpticsReader extends ConsumerStatefulWidget {
  const _GuideOpticsReader({required this.child});

  final Widget child;

  @override
  ConsumerState<_GuideOpticsReader> createState() => _GuideOpticsReaderState();
}

class _GuideOpticsReaderState extends ConsumerState<_GuideOpticsReader> {
  @override
  void initState() {
    super.initState();
    ref.listenManual(
      guiderStatusProvider.select((s) => s.value?.isConnected ?? false),
      (was, now) {
        // Open (was null), or the guider just connected.
        if (was == null || (now && !was)) {
          unawaited(_ThisRig._readGuideOptics(context, ref, retry: now));
        }
      },
      fireImmediately: true,
    );
  }

  @override
  Widget build(BuildContext context) => widget.child;
}

/// A value the rig keeps, set by wheels: tap it and one wheel per digit
/// (and a hemisphere for a coordinate) opens under it; a wheel at rest sets
/// it. A second tap closes them.
class _RigWheelRow extends StatefulWidget {
  const _RigWheelRow._({
    required this.id,
    required this.label,
    required this.text,
    required this.columns,
    required this.initial,
    required this.unit,
    required this.read,
    required this.onSet,
    this.dotAfter,
  });

  /// Whole millimetres: [digits] wheels, or with none one wheel over
  /// [_kApertureMm] (an unset value starts at 80).
  factory _RigWheelRow.millimetres(
    AppLocalizations l10n, {
    required String id,
    required String label,
    required int? digits,
    required double value,
    required ValueChanged<double> onSet,
  }) {
    final text = value > 0 ? '${value.round()} mm' : l10n.shellNotSet;
    if (digits == null) {
      final (lo, hi) = _kApertureMm;
      final v = value > 0 ? value.round().clamp(lo, hi) : 80;
      return _RigWheelRow._(
        id: id,
        label: label,
        text: text,
        columns: [
          [for (var mm = lo; mm <= hi; mm++) '$mm'],
        ],
        initial: [v - lo],
        unit: 'mm',
        read: (w) => (lo + w.single).toDouble(),
        onSet: onSet,
      );
    }
    return _RigWheelRow._(
      id: id,
      label: label,
      text: text,
      columns: List.filled(digits, _kDigits),
      initial: _digitsOf(value.round(), digits),
      unit: 'mm',
      read: (w) => int.parse(w.join()).toDouble(),
      onSet: onSet,
    );
  }

  /// A small refractor to a big Dobsonian.
  static const _kApertureMm = (10, 600);

  /// Latitude or longitude: hemisphere, degrees, four decimals (about 11 m).
  factory _RigWheelRow.coordinate(
    AppLocalizations l10n, {
    required String id,
    required String label,
    required double value,
    required ValueChanged<double> onSet,
  }) {
    final lat = id == 'lat';
    final hemi = lat
        ? [l10n.shellHemiNorth, l10n.shellHemiSouth]
        : [l10n.shellHemiEast, l10n.shellHemiWest];
    final degDigits = lat ? 2 : 3;
    final deg = value.abs().floor();
    final frac = ((value.abs() - deg) * 10000).round().clamp(0, 9999);
    return _RigWheelRow._(
      id: id,
      label: label,
      text:
          '${value.abs().toStringAsFixed(4)}° ${value < 0 ? hemi[1] : hemi[0]}',
      columns: [hemi, ...List.filled(degDigits + 4, _kDigits)],
      initial: [
        value < 0 ? 1 : 0,
        ..._digitsOf(deg, degDigits),
        ..._digitsOf(frac, 4),
      ],
      unit: '°',
      dotAfter: degDigits,
      read: (w) {
        final v = math.min(
          int.parse(w.sublist(1, 1 + degDigits).join()) +
              int.parse(w.sublist(1 + degDigits).join()) / 10000,
          lat ? 90.0 : 180.0,
        );
        return w[0] == 1 ? -v : v;
      },
      onSet: onSet,
    );
  }

  static const _kDigits = ['0', '1', '2', '3', '4', '5', '6', '7', '8', '9'];

  static List<int> _digitsOf(int v, int n) => [
    for (final c
        in v
            .clamp(0, math.pow(10, n).toInt() - 1)
            .toString()
            .padLeft(n, '0')
            .split(''))
      int.parse(c),
  ];

  final String id;
  final String label;
  final String text;
  final List<List<String>> columns;
  final List<int> initial;
  final String unit;

  /// The wheels' column that a '.' follows.
  final int? dotAfter;
  final double Function(List<int> wheels) read;
  final ValueChanged<double> onSet;

  @override
  State<_RigWheelRow> createState() => _RigWheelRowState();
}

class _RigWheelRowState extends State<_RigWheelRow> {
  bool _open = false;
  List<FixedExtentScrollController> _wheels = const [];

  void _toggle() => setState(() {
    _open = !_open;
    for (final w in _wheels) {
      w.dispose();
    }
    _wheels = _open
        ? [
            for (final i in widget.initial)
              FixedExtentScrollController(initialItem: i),
          ]
        : const [];
  });

  @override
  void dispose() {
    for (final w in _wheels) {
      w.dispose();
    }
    super.dispose();
  }

  // iOS's own picker: the value under the band magnified, the wheels of a
  // group sharing one band like the Clock app's timer. [k] is the user's
  // text size, so a digit keeps its slot (WCAG 1.4.4).
  Widget _wheel(
    int i,
    double k, {
    required bool first,
    required bool last,
  }) => SizedBox(
    // A digit's wheel, or wide enough for "600".
    width: (widget.columns[i].length > 10 ? 72 : 36) * k,
    height: 160 * k,
    child: Semantics(
      label: context.l10n.shellWheelSemantics(
        widget.label,
        i + 1,
        widget.columns.length,
      ),
      child: SimpleWheelKeys(
        debugLabel: 'simple-rig-${widget.id}-wheel-$i',
        onStep: SimpleWheelKeys.stepper(_wheels[i], widget.columns[i].length),
        child: CupertinoPicker(
          key: Key('simple-rig-${widget.id}-wheel-$i'),
          scrollController: _wheels[i],
          itemExtent: 36 * k,
          useMagnifier: true,
          magnification: 1.2,
          selectionOverlay: CupertinoPickerDefaultSelectionOverlay(
            capStartEdge: first,
            capEndEdge: last,
          ),
          onSelectedItemChanged: (_) {},
          children: [
            for (final o in widget.columns[i])
              Center(child: Text(o, style: kSimpleText.copyWith(fontSize: 20))),
          ],
        ),
      ),
    ),
  );

  /// Wheels [from] up to [to] on one band, then [tail].
  Widget _group(int from, int to, double k, List<Widget> tail) => Row(
    mainAxisSize: MainAxisSize.min,
    children: [
      for (var i = from; i < to; i++)
        _wheel(i, k, first: i == from, last: i == to - 1),
      ...tail,
    ],
  );

  @override
  Widget build(BuildContext context) {
    final k = MediaQuery.textScalerOf(context).scale(20) / 20;
    final n = widget.columns.length;
    final dot = widget.dotAfter;
    final unit = Text(widget.unit, style: AraText.caption);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        ConstrainedBox(
          constraints: const BoxConstraints(minHeight: 44),
          child: Row(
            spacing: AraSpace.s12,
            children: [
              Expanded(child: Text(widget.label, style: kSimpleText)),
              TextButton(
                key: Key('simple-rig-${widget.id}'),
                style: TextButton.styleFrom(
                  backgroundColor: AraColors.tileFill,
                  minimumSize: const Size(kSimpleRigControlWidth, 44),
                  side: _open
                      ? const BorderSide(color: AraColors.selectionBg, width: 2)
                      : null,
                ),
                onPressed: _toggle,
                child: Text(widget.text, style: kSimpleText),
              ),
            ],
          ),
        ),
        if (_open)
          // Set when a wheel comes to rest, not on every digit it passes.
          NotificationListener<ScrollEndNotification>(
            onNotification: (_) {
              final v = widget.read([for (final w in _wheels) w.selectedItem]);
              widget.onSet(v);
              return false;
            },
            child: Padding(
              padding: const EdgeInsets.symmetric(vertical: AraSpace.s4),
              // A coordinate's decimals drop under its degrees when one line
              // won't fit (a narrow window, large text).
              child: Wrap(
                alignment: WrapAlignment.end,
                crossAxisAlignment: WrapCrossAlignment.center,
                runSpacing: AraSpace.s4,
                children: dot == null
                    ? [
                        _group(0, n, k, [unit]),
                      ]
                    : [
                        _group(0, dot + 1, k, [
                          Text('.', style: kSimpleText.copyWith(fontSize: 20)),
                        ]),
                        _group(dot + 1, n, k, [unit]),
                      ],
              ),
            ),
          ),
      ],
    );
  }
}

/// A whole number stepped with − and +: a tap is one, a hold runs (1, then
/// 10, then 50 at a time) and sets it on release.
class _RigStepper extends StatefulWidget {
  const _RigStepper({
    required this.id,
    required this.label,
    required this.unit,
    required this.value,
    required this.onSet,
  });

  final String id;
  final String label;
  final String unit;
  final double value;
  final ValueChanged<double> onSet;

  @override
  State<_RigStepper> createState() => _RigStepperState();
}

class _RigStepperState extends State<_RigStepper> {
  double? _held;
  Timer? _run;
  int _steps = 0;

  void _bump(int dir) => setState(() {
    final by = _steps > 25 ? 50 : (_steps > 10 ? 10 : 1);
    _held = (_held ?? widget.value) + dir * by;
    _steps++;
  });

  void _start(int dir) {
    _run?.cancel();
    _steps = 0;
    _bump(dir);
    _run = Timer.periodic(const Duration(milliseconds: 100), (_) => _bump(dir));
  }

  void _end() {
    _run?.cancel();
    final v = _held;
    setState(() => _held = null);
    if (v != null) widget.onSet(v);
  }

  /// The system took the finger mid-hold: stop and set nothing.
  void _cancel() {
    _run?.cancel();
    _run = null;
    if (_held != null) setState(() => _held = null);
  }

  @override
  void dispose() {
    _run?.cancel();
    super.dispose();
  }

  Widget _button(int dir) => GestureDetector(
    onLongPressStart: (_) => _start(dir),
    onLongPressEnd: (_) => _end(),
    onLongPressCancel: _cancel,
    // No tooltip: its own long press would take the hold.
    child: IconButton(
      key: Key('simple-rig-${widget.id}-${dir > 0 ? 'up' : 'down'}'),
      onPressed: () => widget.onSet(widget.value + dir),
      icon: Icon(
        dir > 0 ? Icons.add : Icons.remove,
        semanticLabel: dir > 0
            ? context.l10n.shellRaise
            : context.l10n.shellLower,
      ),
    ),
  );

  @override
  Widget build(BuildContext context) => ConstrainedBox(
    constraints: const BoxConstraints(minHeight: 44),
    child: Row(
      spacing: AraSpace.s12,
      children: [
        Expanded(child: Text(widget.label, style: kSimpleText)),
        // The wheels' width, so every Rig value lines up (user, 2026-11-04).
        Container(
          key: Key('simple-rig-${widget.id}'),
          width: kSimpleRigControlWidth,
          decoration: BoxDecoration(
            color: AraColors.tileFill,
            borderRadius: BorderRadius.circular(8),
          ),
          child: Row(
            children: [
              _button(-1),
              Expanded(
                child: Text(
                  '${(_held ?? widget.value).round()} ${widget.unit}',
                  style: kSimpleText,
                  textAlign: TextAlign.center,
                  maxLines: 1,
                  softWrap: false,
                  overflow: TextOverflow.visible,
                ),
              ),
              _button(1),
            ],
          ),
        ),
      ],
    ),
  );
}

/// Where a device window and the Ara sheet start: one inset right of the left
/// rail, or at the left inset when the window can't fit beside it.
double _topLeftOverlayLeft(BuildContext context) {
  final scale = simpleRailScale(context);
  final rail = kSimpleRailWidth * scale;
  final fits =
      MediaQuery.sizeOf(context).width >=
      kSimpleDeviceWindowWidth * scale + rail + kSimpleInset * 3;
  return fits ? kSimpleInset * 2 + rail : kSimpleInset;
}

/// Back to the launch screens for another rig or profile: Settings › Rig and
/// the Ara sheet do the same thing.
void _changeRigOrProfile(
  WidgetRef ref,
  VoidCallback onClose, {
  required bool rig,
}) {
  onClose();
  ref.read(offlineModeProvider.notifier).exit();
  ref.read(profileGatePassedProvider.notifier).reset();
  // Changing rig starts at the rig list; changing profile stays on this
  // rig and opens its profile box.
  if (rig) ref.read(serverChooserRequestedProvider.notifier).request();
}

/// A fact with its change button on the same line: Rig and Profile.
class _ChangeRow extends StatelessWidget {
  const _ChangeRow({
    required this.buttonKey,
    required this.label,
    required this.value,
    required this.change,
    required this.onTap,
    this.wrapValue = false,
    this.linePlace = CrossAxisAlignment.center,
  });

  final Key buttonKey;
  final String label;
  final String value;

  /// What a screen reader hears for the button.
  final String change;
  final VoidCallback onTap;

  /// The Ara sheet wraps a long name; Settings cuts it to one line.
  final bool wrapValue;

  /// Where the line sits in the 44 pt target. The Ara sheet puts two rows'
  /// lines together and spends each target's height away from the other.
  final CrossAxisAlignment linePlace;

  @override
  Widget build(BuildContext context) => Row(
    spacing: AraSpace.s12,
    crossAxisAlignment: linePlace,
    children: [
      Text(label, style: kSimpleText),
      Expanded(
        child: Text(
          value,
          style: kSimpleText,
          textAlign: TextAlign.right,
          overflow: wrapValue ? null : TextOverflow.ellipsis,
        ),
      ),
      // A plain blue "Change"; a screen reader hears what it changes.
      TextButton(
        key: buttonKey,
        // A foreground builder turns clipping on, which cuts the tint's corners.
        clipBehavior: Clip.none,
        style: _linkStyle(
          alignment: switch (linePlace) {
            CrossAxisAlignment.end => Alignment.bottomCenter,
            CrossAxisAlignment.start => Alignment.topCenter,
            _ => Alignment.center,
          },
        ),
        onPressed: onTap,
        child: Text(
          context.l10n.shellChange,
          semanticsLabel: change,
          style: const TextStyle(fontSize: 14),
        ),
      ),
    ],
  );
}

/// A blue text link with Simple Mode's 44 pt target (user, 2026-11-03). Its
/// hover, focus and press tint fits the word: a target that reaches away from
/// its line must not light up the empty space.
ButtonStyle _linkStyle({Alignment alignment = Alignment.center}) {
  final tint = AraColors.linkText.withValues(alpha: 0.16);
  return TextButton.styleFrom(
    minimumSize: const Size(0, 44),
    padding: EdgeInsets.zero,
    tapTargetSize: MaterialTapTargetSize.shrinkWrap,
    alignment: alignment,
    overlayColor: Colors.transparent,
    foregroundBuilder: (context, states, child) {
      final lit =
          states.contains(WidgetState.hovered) ||
          states.contains(WidgetState.focused) ||
          states.contains(WidgetState.pressed);
      return Stack(
        clipBehavior: Clip.none,
        children: [
          // Past the word's line height, without moving the word off its row.
          Positioned(
            left: 0,
            right: 0,
            top: -2,
            bottom: -2,
            child: DecoratedBox(
              key: const Key('simple-link-highlight'),
              decoration: BoxDecoration(
                color: lit ? tint : null,
                borderRadius: BorderRadius.circular(6),
              ),
            ),
          ),
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: AraSpace.s8),
            child: child,
          ),
        ],
      );
    },
  );
}

/// The OpenAstro Ara sheet under the mark (#1602): the versions a support
/// question starts with, then which rig and profile this is. Settings stays
/// on the gear.
class _AraSheet extends ConsumerWidget {
  const _AraSheet({required this.onClose});

  final VoidCallback onClose;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = context.l10n;
    final rig = ref.watch(daemonVersionsProvider).value;
    final server = ref.watch(activeServerProvider);
    final profiles = ref.watch(profileManagementProvider).value;
    final profile = profiles?.profiles
        .where((p) => p.id == profiles.activeId)
        .firstOrNull
        ?.name;
    // As wide as its text, up to 420 px; past that the table shrinks the
    // value column to its longest word and the text wraps.
    final maxWidth = math.min(
      420.0,
      MediaQuery.sizeOf(context).width -
          _topLeftOverlayLeft(context) -
          kSimpleInset,
    );
    final running = ref.watch(
      sequenceRunStateProvider.select((s) => s.value?.state?.isActive ?? false),
    );
    final version = ref.watch(appDisplayVersionProvider).value;
    return ConstrainedBox(
      key: const Key('simple-ara-sheet'),
      // minWidth stays <= maxWidth in a window narrower than 312 px.
      constraints: BoxConstraints(
        minWidth: math.min(280.0, maxWidth),
        maxWidth: maxWidth,
      ),
      child: IntrinsicWidth(
        child: SimplePanel(
        // The top and bottom 44 pt targets fill most of the usual inset.
        padding: const EdgeInsets.fromLTRB(
          AraSpace.s16,
          AraSpace.s4,
          AraSpace.s16,
          0,
        ),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            SimpleVersions(
              title: kAppName,
              titleAction: TextButton(
                key: const Key('simple-ara-licenses'),
                // A foreground builder turns clipping on, which cuts the tint's corners.
                clipBehavior: Clip.none,
                style: _linkStyle(),
                onPressed: () {
                  showAppLicenses(context, version);
                  onClose();
                },
                child: Text(
                  l10n.shellLicenses,
                  semanticsLabel: l10n.shellOpenSourceLicenses,
                  style: const TextStyle(fontSize: 14),
                ),
              ),
              rows: [
                (l10n.shellThisApp, version ?? '—'),
                (l10n.shellAraServer, rig?.daemonVersion ?? '—'),
                if (rig?.guiderVersion != null)
                  (l10n.deviceGuider, rig!.guiderVersion!),
              ],
            ),
            // Half of the version table's 4 px line gap on each side.
            Padding(
              padding: const EdgeInsets.only(bottom: AraSpace.s4 / 2),
              child: _ChangeRow(
                buttonKey: const Key('simple-ara-change-rig'),
                label: l10n.shellSectionRig,
                value: server == null
                    ? '—'
                    : server.mdnsName ?? server.hostname,
                change: l10n.shellChangeRig,
                wrapValue: true,
                linePlace: CrossAxisAlignment.end,
                onTap: () => _changeRigOrProfile(ref, onClose, rig: true),
              ),
            ),
            Padding(
              padding: const EdgeInsets.only(top: AraSpace.s4 / 2),
              child: _ChangeRow(
                buttonKey: const Key('simple-ara-change-profile'),
                label: l10n.shellProfile,
                value: profile ?? '—',
                change: l10n.shellChangeProfile,
                wrapValue: true,
                linePlace: CrossAxisAlignment.start,
                onTap: () => _changeRigOrProfile(ref, onClose, rig: false),
              ),
            ),
            if (running)
              Padding(
                padding: const EdgeInsets.only(bottom: AraSpace.s16),
                child: Text(
                  l10n.shellPlanRunning,
                  style: AraText.caption.copyWith(color: AraColors.accentBusy),
                ),
              ),
          ],
        ),
      ),
      ),
    );
  }
}
