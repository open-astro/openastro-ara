import 'profile_meta.dart';

/// One device's problem after a profile switch (#1710), as the daemon words it.
class ProfileDeviceMessage {
  final String deviceName;
  final String message;

  const ProfileDeviceMessage({required this.deviceName, required this.message});

  factory ProfileDeviceMessage.fromJson(Map<String, dynamic> json) =>
      ProfileDeviceMessage(
        deviceName: json['device_name'] as String? ?? '',
        message: json['message'] as String? ?? '',
      );
}

/// Mirrors `ProfileListDto` from the daemon's §37 multi-profile API
/// (`GET /api/v1/profiles`): the known profiles plus which one is active.
class ProfileList {
  /// The currently-active profile id, or null if none exists yet.
  final String? activeId;
  final List<ProfileMeta> profiles;

  const ProfileList({required this.activeId, required this.profiles});

  /// The active profile's metadata, or null if [activeId] doesn't resolve.
  ProfileMeta? get active {
    for (final p in profiles) {
      if (p.id == activeId) return p;
    }
    return null;
  }

  factory ProfileList.fromJson(Map<String, dynamic> json) {
    // Daemon serializes with SnakeCaseLower (see Program.cs): active_id, profiles.
    final rawProfiles = json['profiles'];
    final list = <ProfileMeta>[];
    if (rawProfiles is List) {
      for (final item in rawProfiles) {
        if (item is Map<String, dynamic>) {
          list.add(ProfileMeta.fromJson(item));
        } else if (item is Map) {
          list.add(ProfileMeta.fromJson(Map<String, dynamic>.from(item)));
        }
      }
    }
    return ProfileList(
      activeId: json['active_id']?.toString(),
      profiles: list,
    );
  }
}
