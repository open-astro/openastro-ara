import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lat_lng_to_timezone/lat_lng_to_timezone.dart' as tz_map;

import '../l10n/l10n.dart';
import '../state/profile_management_state.dart' show profileApiProvider;
import '../state/settings/loaded_section.dart';
import '../state/settings/site_settings_state.dart';
import '../state/simple_messages_state.dart';
import '../state/simple_mode_state.dart';
import '../state/time_sync_state.dart';
import '../util/friendly_error.dart';
import '../util/gps_site_fill.dart';

/// §41.1 — Not now holds the offer until the next session: lives as long as
/// the app process, never written to disk.
final companionTimeOfferDeclinedProvider =
    NotifierProvider<_DeclinedNotifier, bool>(_DeclinedNotifier.new);

class _DeclinedNotifier extends Notifier<bool> {
  @override
  bool build() => false;
  void decline() => state = true;
}

/// §41.1 / #1780 — on a phone (Companion Mode), when the rig reports no recent
/// time sync, a question pill (Not now, Send) offers this phone's location and
/// time. Send uses the same lookup as Simple Mode's site fill (fillSiteFromGps)
/// and the existing time push; no new server call. Asked after each
/// connect (the on-connect check says the rig was not synced), once per
/// session unless answered Not now.
class CompanionTimeOfferListener extends ConsumerStatefulWidget {
  const CompanionTimeOfferListener({super.key, required this.child});

  final Widget child;

  @override
  ConsumerState<CompanionTimeOfferListener> createState() =>
      _CompanionTimeOfferListenerState();
}

class _CompanionTimeOfferListenerState
    extends ConsumerState<CompanionTimeOfferListener> {
  /// The pill is up and not answered yet: one at a time.
  bool _pillUp = false;

  @override
  Widget build(BuildContext context) {
    // The on-connect check's reading, taken before its own clock push.
    ref.listen(timeSyncCheckProvider, (previous, next) {
      if (next != null && !next.synced) _maybeOffer();
    });
    return widget.child;
  }

  void _maybeOffer() {
    if (_pillUp ||
        !ref.read(companionModeProvider) ||
        ref.read(companionTimeOfferDeclinedProvider)) {
      return;
    }
    _pill();
  }

  void _pill() {
    final l10n = context.l10n;
    _pillUp = true;
    // The pill can outlive this widget: answers go through the container.
    final container = ProviderScope.containerOf(context, listen: false);
    final messages = container.read(simpleMessagesProvider.notifier);
    int? id;
    id = messages.show(
      SimpleMessageKind.question,
      l10n.companionTimeOfferQuestion,
      actions: [
        SimpleMessageAction(l10n.companionTimeOfferNotNow, () {
          _pillUp = false;
          container.read(companionTimeOfferDeclinedProvider.notifier).decline();
          messages.dismiss(id!);
        }),
        SimpleMessageAction(l10n.companionTimeOfferSend, () {
          _pillUp = false;
          messages.dismiss(id!);
          unawaited(_send());
        }),
      ],
    );
  }

  /// Same steps as Simple Mode's "use this device's location": the clock, then
  /// the site from fillSiteFromGps saved to the rig. Every outcome is a pill.
  Future<void> _send() async {
    final notify = userNotifier(context);
    final l10n = context.l10n;
    final clock = ref.read(timeSyncApiProvider);
    try {
      await clock?.pushClientTime(DateTime.now().toUtc());
    } catch (e) {
      notify(
        l10n.shellCouldntSetClock,
        detail: friendlyError(l10n, e),
        error: true,
      );
    }
    if (!mounted) return;
    final r = await fillSiteFromGps(ref);
    if (!mounted) return;
    if (!r.success) {
      notify(l10n.companionTimeOfferNoGps, detail: r.message, error: true);
      return;
    }
    if (ref.read(profileApiProvider) == null) {
      notify(
        l10n.shellCouldntSaveSite,
        detail: l10n.shellRigNotConnected,
        error: true,
      );
      return;
    }
    // The fix goes onto the rig's copy of the site (loaded once more first
    // when it isn't in) and is saved whole; not loaded, refused or stale:
    // nothing is sent and it says why (ADR 0015).
    try {
      await ref
          .read(siteSettingsProvider.notifier)
          .changeAndSave(
            (c) => SiteSettingsNotifier.withFix(
              c,
              lat: r.lat,
              lng: r.lng,
              alt: r.alt,
              timeZone: tz_map.latLngToTimezoneString(r.lat, r.lng),
            ),
          );
    } catch (e) {
      notify(
        l10n.shellCouldntSaveSite,
        detail: e is SectionNotLoadedException || e is SectionConflictException
            ? e.toString()
            : friendlyError(l10n, e),
        error: true,
      );
      return;
    }
    notify(l10n.companionTimeOfferSent);
  }
}
