import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lat_lng_to_timezone/lat_lng_to_timezone.dart' as tz_map;

import '../l10n/l10n.dart';
import '../services/profile_api.dart';
import '../state/profile_management_state.dart' show profileApiProvider;
import '../services/ws_event_stream.dart';
import '../state/settings/site_settings_state.dart';
import '../state/simple_messages_state.dart';
import '../state/simple_mode_state.dart';
import '../state/time_sync_state.dart';
import '../state/ws/ws_providers.dart';
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
/// and the existing time push; no new server call. Asked on a transition into
/// connected, once per session unless answered Not now.
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
    ref.listen(wsConnectionStateProvider, (previous, next) {
      final now = next.asData?.value;
      final was = previous?.asData?.value;
      if (now == WsConnectionState.connected &&
          was != WsConnectionState.connected) {
        unawaited(_maybeOffer());
      }
    });
    return widget.child;
  }

  Future<void> _maybeOffer() async {
    if (_pillUp ||
        !ref.read(companionModeProvider) ||
        ref.read(companionTimeOfferDeclinedProvider)) {
      return;
    }
    final api = ref.read(timeSyncApiProvider);
    if (api == null) return;
    try {
      if ((await api.getState()).synced) return;
    } catch (_) {
      return; // can't ask the rig: no offer now, the next connect asks again
    }
    // Re-checked after the await: the mode or the answer may have changed.
    if (!mounted ||
        _pillUp ||
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
    final site = ref.read(siteSettingsProvider.notifier);
    site.setLatitudeDeg(r.lat);
    site.setLongitudeDeg(r.lng);
    if (r.alt != null) site.setElevationM(r.alt!);
    site.setTimeZone(tz_map.latLngToTimezoneString(r.lat, r.lng));
    final ProfileApi? api = ref.read(profileApiProvider);
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
    notify(l10n.companionTimeOfferSent);
  }
}
