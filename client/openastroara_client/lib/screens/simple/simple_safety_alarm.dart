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
class SimpleSafetyAlarm extends ConsumerWidget {
  const SimpleSafetyAlarm({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    // An emergency stop pressed on this device: no screen and no tone.
    bool own(SafetyAlarmState a) =>
        a.reason == kEmergencyStopReason &&
        ref.read(localEmergencyStopProvider.notifier).recent;
    ref.listen(safetyAlarmProvider, (_, next) {
      if ((next.pending || next.ringing) && own(next)) {
        ref.read(safetyAlarmProvider.notifier).silence();
      }
    });
    final alarm = ref.watch(safetyAlarmProvider);
    if (!alarm.pending && !alarm.ringing || own(alarm)) {
      return const SizedBox.shrink();
    }
    return Positioned.fill(
      key: const Key('simple-safety-alarm'),
      child: Semantics(
        liveRegion: true,
        label: 'Safety alert',
        child: Material(
          color: const Color(0xE03C0808),
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
                        color: Colors.white,
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
                      style: FilledButton.styleFrom(
                        backgroundColor: AraColors.accentError,
                        foregroundColor: Colors.white,
                        minimumSize: const Size(220, 56),
                        textStyle: const TextStyle(
                          fontSize: 16,
                          fontWeight: FontWeight.w600,
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
    );
  }
}
