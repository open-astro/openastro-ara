import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/alarm/safety_alarm_state.dart';
import '../../theme/ara_colors.dart';
import '../../widgets/emergency_stop_button.dart'
    show localEmergencyStopProvider;
import '../../theme/ara_metrics.dart';
import 'simple_preview_panel.dart' show kSimpleText;

/// Simple Mode's safety alarm (unsafe conditions or an emergency stop): the
/// one popup it keeps, as a full-screen warning over everything, immersive
/// view included. It shows at once; the tone follows after the device's
/// silent delay. Silence stops the sound and closes it (the full UI keeps its
/// dialog, SafetyAlarmListener).
class SimpleSafetyAlarm extends ConsumerStatefulWidget {
  const SimpleSafetyAlarm({super.key});

  @override
  ConsumerState<SimpleSafetyAlarm> createState() => _SimpleSafetyAlarmState();
}

class _SimpleSafetyAlarmState extends ConsumerState<SimpleSafetyAlarm> {
  // The shell already holds the focus, so autofocus wouldn't move it: the
  // alarm takes it when it shows, and the keyboard lands on Silence.
  final _silence = FocusNode(debugLabel: 'Silence alarm');

  @override
  void dispose() {
    _silence.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    // An emergency stop pressed on this device: no screen and no tone.
    bool own(SafetyAlarmState a) =>
        a.reason == kEmergencyStopReason &&
        ref.read(localEmergencyStopProvider.notifier).recent;
    ref.listen(safetyAlarmProvider, (prev, next) {
      final active = next.pending || next.ringing;
      if (active && own(next)) {
        ref.read(safetyAlarmProvider.notifier).silence();
      } else if (active &&
          !(prev?.pending ?? false) &&
          !(prev?.ringing ?? false)) {
        WidgetsBinding.instance.addPostFrameCallback((_) {
          if (mounted) _silence.requestFocus();
        });
      }
    });
    final alarm = ref.watch(safetyAlarmProvider);
    if (!alarm.pending && !alarm.ringing || own(alarm)) {
      return const SizedBox.shrink();
    }
    return Positioned.fill(
      key: const Key('simple-safety-alarm'),
      // Screen readers and the keyboard stay on the alarm: nothing behind it
      // is read or reachable until it's silenced.
      // Its own focus scope: Tab cycles within the alarm instead of reaching
      // the controls under the veil.
      child: FocusScope(
        child: BlockSemantics(
          child: Semantics(
            liveRegion: true,
            label: 'Safety alert',
            child: Material(
              color: AraColors.alarmScrim,
              child: SafeArea(
                child: Center(
                  child: SingleChildScrollView(
                    padding: const EdgeInsets.all(AraSpace.s24),
                    child: Column(
                      mainAxisSize: MainAxisSize.min,
                      spacing: AraSpace.s12,
                      children: [
                        const Icon(
                          Icons.error,
                          size: 72,
                          color: AraColors.accentError,
                        ),
                        Text(
                          'SAFETY ALERT',
                          style: kSimpleText.copyWith(
                            fontSize: 26,
                            fontWeight: FontWeight.w600,
                            letterSpacing: 1,
                            color: AraColors.textPrimary,
                          ),
                        ),
                        Text(
                          alarm.reason.isEmpty
                              ? 'Conditions are UNSAFE.'
                              : alarm.reason,
                          textAlign: TextAlign.center,
                          style: kSimpleText.copyWith(
                            fontSize: 16,
                            color: AraColors.errorText,
                          ),
                        ),
                        Text(
                          // Pending: the silent delay (or the tone is muted).
                          alarm.ringing
                              ? 'Alarm ringing'
                              : 'Silence it before it sounds',
                          style: kSimpleText.copyWith(
                            color: AraColors.textSecondary,
                          ),
                        ),
                        const SizedBox(height: AraSpace.s8),
                        FilledButton(
                          key: const Key('simple-safety-silence'),
                          focusNode: _silence,
                          style: FilledButton.styleFrom(
                            backgroundColor: AraColors.accentError,
                            foregroundColor: AraColors.textPrimary,
                            minimumSize: const Size(220, 56),
                            textStyle: const TextStyle(
                              // Large text (20 px bold): passes 3:1 on Fault Red, where 16 px didn't pass 4.5:1.
                              fontSize: 20,
                              fontWeight: FontWeight.w700,
                            ),
                          ),
                          onPressed: () =>
                              ref.read(safetyAlarmProvider.notifier).silence(),
                          child: const Text('Silence alarm'),
                        ),
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
