#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Collections.Generic;
using System.Text.Json.Serialization;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;

namespace OpenAstroAra.Server;

/// <summary>
/// Phase 14a — System.Text.Json source-generated serializer context.
/// Closes the long-running AOT-readiness gap tracked in PORT_TODO.md:
/// <c>OpenAstroAra.Server.csproj</c> has <c>&lt;PublishAot&gt;true&lt;/PublishAot&gt;</c>
/// but until this PR there was no <see cref="JsonSerializerContext"/>,
/// so <c>dotnet run</c> in Development mode would throw
/// <c>System.NotSupportedException: JsonTypeInfo metadata for type
/// 'OpenAstroAra.Server.Contracts.SequenceCreateRequestDto' was not
/// provided</c> on the first request when OpenAPI introspection ran.
///
/// Every DTO record in <see cref="Contracts"/> + the WS event envelope
/// is registered here. Concrete <see cref="CursorPage{T}"/> instantiations
/// for each paginated endpoint and the collection wrappers
/// (<see cref="IReadOnlyList{T}"/>) for endpoints that return lists are
/// also registered so the runtime never falls back to reflection.
///
/// Property naming policy is <c>SnakeCaseLower</c> matching the
/// <c>ConfigureHttpJsonOptions</c> setup in <see cref="Program"/> so the
/// wire shape stays consistent whether the request is served by the
/// source-gen path or by an OpenAPI-introspection path that uses its own
/// resolver chain.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ApiSurfaceVersionDto))]
[JsonSerializable(typeof(ApiVersionsDto))]
[JsonSerializable(typeof(AutoFlatsDecisionRequestDto))]
[JsonSerializable(typeof(AutofocusSettingsDto))]
[JsonSerializable(typeof(BackupClaimRequestDto))]
[JsonSerializable(typeof(BackupFrameDto))]
[JsonSerializable(typeof(BackupSubscriptionDto))]
[JsonSerializable(typeof(BackupZipDto))]
[JsonSerializable(typeof(BestFrameDto))]
[JsonSerializable(typeof(BugReportPreparationDto))]
[JsonSerializable(typeof(BulkDeleteRequestDto))]
[JsonSerializable(typeof(BulkRateRequestDto))]
[JsonSerializable(typeof(BulkTagRequestDto))]
[JsonSerializable(typeof(CalibrationFilterSummaryDto))]
[JsonSerializable(typeof(CalibrationSessionDto))]
[JsonSerializable(typeof(CameraCapabilitiesDto))]
[JsonSerializable(typeof(CameraDto))]
[JsonSerializable(typeof(CameraStateDto))]
[JsonSerializable(typeof(ConnectRequestDto))]
[JsonSerializable(typeof(DarkLibraryBuildRequestDto))]
[JsonSerializable(typeof(DarkLibraryEntryDto))]
[JsonSerializable(typeof(DarkLibraryStateDto))]
[JsonSerializable(typeof(DataManagerActiveDownloadDto))]
[JsonSerializable(typeof(DataManagerStateDto))]
[JsonSerializable(typeof(DataPackageDto))]
[JsonSerializable(typeof(DiagnosticEventDto))]
[JsonSerializable(typeof(DiagnosticIssueDto))]
[JsonSerializable(typeof(DiagnosticsModeDto))]
[JsonSerializable(typeof(DiagnosticsModeRequestDto))]
[JsonSerializable(typeof(DiagnosticsStateDto))]
[JsonSerializable(typeof(DiscoveredDeviceDto))]
[JsonSerializable(typeof(DomeDto))]
[JsonSerializable(typeof(DomeSlewRequestDto))]
[JsonSerializable(typeof(DomeStateDto))]
[JsonSerializable(typeof(DownloadRequestDto))]
[JsonSerializable(typeof(EquipmentConnectionDto))]
[JsonSerializable(typeof(ExposureRequestDto))]
[JsonSerializable(typeof(ExposureResponseDto))]
[JsonSerializable(typeof(FilenamesSettingsDto))]
[JsonSerializable(typeof(FilterChangeRequestDto))]
[JsonSerializable(typeof(FilterSlotDto))]
[JsonSerializable(typeof(FilterWheelDto))]
[JsonSerializable(typeof(FilterWheelStateDto))]
[JsonSerializable(typeof(FlatDeviceDto))]
[JsonSerializable(typeof(FlatDeviceStateDto))]
[JsonSerializable(typeof(FlatPanelRequestDto))]
[JsonSerializable(typeof(FocuserCapabilitiesDto))]
[JsonSerializable(typeof(FocuserDto))]
[JsonSerializable(typeof(FocuserMoveRequestDto))]
[JsonSerializable(typeof(FocuserStateDto))]
[JsonSerializable(typeof(FocusTempPointDto))]
[JsonSerializable(typeof(FrameDto))]
[JsonSerializable(typeof(FrameListItemDto))]
[JsonSerializable(typeof(FramePreviewRequestDto))]
[JsonSerializable(typeof(FrameQualityBucketDto))]
[JsonSerializable(typeof(GeneratedFlatSequenceDto))]
[JsonSerializable(typeof(GeneratedFlatStepDto))]
[JsonSerializable(typeof(GuiderConnectRequestDto))]
[JsonSerializable(typeof(GuiderDto))]
[JsonSerializable(typeof(GuiderStateDto))]
[JsonSerializable(typeof(GuidingRmsPointDto))]
[JsonSerializable(typeof(HfrAnalysisDto))]
[JsonSerializable(typeof(HfrTimeSeriesPointDto))]
[JsonSerializable(typeof(ImagingDefaultsDto))]
[JsonSerializable(typeof(InstructionProgressDto))]
[JsonSerializable(typeof(LogEntryDto))]
[JsonSerializable(typeof(LogTailRequestDto))]
[JsonSerializable(typeof(MatchingFlatsRequestDto))]
[JsonSerializable(typeof(MosaicCreateRequestDto))]
[JsonSerializable(typeof(MosaicDto))]
[JsonSerializable(typeof(MosaicPanelDto))]
[JsonSerializable(typeof(MosaicProgressDto))]
[JsonSerializable(typeof(NotificationActionRequestDto))]
[JsonSerializable(typeof(NotificationCategoryPrefDto))]
[JsonSerializable(typeof(NotificationDto))]
[JsonSerializable(typeof(NotificationPreferenceDto))]
[JsonSerializable(typeof(NotificationsSettingsDto))]
[JsonSerializable(typeof(ObservingConditionsDto))]
[JsonSerializable(typeof(OperationAcceptedDto))]
[JsonSerializable(typeof(ParkRequestDto))]
[JsonSerializable(typeof(PendingRestartDto))]
[JsonSerializable(typeof(Phd2SettingsDto))]
[JsonSerializable(typeof(PlateSolveSettingsDto))]
[JsonSerializable(typeof(PolarAlignFrameDto))]
[JsonSerializable(typeof(PolarAlignStateDto))]
[JsonSerializable(typeof(ProfileShareDto))]
[JsonSerializable(typeof(ProfileShareImportPreviewDto))]
[JsonSerializable(typeof(ProfileSnapshotDto))]
[JsonSerializable(typeof(QualityScoreBreakdownDto))]
[JsonSerializable(typeof(QuietHoursDto))]
[JsonSerializable(typeof(ReleaseNotesDto))]
[JsonSerializable(typeof(RestoreRequestDto))]
[JsonSerializable(typeof(ResumeTargetRequestDto))]
[JsonSerializable(typeof(RotatorDto))]
[JsonSerializable(typeof(RotatorMoveRequestDto))]
[JsonSerializable(typeof(RotatorStateDto))]
[JsonSerializable(typeof(SafetyMonitorDto))]
[JsonSerializable(typeof(SafetyPoliciesDto))]
[JsonSerializable(typeof(SequenceCreateRequestDto))]
[JsonSerializable(typeof(SequenceDto))]
[JsonSerializable(typeof(SequenceImportRequestDto))]
[JsonSerializable(typeof(SequenceImportResultDto))]
[JsonSerializable(typeof(SequenceListItemDto))]
[JsonSerializable(typeof(SequenceRunStateDto))]
[JsonSerializable(typeof(SequenceShareDto))]
[JsonSerializable(typeof(SequenceStartRequestDto))]
[JsonSerializable(typeof(SequenceTemplateDto))]
[JsonSerializable(typeof(SequenceUpdateRequestDto))]
[JsonSerializable(typeof(ServerInfoDto))]
[JsonSerializable(typeof(ServerStateDto))]
[JsonSerializable(typeof(SessionDto))]
[JsonSerializable(typeof(SessionRestretchRequestDto))]
[JsonSerializable(typeof(SiteSettingsDto))]
[JsonSerializable(typeof(SlewRequestDto))]
[JsonSerializable(typeof(StatsBestFramesDto))]
[JsonSerializable(typeof(StatsCalendarDayDto))]
[JsonSerializable(typeof(StatsCalendarDto))]
[JsonSerializable(typeof(StatsFocusTempDto))]
[JsonSerializable(typeof(StatsFrameQualityDto))]
[JsonSerializable(typeof(StatsGuidingDto))]
[JsonSerializable(typeof(StatsOverviewDto))]
[JsonSerializable(typeof(StatsSparklineDto<System.DateTimeOffset, double>))]
[JsonSerializable(typeof(StatsTargetsDto))]
[JsonSerializable(typeof(StatsTargetSummaryDto))]
[JsonSerializable(typeof(StorageSettingsDto))]
[JsonSerializable(typeof(SwitchDto))]
[JsonSerializable(typeof(SwitchPortDto))]
[JsonSerializable(typeof(SwitchValueRequestDto))]
[JsonSerializable(typeof(TelescopeCapabilitiesDto))]
[JsonSerializable(typeof(TelescopeDto))]
[JsonSerializable(typeof(TelescopeStateDto))]
[JsonSerializable(typeof(TemplateInstantiateRequestDto))]
[JsonSerializable(typeof(WsEventEnvelopeDto))]
// Concrete CursorPage<T> instantiations — one per paginated endpoint.
[JsonSerializable(typeof(CursorPage<CalibrationSessionDto>))]
[JsonSerializable(typeof(CursorPage<DiagnosticEventDto>))]
[JsonSerializable(typeof(CursorPage<FrameListItemDto>))]
[JsonSerializable(typeof(CursorPage<MosaicDto>))]
[JsonSerializable(typeof(CursorPage<NotificationDto>))]
[JsonSerializable(typeof(CursorPage<SequenceListItemDto>))]
[JsonSerializable(typeof(CursorPage<SessionDto>))]
// Collection wrappers — endpoints that return raw lists.
[JsonSerializable(typeof(IReadOnlyList<BackupZipDto>))]
[JsonSerializable(typeof(IReadOnlyList<DarkLibraryEntryDto>))]
[JsonSerializable(typeof(IReadOnlyList<DataPackageDto>))]
[JsonSerializable(typeof(IReadOnlyList<DiscoveredDeviceDto>))]
[JsonSerializable(typeof(IReadOnlyList<LogEntryDto>))]
[JsonSerializable(typeof(IReadOnlyList<MosaicPanelDto>))]
[JsonSerializable(typeof(IReadOnlyList<SequenceTemplateDto>))]
[JsonSerializable(typeof(IReadOnlyList<WsEventEnvelopeDto>))]
public partial class AraJsonSerializerContext : JsonSerializerContext {
}
