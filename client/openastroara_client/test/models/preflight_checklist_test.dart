import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/preflight_checklist.dart';

void main() {
  final wire = <String, dynamic>{
    'generated_utc': '2026-07-18T04:00:00Z',
    'items': [
      {
        'key': 'equipment',
        'status': 'ok',
        'title': 'Equipment',
        'detail': 'All 3 devices connected and healthy.',
      },
      {
        'key': 'storage',
        'status': 'warn',
        'title': 'Storage',
        'detail': '9 GB free: below the 10 GB warning threshold.',
      },
      {
        'key': 'cooler',
        'status': 'unknown',
        'title': 'Cooler',
        'detail': 'Camera not connected.',
      },
      {'bogus': true},
    ],
    'flips': [
      {
        'target': 'M81',
        'at_utc': '2026-07-18T08:14:00Z',
        'altitude_deg': 67.2,
        'standing': 'ok',
      },
      {
        'target': 'NGC 6188',
        'at_utc': '2026-07-18T10:33:00Z',
        'altitude_deg': 12.0,
        'standing': 'soft_warning',
      },
    ],
    'unforecast_targets': 2,
    'unattended_window_start': '23:00',
    'unattended_window_end': '06:30',
    'unattended_window_source': 'clock',
    'alarm_device': 'macbook-bedroom.local',
    'alarm_sound_enabled': true,
    'all_good': true,
  };

  test(
    '#1138 §58.14 parses the daemon wire shape and skips a malformed item',
    () {
      final c = PreflightChecklist.fromJson(wire)!;
      expect(c.generatedUtc, DateTime.utc(2026, 7, 18, 4));
      expect(c.items.map((i) => i.key), ['equipment', 'storage', 'cooler']);
      expect(
        c.flips.singleWhere((f) => f.target == 'NGC 6188').standing,
        'soft_warning',
      );
      expect(c.unforecastTargets, 2);
      expect(c.allGood, isTrue);
      expect(c.hasFailure, isFalse);
      expect(c.hasWarning, isTrue, reason: 'warn and unknown items both count');
      expect(c.unattendedLine, 'Unattended hours: 23:00 — 06:30');
      expect(
        c.alarmLine,
        'Alarm device: macbook-bedroom.local (alarm sound on)',
      );
    },
  );

  test('#1138 a flip below the floor is a failure; sun-driven window and a silent alarm read honestly', () {
    final c = PreflightChecklist.fromJson({
      ...wire,
      'flips': [
        {
          'target': 'x',
          'at_utc': '2026-07-18T08:14:00Z',
          'altitude_deg': 5.0,
          'standing': 'below_floor',
        },
      ],
      'unattended_window_source': 'sun',
      'unattended_window_start': null,
      'unattended_window_end': null,
      'alarm_sound_enabled': false,
      'all_good': false,
    })!;
    expect(c.hasFailure, isTrue);
    expect(c.unattendedLine, contains('astronomical darkness'));
    expect(
      c.alarmLine,
      'Alarm device: macbook-bedroom.local — alarm sound is OFF',
    );
    expect(
      PreflightChecklist.fromJson({...wire, 'alarm_device': null})!.alarmLine,
      'Alarm device: no client connected',
    );
  });

  test('#1138 fromJson rejects a body without the required shape', () {
    expect(PreflightChecklist.fromJson(null), isNull);
    expect(PreflightChecklist.fromJson({'items': []}), isNull);
    expect(
      PreflightChecklist.fromJson({'generated_utc': 'nope', 'items': []}),
      isNull,
    );
  });

  test(
    '#1138 the night key belongs to the EVENING: 03:00 is still last night',
    () {
      expect(preflightNightKey(DateTime(2026, 7, 18, 23, 0)), '2026-07-18');
      expect(preflightNightKey(DateTime(2026, 7, 19, 3, 0)), '2026-07-18');
      expect(preflightNightKey(DateTime(2026, 7, 19, 12, 0)), '2026-07-19');
    // Spring-forward morning (US, 2026-03-08): 00:30 is still the 7th, not the 6th.
    expect(preflightNightKey(DateTime(2026, 3, 8, 0, 30)), '2026-03-07');
    },
  );

  test(
    '#1138 the card fires once per night, only after dark with a run going',
    () {
      bool go({
        bool run = true,
        bool dark = true,
        Set<String> acked = const {},
      }) => shouldPromptPreflight(
        sequenceRunning: run,
        dark: dark,
        nightKey: '2026-07-18',
        acknowledgedNights: acked,
      );
      expect(go(), isTrue);
      expect(go(run: false), isFalse, reason: 'nothing to sleep through');
      expect(go(dark: false), isFalse, reason: 'daylight');
      expect(
        go(acked: {'2026-07-18'}),
        isFalse,
        reason: 'already answered tonight',
      );
      expect(
        go(acked: {'2026-07-17'}),
        isTrue,
        reason: 'last night does not count',
      );
    },
  );
}
