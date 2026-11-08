import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/sky_atlas/planetarium_selection_state.dart';

/// Runs the searches sent on [planetariumSearchProvider], including one sent
/// before this mounted: a listener only hears changes after it subscribes, and
/// Simple Mode creates the sky view in the same step as it sends the search.
/// Mount it only once the view can run a search; until then the query waits.
class PlanetariumSearchListener extends ConsumerStatefulWidget {
  const PlanetariumSearchListener({
    super.key,
    required this.onSearch,
    required this.child,
  });
  final void Function(String query) onSearch;
  final Widget child;

  @override
  ConsumerState<PlanetariumSearchListener> createState() =>
      _PlanetariumSearchListenerState();
}

class _PlanetariumSearchListenerState
    extends ConsumerState<PlanetariumSearchListener> {
  @override
  void initState() {
    super.initState();
    // After the frame: clearing a provider while the tree builds is an error.
    WidgetsBinding.instance.addPostFrameCallback((_) => _take());
  }

  void _take() {
    if (!mounted) return;
    final q = ref.read(planetariumSearchProvider);
    if (q == null) return;
    ref.read(planetariumSearchProvider.notifier).clear();
    widget.onSearch(q);
  }

  @override
  Widget build(BuildContext context) {
    ref.listen<String?>(planetariumSearchProvider, (_, q) {
      if (q != null) _take();
    });
    return widget.child;
  }
}
