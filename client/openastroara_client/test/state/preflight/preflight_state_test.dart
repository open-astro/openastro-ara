import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/preflight_checklist.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/preflight_api.dart';
import 'package:openastroara/services/preflight_prefs_service.dart';
import 'package:openastroara/state/preflight/preflight_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/setup/setup_readiness.dart';
import 'package:openastroara/state/sky_atlas/tonight_sky_state.dart';

class _List extends SequenceListNotifier {
  _List(this.items);
  // A getter, not a value: Riverpod keeps the notifier instance across an
  // invalidate, so a test that changes the list mid-flight must be read live.
  final List<SequenceListItem> Function() items;
  @override
  Future<List<SequenceListItem>?> build() async => items();
}

class _Site extends SiteSettingsNotifier {
  _Site(this._site);
  final SiteSettings _site;
  @override
  SiteSettings build() => _site;
}

class _MemPrefs extends PreflightPrefsService {
  Set<String> nights = {};
  @override
  Future<Set<String>> loadAcknowledgedNights() async => nights;
  @override
  Future<void> saveAcknowledgedNights(Set<String> n) async => nights = n;
}

class _Api extends PreflightApi {
  _Api(this._answer) : super(AraServer(hostname: 'h', port: 1));
  final PreflightChecklist? _answer;
  int calls = 0;
  @override
  Future<PreflightChecklist?> fetchToday() async {
    calls++;
    return _answer;
  }

  @override
  void close() {}
}

const belen = SiteSettings(
  siteName: 'Belen',
  latitudeDeg: 34.66,
  longitudeDeg: -106.78,
  timeZone: 'America/Denver',
);
final running = [
  const SequenceListItem(
    id: 's1',
    name: 'M81 night',
    currentRunState: SequenceRunState.running,
  ),
];
final idle = [
  const SequenceListItem(
    id: 's1',
    name: 'M81 night',
    currentRunState: SequenceRunState.completed,
  ),
];

void main() {
  test('#1138 anySequenceLive: running, starting and both pauses count; ended runs do not', () {
    expect(anySequenceLive(running), isTrue);
    expect(
      anySequenceLive([
        const SequenceListItem(
          id: 'a',
          currentRunState: SequenceRunState.pausedAwaitingUser,
        ),
      ]),
      isTrue,
    );
    expect(anySequenceLive(idle), isFalse);
    expect(anySequenceLive(null), isFalse);
  });

  test('#1138 the Setup glyph: problem on a failure, done once answered, pending otherwise', () {
    PreflightChecklist c(bool fail) => PreflightChecklist(
      generatedUtc: DateTime.utc(2026),
      items: [
        PreflightItem(
          key: 'mount',
          status: fail ? 'fail' : 'ok',
          title: 'Mount',
          detail: '',
        ),
      ],
      allGood: !fail,
    );
    expect(preSleepStepState(AsyncData(c(true)), true), SetupStepState.problem);
    expect(preSleepStepState(AsyncData(c(false)), true), SetupStepState.done);
    expect(
      preSleepStepState(AsyncData(c(false)), false),
      SetupStepState.pending,
    );
    expect(
      preSleepStepState(const AsyncData(null), true),
      SetupStepState.pending,
    );
    expect(
      preSleepStepState(const AsyncLoading(), true),
      SetupStepState.pending,
    );
  });

  group('#1138 the bedtime prompt', () {
    late _MemPrefs prefs;
    late StreamController<int> ticks;

    ProviderContainer make({
      required List<SequenceListItem> runs,
      required DateTime now,
      SiteSettings site = belen,
    }) {
      final c = ProviderContainer(
        overrides: [
          sequenceListProvider.overrideWith(() => _List(() => runs)),
          siteSettingsProvider.overrideWith(() => _Site(site)),
          preflightPrefsProvider.overrideWithValue(prefs),
          preflightClockProvider.overrideWithValue(() => now),
          skyClockProvider.overrideWith((ref) => ticks.stream),
        ],
      );
      addTearDown(c.dispose);
      return c;
    }

    setUp(() {
      prefs = _MemPrefs();
      ticks = StreamController<int>.broadcast();
    });
    tearDown(() => ticks.close());

    // 00:30 MDT on 2026-07-19 = 06:30 UTC: well past astronomical dusk at Belen.
    final midnight = DateTime.utc(2026, 7, 19, 6, 30);

    test('fires after dark with a run going, and only once per night', () async {
      final c = make(runs: running, now: midnight);
      final s = await c.read(preflightPromptProvider.future);
      expect(s.shouldPrompt, isTrue);
      expect(s.acknowledgedTonight, isFalse);

      await c.read(preflightPromptProvider.notifier).acknowledgeTonight();
      final after = c.read(preflightPromptProvider).requireValue;
      expect(after.shouldPrompt, isFalse);
      expect(after.acknowledgedTonight, isTrue);
      expect(prefs.nights, {
        '2026-07-18',
      }, reason: 'the EVENING key, persisted');

      // A fresh client (a relaunch at 03:00) reads the same night as answered.
      final later = make(runs: running, now: DateTime.utc(2026, 7, 19, 9));
      final again = await later.read(preflightPromptProvider.future);
      expect(again.shouldPrompt, isFalse);
      expect(again.acknowledgedTonight, isTrue);
    });

    test(
      'does not fire with nothing running, in daylight, or at an unset site',
      () async {
        expect(
          (await make(
            runs: idle,
            now: midnight,
          ).read(preflightPromptProvider.future)).shouldPrompt,
          isFalse,
        );
        expect(
          (await make(
            runs: running,
            now: DateTime.utc(2026, 7, 18, 20),
          ).read(preflightPromptProvider.future)).shouldPrompt,
          isFalse,
          reason: '14:00 MDT',
        );
        expect(
          (await make(
            runs: running,
            now: midnight,
            site: const SiteSettings(),
          ).read(preflightPromptProvider.future)).shouldPrompt,
          isFalse,
          reason: 'a (0, 0) site is never dark',
        );
      },
    );

    test(
      'the sky tick re-evaluates, so a run started after dusk gets its card',
      () async {
        var runs = idle;
        final c = ProviderContainer(
          overrides: [
            sequenceListProvider.overrideWith(() => _List(() => runs)),
            siteSettingsProvider.overrideWith(() => _Site(belen)),
            preflightPrefsProvider.overrideWithValue(prefs),
            preflightClockProvider.overrideWithValue(() => midnight),
            skyClockProvider.overrideWith((ref) => ticks.stream),
          ],
        );
        addTearDown(c.dispose);
        c.listen(preflightPromptProvider, (_, _) {});
        expect(
          (await c.read(preflightPromptProvider.future)).shouldPrompt,
          isFalse,
        );
        runs = running;
        c.invalidate(sequenceListProvider);
        ticks.add(1);
        await pumpEventQueue();
        expect(
          (await c.read(preflightPromptProvider.future)).shouldPrompt,
          isTrue,
        );
      },
    );
  });

  test('#1138 the checklist provider reads the active server once and refresh re-reads', () async {
    final answer = PreflightChecklist(
      generatedUtc: DateTime.utc(2026),
      items: const [],
      allGood: true,
    );
    final api = _Api(answer);
    final c = ProviderContainer(
      overrides: [
        activeServerProvider.overrideWithValue(
          AraServer(hostname: 'h', port: 1),
        ),
        preflightApiFactoryProvider.overrideWithValue((_) => api),
      ],
    );
    addTearDown(c.dispose);
    expect(await c.read(preflightChecklistProvider.future), same(answer));
    expect(api.calls, 1);
    expect(
      await c.read(preflightChecklistProvider.notifier).refresh(),
      same(answer),
    );
    expect(api.calls, 2);
  });

  test('#1138 no active server → null without touching the API', () async {
    final api = _Api(null);
    final c = ProviderContainer(
      overrides: [
        activeServerProvider.overrideWithValue(null),
        preflightApiFactoryProvider.overrideWithValue((_) => api),
      ],
    );
    addTearDown(c.dispose);
    expect(await c.read(preflightChecklistProvider.future), isNull);
    expect(api.calls, 0);
  });
}
