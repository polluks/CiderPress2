﻿/*
 * Copyright 2023 faddenSoft
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
using System;

using DiskArc;

namespace AppCommon {
    /// <summary>
    /// Functions for identifying file archives and disk images by name or file type.  This is
    /// used for files stored inside file archives or disk images, not those stored on the host
    /// filesystem.  The goal is to filter out anything that doesn't appear to be a file
    /// that we can descend into.
    /// </summary>
    public static class FileIdentifier {
        /// <summary>
        /// Filename extensions used on disk images.
        /// </summary>
        private static string[] sDiskExts = {
            // Not including ".raw", which is too generic.  This includes NuFX, which might
            // or might not hold a disk image, so special handling is required by the caller.
            ".do", ".po", ".iso", ".hdv", ".img", ".d13", ".dsk", ".nib", ".nb2", ".woz",
            ".2mg", ".2img", ".dc", ".dc6", ".image", ".app", ".ddd", ".sdk", ".shk"
        };

        /// <summary>
        /// Filename extensions used on file archives.
        /// </summary>
        private static string[] sArchiveExts = {
            // Not including ".as", which only has a single file that shouldn't be a disk image
            // or archive, so there's no real value in pulling it out.  With ".gz" we only need
            // to dig into them if the extension suggests they hold something interesting, e.g.
            // ".woz.gz", though this is only beneficial to our caller if the .gz itself is stored
            // in an archive.
            //
            // NuFX disk images require special exclusion handling by the caller.
            ".zip", ".bny", ".bqy", ".shk", ".bxy", ".bse", ".sea", ".acu", ".gz"
        };

        /// <summary>
        /// Returns true if the file attributes (type, extension) match those of a file archive.
        /// </summary>
        public static bool HasFileArchiveAttribs(IFileEntry entry, out string ext) {
            bool looksGood = false;
            bool hasProType = false;
            byte proType = 0;
            ushort proAux = 0;
            if (entry.HasHFSTypes) {
                hasProType = FileAttribs.ProDOSFromHFS(entry.HFSFileType, entry.HFSCreator,
                        out proType, out proAux);
            }
            if (!looksGood && !hasProType && entry.HasProDOSTypes) {
                proType = entry.FileType;
                proAux = entry.AuxType;
            }
            if (!looksGood) {
                if (proType == FileAttribs.FILE_TYPE_LBR &&
                        (proAux == 0x8000 || proAux == 0x8002)) {
                    // Binary II or NuFX.
                    looksGood = true;
                }
            }

            ext = Path.GetExtension(entry.FileName).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) {
                return false;
            }

            if (!looksGood) {
                // If it's BIN or NON, or totally typeless (ZIP), test the filename extension.
                if ((!hasProType /*&& !entry.HasHFSTypes*/) ||
                        (hasProType && (proType == FileAttribs.FILE_TYPE_NON ||
                                        proType == FileAttribs.FILE_TYPE_BIN))) {
                    if (ExtInSet(ext, sArchiveExts)) {
                        looksGood = true;
                    }
                }
            }
            return looksGood;
        }

        /// <summary>
        /// Returns true if the file attributes (type, extension) match those of a disk image.
        /// </summary>
        public static bool HasDiskImageAttribs(IFileEntry entry, out string ext) {
            if (entry.IsDiskImage) {
                ext = "po";
                return true;
            }
            bool looksGood = false;
            bool hasProType = false;
            byte proType = 0;
            ushort proAux = 0;
            if (entry.HasHFSTypes) {
                if (entry.HFSCreator == DiskArc.Disk.DiskCopy.HFS_CREATOR &&
                        entry.HFSFileType == DiskArc.Disk.DiskCopy.HFS_FILE_TYPE) {
                    looksGood = true;
                } else {
                    hasProType = FileAttribs.ProDOSFromHFS(entry.HFSFileType, entry.HFSCreator,
                            out proType, out proAux);
                }
            }
            if (!looksGood && !hasProType && entry.HasProDOSTypes) {
                proType = entry.FileType;
                proAux = entry.AuxType;
            }
            if (!looksGood) {
                if (proType == FileAttribs.FILE_TYPE_LBR &&
                        (proAux == 0x0005 || proAux == 0x8002)) {
                    // DiskCopy or NuFX.
                    looksGood = true;
                }
            }

            ext = Path.GetExtension(entry.FileName).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) {
                return false;
            }

            if (!looksGood) {
                // If it's BIN or NON, or totally typeless (ZIP), test the filename extension.
                if ((!hasProType /*&& !entry.HasHFSTypes*/) ||
                        (hasProType && (proType == FileAttribs.FILE_TYPE_NON ||
                                        proType == FileAttribs.FILE_TYPE_BIN))) {
                    if (ExtInSet(ext, sDiskExts)) {
                        looksGood = true;
                    }
                }
            }
            return looksGood;
        }

        /// <summary>
        /// Returns true if the string is in the set.  The caller should convert
        /// <paramref name="ext"/> to lower case before calling.
        /// </summary>
        private static bool ExtInSet(string ext, string[] extensions) {
            foreach (string str in extensions) {
                if (str == ext) {
                    return true;
                }
            }
            return false;
        }
    }
}
