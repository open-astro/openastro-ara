import '../services/dso_catalog_service.dart';
import '../services/target_resolve_api.dart';
import 'coord_parse.dart';

/// Where a typed search goes (#1267). Pure, so the planetarium view's
/// `_submitSearch` is a switch over this and the decision is unit-tested.
enum SearchRouteKind {
  /// A pasted position: open the coordinate dialog with it.
  coordinates,

  /// One of our catalog objects: a `goto` command, no network.
  catalog,

  /// A name only the planetarium engine resolves (the Sun, Moon, planets):
  /// its own search, straight away. These are moving bodies with no fixed
  /// J2000 position, so the daemon's resolver must never see them.
  engine,

  /// Not ours and not a solar-system body: ask the daemon's resolver, and on
  /// a miss fall through to the engine search.
  resolve,
}

class SearchRoute {
  final SearchRouteKind kind;

  /// The planetarium command for [SearchRouteKind.catalog] and
  /// [SearchRouteKind.engine], and the fall-through command for
  /// [SearchRouteKind.resolve]; null for coordinates.
  final Map<String, Object?>? command;

  const SearchRoute(this.kind, this.command);
}

/// The solar-system names the planetarium engine owns. Lower-case; matched
/// whole, ignoring case and surrounding space.
const solarSystemNames = {
  'sun',
  'sol',
  'moon',
  'mercury',
  'venus',
  'earth',
  'mars',
  'jupiter',
  'saturn',
  'uranus',
  'neptune',
  'pluto',
  'ceres',
  'vesta',
  'pallas',
  'juno',
  'io',
  'europa',
  'ganymede',
  'callisto',
  'titan',
  'phobos',
  'deimos',
  'enceladus',
  'rhea',
  'iapetus',
  'dione',
  'tethys',
  'mimas',
  'triton',
  'charon',
  'titania',
  'oberon',
};

bool isSolarSystemName(String query) =>
    solarSystemNames.contains(query.trim().toLowerCase());

SearchRoute routeSearch(List<PlanningDso> catalog, String query) {
  final q = query.trim();
  if (looksLikeCoordinates(q)) {
    return const SearchRoute(SearchRouteKind.coordinates, null);
  }
  final cmd = planetariumSearchCommand(catalog, q);
  if (cmd['type'] == 'goto') {
    return SearchRoute(SearchRouteKind.catalog, cmd);
  }
  if (isSolarSystemName(q)) {
    return SearchRoute(SearchRouteKind.engine, cmd);
  }
  return SearchRoute(SearchRouteKind.resolve, cmd);
}

/// What to tell the user when the daemon did not resolve a name and the
/// search went to the planetarium's own engine instead: null when nothing is
/// worth saying (the engine may well find it), the daemon's reason when the
/// name is not in the offline index and Simbad could not be reached, so an
/// offline miss is explained rather than left spinning.
String? searchFallbackMessage(TargetResolution r) {
  if (r.resolved) return null;
  final m = r.message;
  if (m == null || m.isEmpty) return null;
  return r.offline
      ? '$m If the sky map does not find it either, try again with internet.'
      : m;
}
