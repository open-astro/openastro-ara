import 'package:flutter/widgets.dart';

/// Calls [onChanged] with whether the planetarium is on screen: now, and on
/// each change. Simple Mode keeps the sky loaded offstage with its tickers
/// off while a frame shows, so the page can rest then and draw nothing.
class PlanetariumShownListener extends StatefulWidget {
  const PlanetariumShownListener({
    super.key,
    required this.onChanged,
    required this.child,
  });

  final ValueChanged<bool> onChanged;
  final Widget child;

  @override
  State<PlanetariumShownListener> createState() =>
      _PlanetariumShownListenerState();
}

class _PlanetariumShownListenerState extends State<PlanetariumShownListener> {
  bool? _shown;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    final shown = TickerMode.valuesOf(context).enabled;
    if (shown == _shown) return;
    _shown = shown;
    widget.onChanged(shown);
  }

  @override
  Widget build(BuildContext context) => widget.child;
}
