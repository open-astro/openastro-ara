import 'package:flutter/material.dart';

import '../widgets/auto_flats_prompt_listener.dart';
import '../widgets/connection_policy_listener.dart';
import '../widgets/manual_filter_swap_listener.dart';
import '../widgets/pending_profile_replay_listener.dart';
import '../widgets/preflight_checklist_listener.dart';
import '../widgets/safety_alarm_listener.dart';
import '../widgets/session_ended_summary_listener.dart';
import '../widgets/stop_mount_overlay.dart';
import '../widgets/time_sync_on_connect.dart';

/// The always-alive listeners every shell must host, whatever is showing —
/// moved out of AppShell unchanged so SimpleShell gets the same modals,
/// alarms and overlays.
class ShellListeners extends StatelessWidget {
  const ShellListeners({super.key, required this.child, this.quiet = false});

  final Widget child;

  /// Simple Mode: the listeners that only inform stay off screen (no popups).
  final bool quiet;

  @override
  Widget build(BuildContext context) {
    // §27 — daemon-initiated connection-policy modals (takeover request /
    // session transferred) listen here, at the always-alive shell level.
    // §48 — the "capture calibration tonight?" prompt nests inside for the
    // same reason: it must fire no matter which tab is showing.
    return PendingProfileReplayListener(
      // §58.14 / #1138 — the bedtime card: the pre-sleep checklist, once per
      // night after dark with a sequence running, whatever tab is showing.
      child: PreflightChecklistListener(
        quiet: quiet,
        // §58.13 / #1138 — the morning card: a run that ended while nobody was
        // watching is summarised once on reconnect, whatever tab is showing.
        child: SessionEndedSummaryListener(
          quiet: quiet,
          // #1298 — the manual filter wheel's "install the X filter" prompt
          // must reach the user on whatever tab is showing.
          child: ManualFilterSwapListener(
            child: ConnectionPolicyListener(
              // §31.1 — push this device's clock on every transition into
              // connected (no-ops when the daemon already holds a fresh,
              // trustworthy sync).
              child: TimeSyncOnConnectListener(
                child: AutoFlatsPromptListener(
                  // §35.5 — the audible safety alarm's modal (safety.unsafe /
                  // safety.emergency_stop) must fire no matter which tab is
                  // showing.
                  // Simple Mode draws its own, full screen (SimpleSafetyAlarm).
                  child: SafetyAlarmListener(
                    quiet: quiet,
                    // §57 — the Stop Mount panic overlay surfaces over every
                    // tab while the daemon reports an autonomous slew.
                    child: StopMountListener(quiet: quiet, child: child),
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
