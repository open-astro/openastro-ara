#region "copyright"

/*
    Copyright � 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Enums;
using OpenAstroAra.Profile.Interfaces;

namespace OpenAstroAra.Image.FileFormat {

    public class FileSaveInfo {
        public string FilePath { get; set; } = string.Empty;
        public string FilePattern { get; set; } = string.Empty;
        public string ForceExtension { get; set; } = string.Empty;
        public FileType FileType { get; set; } = FileType.FITS;
        public TIFFCompressionType TIFFCompressionType { get; set; } = TIFFCompressionType.NONE;
        public XISFCompressionType XISFCompressionType { get; set; } = XISFCompressionType.NONE;
        public XISFChecksumType XISFChecksumType { get; set; } = XISFChecksumType.NONE;
        public bool XISFByteShuffling { get; set; } = false;
        public FITSCompressionType FITSCompressionType { get; set; } = FITSCompressionType.NONE;
        public bool FITSAddFzExtension { get; set; } = false;
        public bool FITSUseLegacyWriter { get; set; } = true;

        public FileSaveInfo(IProfileService? profileService = null) {
            if (profileService != null) {
                FilePath = profileService.ActiveProfile.ImageFileSettings.FilePath;
                FilePattern = profileService.ActiveProfile.ImageFileSettings.FilePattern;
                FileType = profileService.ActiveProfile.ImageFileSettings.FileType;
                TIFFCompressionType = profileService.ActiveProfile.ImageFileSettings.TIFFCompressionType;
                XISFCompressionType = profileService.ActiveProfile.ImageFileSettings.XISFCompressionType;
                XISFByteShuffling = profileService.ActiveProfile.ImageFileSettings.XISFByteShuffling;
                XISFChecksumType = profileService.ActiveProfile.ImageFileSettings.XISFChecksumType;
                FITSCompressionType = profileService.ActiveProfile.ImageFileSettings.FITSCompressionType;
                FITSAddFzExtension = profileService.ActiveProfile.ImageFileSettings.FITSAddFzExtension;
                FITSUseLegacyWriter = profileService.ActiveProfile.ImageFileSettings.FITSUseLegacyWriter;
            }
        }

        public string GetExtension(string defaultExtension) {
            return string.IsNullOrEmpty(ForceExtension) ? defaultExtension : ForceExtension;
        }
    }
}