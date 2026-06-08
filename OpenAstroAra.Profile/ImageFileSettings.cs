#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Enums;
using OpenAstroAra.Profile.Interfaces;
using System;
using System.IO;
using System.Runtime.Serialization;

namespace OpenAstroAra.Profile {

    [Serializable()]
    [DataContract]
    public class ImageFileSettings : Settings, IImageFileSettings {

        [OnDeserializing]
        public void OnDeserializing(StreamingContext context) {
            SetDefaultValues();
        }

        protected override void SetDefaultValues() {
            filePath = Path.Combine(System.Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "N.I.N.A");
            filePattern = "$$DATEMINUS12$$\\$$IMAGETYPE$$\\$$DATETIME$$_$$FILTER$$_$$SENSORTEMP$$_$$EXPOSURETIME$$s_$$FRAMENR$$";
            filePatternDARK = "";
            filePatternBIAS = "";
            filePatternFLAT = "";
            fileType = FileType.FITS;
            tiffCompressionType = TIFFCompressionType.NONE;
            xisfCompressionType = XISFCompressionType.NONE;
            xisfChecksumType = XISFChecksumType.SHA256;
            xisfByteShuffling = false;
            fitsCompressionType = FITSCompressionType.NONE;
            fitsAddFzExtension = true;
            fitsUseLegacyWriter = true;
        }

        private string filePath = string.Empty;

        [DataMember]
        public string FilePath {
            get => filePath;
            set {
                if (filePath != value) {
                    filePath = value;
                    RaisePropertyChanged();
                }
            }
        }

        private string filePattern = string.Empty;

        [DataMember]
        public string FilePattern {
            get => filePattern;
            set {
                if (filePattern != value) {
                    filePattern = value;
                    RaisePropertyChanged();
                }
            }
        }

        private string filePatternDARK = string.Empty;

        [DataMember]
        public string FilePatternDARK {
            get => filePatternDARK;
            set {
                if (filePatternDARK != value) {
                    filePatternDARK = value;
                    RaisePropertyChanged();
                }
            }
        }

        private string filePatternFLAT = string.Empty;

        [DataMember]
        public string FilePatternFLAT {
            get => filePatternFLAT;
            set {
                if (filePatternFLAT != value) {
                    filePatternFLAT = value;
                    RaisePropertyChanged();
                }
            }
        }

        private string filePatternBIAS = string.Empty;

        [DataMember]
        public string FilePatternBIAS {
            get => filePatternBIAS;
            set {
                if (filePatternBIAS != value) {
                    filePatternBIAS = value;
                    RaisePropertyChanged();
                }
            }
        }

        private FileType fileType;

        [DataMember]
        public FileType FileType {
            get {
                /*
                 * The TIFFLzw and TIFFZip file types are obsoleted and
                 * the compression options are specified separately now. This block
                 * will catch any old profiles that have old file types set and
                 * correct them to adhere to the new scheme.
                 */
#pragma warning disable CS0612 // Type or member is obsolete
                switch (fileType) {
                    case FileType.TIFFLzw:
                        FileType = FileType.TIFF;
                        TIFFCompressionType = TIFFCompressionType.LZW;
                        break;

                    case FileType.TIFFZip:
                        FileType = FileType.TIFF;
                        TIFFCompressionType = TIFFCompressionType.ZIP;
                        break;
                }
#pragma warning restore CS0612 // Type or member is obsolete

                return fileType;
            }
            set {
                if (fileType != value) {
                    fileType = value;
                    RaisePropertyChanged();
                }
            }
        }

        private TIFFCompressionType tiffCompressionType;

        [DataMember]
        public TIFFCompressionType TIFFCompressionType {
            get => tiffCompressionType;
            set {
                if (tiffCompressionType != value) {
                    tiffCompressionType = value;
                    RaisePropertyChanged();
                }
            }
        }

        private XISFCompressionType xisfCompressionType;

        [DataMember]
        public XISFCompressionType XISFCompressionType {
            get => xisfCompressionType;
            set {
                if (xisfCompressionType != value) {
                    xisfCompressionType = value;
                    RaisePropertyChanged();
                }
            }
        }

        private XISFChecksumType xisfChecksumType;

        [DataMember]
        public XISFChecksumType XISFChecksumType {
            get => xisfChecksumType;
            set {
                if (xisfChecksumType != value) {
                    xisfChecksumType = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool xisfByteShuffling;

        [DataMember]
        public bool XISFByteShuffling {
            get => xisfByteShuffling;
            set {
                if (xisfByteShuffling != value) {
                    xisfByteShuffling = value;
                    RaisePropertyChanged();
                }
            }
        }

        private FITSCompressionType fitsCompressionType;

        [DataMember]
        public FITSCompressionType FITSCompressionType {
            get => fitsCompressionType;
            set {
                if (fitsCompressionType != value) {
                    fitsCompressionType = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool fitsAddFzExtension;

        [DataMember]
        public bool FITSAddFzExtension {
            get => fitsAddFzExtension;
            set {
                if (fitsAddFzExtension != value) {
                    fitsAddFzExtension = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool fitsUseLegacyWriter;
        [DataMember]
        public bool FITSUseLegacyWriter {
            get => fitsUseLegacyWriter;
            set {
                if (fitsUseLegacyWriter != value) {
                    fitsUseLegacyWriter = value;
                    RaisePropertyChanged();
                }
            }
        }

        public string GetFilePattern(string imageType) {
            var pattern = FilePattern;

            if (imageType == "DARK" && !string.IsNullOrWhiteSpace(FilePatternDARK)) {
                pattern = FilePatternDARK;
            }
            if (imageType == "FLAT" && !string.IsNullOrWhiteSpace(FilePatternFLAT)) {
                pattern = FilePatternFLAT;
            }
            if (imageType == "BIAS" && !string.IsNullOrWhiteSpace(FilePatternBIAS)) {
                pattern = FilePatternBIAS;
            }

            return pattern;
        }
    }
}