import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../models/server.dart';
import '../../services/last_rig_service.dart';
import '../../services/multicast_lock.dart';
import '../../state/launch_gate_state.dart';
import '../../state/saved_server_state.dart';
import '../../state/server_state.dart';
import '../../state/ws/ws_providers.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import 'simple_shell.dart' show SimplePanel;

/// #1559 — a typed rig address: "astro-pi.local" or "192.0.2.24:5555"; the
/// port defaults to 5555. Null when there is nothing to try.
AraServer? parseRigAddress(String text) {
  final t = text.trim();
  if (t.isEmpty) return null;
  final colon = t.lastIndexOf(':');
  if (colon > 0 && !t.substring(0, colon).contains(':')) {
    final port = int.tryParse(t.substring(colon + 1));
    if (port == null || port <= 0 || port > 65535) return null;
    return AraServer(hostname: t.substring(0, colon), port: port);
  }
  return AraServer(hostname: t, port: 5555);
}

/// #1559 — Simple Mode's Connect to your rig card, over the wallpaper while
/// the rig link is down (a dropped link or offline planning). One list
/// (mock `design/preview/simple-no-rig-card-mock.html` B, user 2026-11-11):
/// the session's rig first with Reconnect, then every other rig found on the
/// network or saved, then Add by address. The session's rig comes back in
/// place; another rig goes through its profile box, as Change rig does.
/// A rig row narrower than this at 1x text (the address, then the button)
/// puts its button under the name; it scales with the user's text size.
const double _kRowBesideWidth = 260;

class SimpleRigCard extends ConsumerStatefulWidget {
  const SimpleRigCard({super.key, this.onClose});

  /// Folds the card back into its bar (a narrow window); null hides ×.
  final VoidCallback? onClose;

  @override
  ConsumerState<SimpleRigCard> createState() => _SimpleRigCardState();
}

class _SimpleRigCardState extends ConsumerState<SimpleRigCard> {
  /// Each rig found, with the scan that last saw it: "Found" means seen within
  /// the last two rescans, not once while the card was up.
  final _seen = <AraServer, int>{};
  int _scan = 0;
  final _address = TextEditingController();
  Timer? _rescan;
  bool _addressOpen = false;

  /// The rig being connected, while its handshake runs.
  AraServer? _connecting;

  /// That rig came from the typed address: its Connect says so.
  bool _connectingAddress = false;

  /// The rig that did not answer, for the line under the list.
  AraServer? _failed;

  /// The typed address could not be read (empty, or a bad port).
  bool _badAddress = false;

  /// Opened from the bar, whose Connect is gone: focus lands on ×.
  final _closeFocus = FocusNode();

  @override
  void initState() {
    super.initState();
    if (widget.onClose != null) {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) _closeFocus.requestFocus();
      });
    }
    // As the first-run screen: mDNS is one-shot, so scan again while shown.
    unawaited(MulticastLock.acquire());
    _rescan = Timer.periodic(const Duration(seconds: 4), (_) {
      if (!mounted) return;
      setState(() => _scan++);
      ref.invalidate(discoveredServersProvider);
    });
  }

  @override
  void dispose() {
    unawaited(MulticastLock.release());
    _rescan?.cancel();
    _address.dispose();
    _closeFocus.dispose();
    super.dispose();
  }

  Future<void> _connect(AraServer server, {required bool session}) async {
    setState(() {
      _connecting = server;
      _failed = null;
    });
    final selected = ref.read(selectedServerProvider.notifier)..select(server);
    // The first-run screen's handshake, kept alive for the await.
    final hold = ref.listenManual(serverHandshakeProvider, (_, _) {});
    try {
      final info = await ref.read(serverHandshakeProvider.future);
      if (info != null) {
        unawaited(ref.read(lastRigServiceProvider).save(LastRig(info.name, uuid: info.uuid)));
      }
      final offline = ref.read(offlineModeProvider);
      await ref.read(savedServersProvider.notifier).add(server);
      if (session && !offline) {
        // The same rig: no launch gate, just dial the link again now.
        ref.invalidate(wsEventStreamProvider);
      } else {
        // Another rig (or going online): its profile box, as Change rig.
        ref.read(offlineModeProvider.notifier).exit();
        ref.read(profileGatePassedProvider.notifier).reset();
      }
    } catch (_) {
      if (mounted) setState(() => _failed = server);
    } finally {
      hold.close();
      selected.select(null);
      if (mounted) {
        setState(() {
          _connecting = null;
          _connectingAddress = false;
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    ref.listen(discoveredServersProvider, (_, next) {
      // AsyncData only: a rescan's loading state still carries the last rig, which is no new sighting.
      if (next case AsyncData(:final value, isLoading: false)) setState(() => _seen[value] = _scan);
    });
    final found = {
      for (final e in _seen.entries)
        if (_scan - e.value <= 2) e.key,
    };
    final l10n = context.l10n;
    final offline = ref.watch(offlineModeProvider);
    final saved = ref.watch(savedServersProvider).value ?? const <AraServer>[];
    final session = offline ? null : ref.watch(activeServerProvider);
    final others = [
      ...found.where((s) => s != session),
      ...saved.reversed.where((s) => s != session && !found.contains(s)),
    ];
    String name(AraServer s) => s.mdnsName ?? s.hostname;

    Widget row(AraServer s, {required bool isSession}) {
      final isFound = found.contains(s);
      final busy = _connecting != null;
      return Container(
        key: Key('simple-rig-card-row-${s.hostname}:${s.port}'),
        constraints: const BoxConstraints(minHeight: 52),
        padding: const EdgeInsets.fromLTRB(AraSpace.s12, AraSpace.s4, AraSpace.s4, AraSpace.s4),
        decoration: BoxDecoration(
          color: AraColors.tileFill,
          borderRadius: BorderRadius.circular(AraRadius.simplePanel),
        ),
        // The button goes under the words once beside them they'd have too
        // little room (a phone at large text), so an address never breaks
        // mid-number.
        child: LayoutBuilder(
          builder: (context, box) {
            final words = Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(name(s), style: kSimpleText.copyWith(fontWeight: FontWeight.w500)),
                Text.rich(
                  TextSpan(children: [
                    TextSpan(
                      text: isFound ? l10n.simpleRigFound : l10n.simpleRigSavedNotSeen,
                      style: TextStyle(color: isFound ? AraColors.linkText : AraColors.accentWarning),
                    ),
                    TextSpan(text: ' · ${s.hostname}:${s.port}'),
                  ]),
                  style: kSimpleCaption.copyWith(color: AraColors.textSecondary),
                ),
              ],
            );
            final button = isSession
                ? FilledButton(
                    key: const Key('simple-rig-card-reconnect'),
                    style: FilledButton.styleFrom(minimumSize: const Size(0, 44)),
                    onPressed: busy ? null : () => _connect(s, session: true),
                    child: Text(_connecting == s ? l10n.simpleRigConnecting : l10n.shellReconnect),
                  )
                : OutlinedButton(
                    key: Key('simple-rig-card-connect-${s.hostname}:${s.port}'),
                    style: OutlinedButton.styleFrom(minimumSize: const Size(0, 44)),
                    onPressed: busy ? null : () => _connect(s, session: false),
                    child: Text(_connecting == s ? l10n.simpleRigConnecting : l10n.simpleRigConnect),
                  );
            if (box.maxWidth >= MediaQuery.textScalerOf(context).scale(_kRowBesideWidth)) {
              return Row(spacing: AraSpace.s12, children: [Expanded(child: words), button]);
            }
            return Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              spacing: AraSpace.s4,
              children: [
                Padding(padding: const EdgeInsets.only(right: AraSpace.s8), child: words),
                Align(alignment: Alignment.centerRight, child: button),
              ],
            );
          },
        ),
      );
    }

    // No spinner: the scan never ends while the card shows, and "Looking
    // for rigs…" says it when nothing is found.
    Widget heading(String text) => Semantics(header: true, child: Text(text.toUpperCase(), style: AraText.section));

    final failed = _failed;
    return ConstrainedBox(
      constraints: const BoxConstraints(maxWidth: 440),
      child: SimplePanel(
        key: const Key('simple-rig-card'),
        padding: const EdgeInsets.all(AraSpace.s16),
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            spacing: AraSpace.s12,
            children: [
              Row(
                children: [
                  Expanded(
                    child: Semantics(
                      header: true,
                      child: Text(
                        offline ? l10n.simpleRigGoOnline : l10n.simpleRigConnectTitle,
                        style: kSimpleReading,
                      ),
                    ),
                  ),
                  if (widget.onClose != null)
                    IconButton(
                      key: const Key('simple-rig-card-close'),
                      focusNode: _closeFocus,
                      tooltip: MaterialLocalizations.of(context).closeButtonTooltip,
                      icon: const Icon(Icons.close, size: 18),
                      onPressed: widget.onClose,
                    ),
                ],
              ),
              Text(
                offline || session == null ? l10n.simpleRigOfflineHint : l10n.simpleRigDropped(name(session)),
                style: kSimpleText.copyWith(color: AraColors.textSecondary),
              ),
              if (session != null) ...[
                heading(l10n.simpleRigThisSession),
                row(session, isSession: true),
              ],
              heading(session == null ? l10n.simpleRigRigs : l10n.simpleRigOthers),
              if (others.isEmpty)
                Text(l10n.simpleRigLooking, style: kSimpleText.copyWith(color: AraColors.textSecondary))
              else
                for (final s in others) row(s, isSession: false),
              if (failed != null)
                // Announced as it appears, as the message pills are.
                Semantics(
                  liveRegion: true,
                  child: Text(
                    l10n.simpleRigNoAnswer('${failed.hostname}:${failed.port}'),
                    key: const Key('simple-rig-card-error'),
                    style: kSimpleText.copyWith(color: AraColors.errorText),
                  ),
                ),
              if (!_addressOpen)
                Align(
                  alignment: Alignment.centerLeft,
                  child: TextButton(
                    key: const Key('simple-rig-card-address-open'),
                    onPressed: () => setState(() => _addressOpen = true),
                    child: Text(l10n.simpleRigAddByAddress),
                  ),
                )
              else
                Row(
                  spacing: AraSpace.s8,
                  children: [
                    Expanded(
                      child: TextField(
                        key: const Key('simple-rig-card-address'),
                        controller: _address,
                        autofocus: true,
                        style: kSimpleText,
                        decoration: InputDecoration(
                          labelText: l10n.simpleRigAddByAddress,
                          hintText: l10n.simpleRigAddressHint,
                          errorText: _badAddress ? l10n.simpleRigAddressInvalid : null,
                          errorMaxLines: 3,
                        ),
                        onChanged: (_) {
                          if (_badAddress) setState(() => _badAddress = false);
                        },
                        onSubmitted: (_) => _connectAddress(),
                      ),
                    ),
                    OutlinedButton(
                      key: const Key('simple-rig-card-address-connect'),
                      style: OutlinedButton.styleFrom(minimumSize: const Size(0, 44)),
                      onPressed: _connecting != null ? null : _connectAddress,
                      child: Text(_connectingAddress ? l10n.simpleRigConnecting : l10n.simpleRigConnect),
                    ),
                  ],
                ),
            ],
          ),
        ),
      ),
    );
  }

  void _connectAddress() {
    final server = parseRigAddress(_address.text);
    setState(() => _badAddress = server == null);
    if (server == null) return;
    final session = ref.read(offlineModeProvider) ? null : ref.read(activeServerProvider);
    _connectingAddress = true;
    unawaited(_connect(server, session: server == session));
  }
}

/// #1559 — the card folded into one line beside Stop, on a window too narrow
/// to hold it between the rails: the rails stay usable, and Connect opens it.
class SimpleRigBar extends ConsumerWidget {
  const SimpleRigBar({super.key, required this.onOpen, this.focusNode});

  final VoidCallback onOpen;

  /// Connect's focus, for the shell to give back when the card closes.
  final FocusNode? focusNode;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = context.l10n;
    return SimplePanel(
      key: const Key('simple-rig-bar'),
      padding: const EdgeInsets.fromLTRB(AraSpace.s12, AraSpace.s4, AraSpace.s4, AraSpace.s4),
      // Connect drops under the words when large text leaves no room beside them.
      child: Wrap(
        alignment: WrapAlignment.spaceBetween,
        crossAxisAlignment: WrapCrossAlignment.center,
        spacing: AraSpace.s8,
        runSpacing: AraSpace.s4,
        children: [
          Text(
            ref.watch(offlineModeProvider) ? l10n.simpleRigGoOnline : l10n.shellRigNotConnected,
            style: kSimpleText.copyWith(color: AraColors.accentWarning),
          ),
          FilledButton(
            key: const Key('simple-rig-bar-connect'),
            focusNode: focusNode,
            style: FilledButton.styleFrom(minimumSize: const Size(0, 44)),
            onPressed: onOpen,
            child: Text(l10n.simpleRigConnect),
          ),
        ],
      ),
    );
  }
}
