import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/settings/autofocus_settings_state.dart';
import 'package:openastroara/state/settings/camera_electronics_state.dart';
import 'package:openastroara/state/settings/filter_set_state.dart';
import 'package:openastroara/state/settings/imaging_defaults_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';
import 'package:openastroara/state/settings/plate_solve_settings_state.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';

/// The sections the setup wizard writes, other than Storage, served from
/// memory with a tag and taken back with any tag (ADR 0015), for a fake
/// [ProfileApi] whose test is about the wizard's flow, not the rig. Storage
/// stays with the fake (FakeStorageApi serves it).
mixin WizardSectionsInMemory on ProfileApi {
  /// The last guider section the wizard wrote.
  Phd2Settings? lastPhd2Put;

  static Future<({T value, String? etag})> _get<T>(T value) async =>
      (value: value, etag: '"t"');
  static Future<({T value, String? etag})> _put<T>(T v, String? ifMatch) async =>
      (value: v, etag: ifMatch);

  @override
  Future<OpticsSettings> getOptics() async => const OpticsSettings();
  @override
  Future<({SiteSettings value, String? etag})> getSiteSection() =>
      _get(const SiteSettings());
  @override
  Future<({SiteSettings value, String? etag})> putSiteSection(
    SiteSettings v, {
    String? ifMatch,
  }) => _put(v, ifMatch);
  @override
  Future<({OpticsSettings value, String? etag})> getOpticsSection() =>
      _get(const OpticsSettings());
  @override
  Future<({OpticsSettings value, String? etag})> putOpticsSection(
    OpticsSettings v, {
    String? ifMatch,
  }) => _put(v, ifMatch);
  @override
  Future<({ImagingDefaults value, String? etag})> getImagingDefaultsSection() =>
      _get(const ImagingDefaults());
  @override
  Future<({ImagingDefaults value, String? etag})> putImagingDefaultsSection(
    ImagingDefaults v, {
    String? ifMatch,
  }) => _put(v, ifMatch);
  @override
  Future<({Phd2Settings value, String? etag})> getPhd2Section() =>
      _get(const Phd2Settings());
  @override
  Future<({Phd2Settings value, String? etag})> putPhd2Section(
    Phd2Settings v, {
    String? ifMatch,
  }) {
    lastPhd2Put = v;
    return _put(v, ifMatch);
  }

  @override
  Future<({PlateSolveSettings value, String? etag})> getPlateSolveSection() =>
      _get(const PlateSolveSettings());
  @override
  Future<({PlateSolveSettings value, String? etag})> putPlateSolveSection(
    PlateSolveSettings v, {
    String? ifMatch,
  }) => _put(v, ifMatch);
  @override
  Future<({AutofocusSettings value, String? etag})> getAutofocusSection() =>
      _get(const AutofocusSettings());
  @override
  Future<({AutofocusSettings value, String? etag})> putAutofocusSection(
    AutofocusSettings v, {
    String? ifMatch,
  }) => _put(v, ifMatch);
  @override
  Future<({CameraElectronics value, String? etag})>
  getCameraElectronicsSection() => _get(const CameraElectronics());
  @override
  Future<({CameraElectronics value, String? etag})> putCameraElectronicsSection(
    CameraElectronics v, {
    String? ifMatch,
  }) => _put(v, ifMatch);
  @override
  Future<({FilterSetSettings value, String? etag})> getFilterSetSection() =>
      _get(const FilterSetSettings());
  @override
  Future<({FilterSetSettings value, String? etag})> putFilterSetSection(
    FilterSetSettings v, {
    String? ifMatch,
  }) => _put(v, ifMatch);
  @override
  Future<({SafetyPolicies value, String? etag})> getSafetySection() =>
      _get(const SafetyPolicies());
  @override
  Future<({SafetyPolicies value, String? etag})> putSafetySection(
    SafetyPolicies v, {
    String? ifMatch,
  }) => _put(v, ifMatch);
}
