import 'dart:math' as math;

import '../services/tonight_sky_api.dart' show TonightFilterAdvice;
import '../state/settings/filter_set_state.dart';
import 'optimal_sub.dart';

/// NEXTGEN §1 — filter/emission-aware planning advice, ported from the
/// daemon's `FilterAdvice` under the 2026-07-15 PORT_DECISIONS call: target
/// emission character × the user's declared filter set × the site's Bortle →
/// a recommended approach + one-line reason. Advise, don't dictate — a tag,
/// never a gate, never a score input.

/// A target's emission character from its catalog type. Emission-line objects
/// punch through light pollution in narrowband; continuum objects gain little
/// from it; Mixed can be either; Unknown gets NO advice — never guess.
enum EmissionClass { unknown, emissionLine, continuum, mixed }

const int _brightSkyBortle = 5;
const int _veryBrightSkyBortle = 6;

/// Emission character from the catalog type — OpenNGC codes (HII/EmN/PN/SNR/
/// G…/OCl/GCl/RfN/Neb/Cl+N) plus the starter catalog's plain names.
EmissionClass classifyEmission(String? type) => switch (type?.trim()) {
      'HII' || 'EmN' || 'PN' || 'SNR' => EmissionClass.emissionLine,
      'G' ||
      'GPair' ||
      'GTrpl' ||
      'GGroup' ||
      'OCl' ||
      'GCl' ||
      'RfN' =>
        EmissionClass.continuum,
      'galaxy' || 'cluster' => EmissionClass.continuum,
      'Neb' || 'Cl+N' || 'nebula' => EmissionClass.mixed,
      _ => EmissionClass.unknown,
    };

/// The recommendation + one-line reason, or null when there is nothing honest
/// to say (empty filter set, unknown emission character).
(TonightFilterAdvice, String)? adviseFilter(
    EmissionClass emission, FilterSetSettings filterSet, int bortleClass) {
  final filters = filterSet.filters;
  if (filters.isEmpty || emission == EmissionClass.unknown) return null;

  final monoNb = filters
      .where((f) =>
          f.kind == FilterKind.ha ||
          f.kind == FilterKind.oiii ||
          f.kind == FilterKind.sii)
      .toList();
  final duo = filters
      .where((f) => f.kind == FilterKind.duo || f.kind == FilterKind.tri)
      .toList();
  final hasBroadband = filters.any((f) =>
      f.kind == FilterKind.l ||
      f.kind == FilterKind.r ||
      f.kind == FilterKind.g ||
      f.kind == FilterKind.b ||
      f.kind == FilterKind.osc);

  switch (emission) {
    case EmissionClass.emissionLine:
      if (monoNb.isNotEmpty) {
        return (
          TonightFilterAdvice.narrowband,
          'Emission-line target — narrowband (${_filterNames(monoNb)}) sees '
              '${_skyRatio(monoNb)}× less sky glow than broadband, efficient '
              'even under a Bortle $bortleClass sky.'
        );
      }
      if (duo.isNotEmpty) {
        return (
          TonightFilterAdvice.duoband,
          'Emission-line target — OSC + ${_filterNames(duo)} cuts the sky '
              'glow ~${_skyRatio(duo)}× per Bayer channel vs unfiltered '
              'broadband.'
        );
      }
      return (
        TonightFilterAdvice.broadband,
        bortleClass >= _brightSkyBortle
            ? 'Emission-line target with broadband-only filters under a '
                'Bortle $bortleClass sky — expect many hours of integration; '
                'a dual-band filter would cut this dramatically.'
            : 'Emission-line target with broadband-only filters — workable '
                'under your dark sky, but a narrowband/dual-band filter would '
                'still cut the integration needed.'
      );

    case EmissionClass.continuum:
      var reason =
          'Broadband continuum target (starlight) — narrowband would starve '
          'it; shoot '
          '${hasBroadband ? _filterNames(_broadbandOf(filterSet)) : 'broadband'}';
      if (bortleClass >= _veryBrightSkyBortle) {
        reason += '. Faint under a Bortle $bortleClass sky — plan generous '
            'integration or a darker site';
      }
      return (TonightFilterAdvice.broadband, '$reason.');

    case EmissionClass.mixed:
      if (bortleClass >= _brightSkyBortle && monoNb.isNotEmpty) {
        return (
          TonightFilterAdvice.narrowband,
          'Mixed emission/continuum target under a Bortle $bortleClass sky — '
              'lead with narrowband (${_filterNames(monoNb)}) for the '
              'emission structure.'
        );
      }
      if (bortleClass >= _brightSkyBortle && duo.isNotEmpty) {
        return (
          TonightFilterAdvice.duoband,
          'Mixed emission/continuum target under a Bortle $bortleClass sky — '
              'OSC + ${_filterNames(duo)} favours the emission structure.'
        );
      }
      return (
        TonightFilterAdvice.broadband,
        'Mixed emission/continuum target — broadband captures both components'
            '${monoNb.isNotEmpty || duo.isNotEmpty ? '; add narrowband subs for the emission detail' : ''}.'
      );

    case EmissionClass.unknown:
      return null;
  }
}

/// The user's filter that best represents an approach — what the Optimal-Sub
/// figure is computed for. Null when the set has no filter of that approach.
PlanningFilter? representativeFilter(
    FilterSetSettings filterSet, TonightFilterAdvice approach) {
  final filters = filterSet.filters;
  if (filters.isEmpty) return null;
  PlanningFilter? first(bool Function(PlanningFilter) test) =>
      filters.where(test).firstOrNull;
  return switch (approach) {
    TonightFilterAdvice.narrowband => first((f) => f.kind == FilterKind.ha) ??
        first((f) => f.kind == FilterKind.oiii || f.kind == FilterKind.sii),
    TonightFilterAdvice.duoband => first((f) => f.kind == FilterKind.duo) ??
        first((f) => f.kind == FilterKind.tri),
    TonightFilterAdvice.broadband => first((f) => f.kind == FilterKind.l) ??
        first((f) => f.kind == FilterKind.osc) ??
        first((f) =>
            f.kind == FilterKind.r ||
            f.kind == FilterKind.g ||
            f.kind == FilterKind.b),
  };
}

/// A filter entry's effective passband: its own bandwidth, or its kind's
/// default.
double effectiveBandwidthNm(PlanningFilter filter) =>
    filter.bandwidthNm > 0 ? filter.bandwidthNm : filter.kind.defaultBandwidthNm;

String _skyRatio(List<PlanningFilter> nb) {
  final narrowest = nb.map(effectiveBandwidthNm).reduce(math.min);
  final ratio = defaultBroadbandBandwidthNm / math.max(narrowest, 0.1);
  return '~${ratio.round()}';
}

List<PlanningFilter> _broadbandOf(FilterSetSettings set) => set.filters
    .where((f) =>
        f.kind == FilterKind.l ||
        f.kind == FilterKind.r ||
        f.kind == FilterKind.g ||
        f.kind == FilterKind.b ||
        f.kind == FilterKind.osc)
    .toList();

String _filterNames(List<PlanningFilter> filters) =>
    filters.take(3).map((f) => f.name).join('/');
