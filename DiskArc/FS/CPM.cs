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
using System.Diagnostics;

using CommonUtil;
using static DiskArc.Defs;
using static DiskArc.FileAnalyzer.DiskLayoutEntry;
using static DiskArc.IFileSystem;

// TODO:
// - create AllocMap class in commonutil to track allocation bitmap
//   - simple bit vector based on 32-bit ints
//   - need FreeCount, FindFirstFree, MarkFree/MarkUsed
//   - makes VolumeAlloc calls

namespace DiskArc.FS {
    /// <summary>
    /// CP/M filesystem implementation.  Focus is on the Apple II implementation, which was
    /// based on CP/M v2.2.
    /// </summary>
    public class CPM : IFileSystem {
        public const int MAX_FILE_LEN = 8 * 1024*1024;  // 8MB (v2.2 limitation)
        public const int DIR_ENTRY_LEN = 32;            // length of a dir entry extent record
        public const int FILE_REC_LEN = 128;            // length of a "record" in a file
        public const int RECS_PER_EXTENT = 128;         // constant for 5.25" and 3.5" disks
        public const byte NO_DATA = 0xe5;

        internal const int MAX_USER_NUM = 15;
        internal const int RESERVED_SPACE = 31;         // extent status indicating reservation
        internal const int MAX_VALID_STATUS = 0x21;     // CP/M v3 timestamp

        private const string FILENAME_RULES =
            "8.3 format, using printable ASCII characters except for spaces and " +
                "\u201c<>.,;:=?*[]\u201d.";
        private static FSCharacteristics sCharacteristics = new FSCharacteristics(
            name: "CP/M",
            canWrite: true,
            isHierarchical: false,
            dirSep: IFileEntry.NO_DIR_SEP,
            hasResourceForks: false,
            fnSyntax: FILENAME_RULES,
            vnSyntax: string.Empty,
            tsStart: DateTime.MinValue,
            tsEnd: DateTime.MinValue
        );

        //
        // IFileSystem interfaces.
        //

        public FSCharacteristics Characteristics => sCharacteristics;
        public static FSCharacteristics SCharacteristics => sCharacteristics;

        public Notes Notes { get; } = new Notes();

        public bool IsReadOnly { get { return ChunkAccess.IsReadOnly || IsDubious; } }

        public bool IsDubious { get; internal set; }

        public long FreeSpace => 1234567;       // TODO

        public GatedChunkAccess RawAccess { get; }

        //
        // Implementation-specific.
        //

        /// <summary>
        /// Size of an allocation unit, in bytes.
        /// </summary>
        public uint AllocUnitSize { get; }

        /// <summary>
        /// Volume directory start block (512-byte).
        /// </summary>
        public uint DirStartBlock { get; }

        /// <summary>
        /// Number of (512-byte) blocks in the directory.
        /// </summary>
        public uint DirBlockCount { get; }

        /// <summary>
        /// For 5.25" disks, we allow allocation blocks to wrap around to the start of the disk.
        /// </summary>
        public bool DoBlocksWrap { get; }

        /// <summary>
        /// Total allocation blocks present in the filesystem (directory and data areas).
        /// </summary>
        public int TotalAllocBlocks { get; }

        /// <summary>
        /// Data source.  Contents may be shared in various ways.
        /// </summary>
        internal IChunkAccess ChunkAccess { get; private set; }

        /// <summary>
        /// Volume usage map.  Only valid in file-access mode.
        /// </summary>
        internal VolumeUsage? VolUsage { get; private set; }

        internal AppHook AppHook { get; private set; }

        /// <summary>
        /// "Fake" volume directory entry, used to hold directory entries.
        /// </summary>
        private IFileEntry mVolDirEntry;

        /// <summary>
        /// Buffer holding the full disk directory.
        /// </summary>
        private byte[] mDirectoryBuf;

        /// <summary>
        /// Dirty flags, one per directory disk block.
        /// </summary>
        private GroupBool[] mDirectoryDirtyFlags;

        /// <summary>
        /// Full list of extents in this volume.
        /// </summary>
        internal CPM_FileEntry.Extent[] Extents { get; private set; }

        /// <summary>
        /// List of open files.
        /// </summary>
        private OpenFileTracker mOpenFiles = new OpenFileTracker();

        /// <summary>
        /// True if we're in file-access mode, false if raw-access mode.
        /// </summary>
        private bool IsPreppedForFileAccess { get { return mVolDirEntry != IFileEntry.NO_ENTRY; } }


        /// <summary>
        /// Gets the magic parameters for the disk volume.
        /// </summary>
        /// <param name="chunks">Chunk access object.</param>
        /// <param name="allocUnit">Result: size of an allocation unit, in bytes.</param>
        /// <param name="dirStartBlock">Result: directory start block number (512-byte).</param>
        /// <param name="dirBlockCount">Result: number of (512-byte) blocks in directory.</param>
        /// <returns>True if the configuration is known, false if not.</returns>
        private static bool GetDiskParameters(IChunkAccess chunks, out uint allocUnit,
                out uint dirStartBlock, out uint dirBlockCount, out bool doBlocksWrap) {
            switch (chunks.FormattedLength) {
                case 140 * 1024:        // 140KB 5.25" disk
                    allocUnit = 1024;
                    dirStartBlock = 24;
                    dirBlockCount = 4;
                    doBlocksWrap = true;
                    return true;
                case 800 * 1024:        // 800KB 3.5" disk
                    allocUnit = 2048;
                    dirStartBlock = 32;
                    dirBlockCount = 16;
                    doBlocksWrap = false;
                    return true;
                default:
                    allocUnit = dirStartBlock = dirBlockCount = 0;
                    doBlocksWrap = false;
                    return false;
            }
        }

        // Delegate: test image to see if it's ours.
        public static TestResult TestImage(IChunkAccess chunks, AppHook appHook) {
            if (!chunks.HasBlocks) {
                return TestResult.No;
            }

            // Get disk parameters and make some calculations.
            if (!GetDiskParameters(chunks, out uint allocUnit, out uint dirStartBlock,
                    out uint dirBlockCount, out bool doBlocksWrap)) {
                return TestResult.No;
            }
            long usableLen = chunks.FormattedLength - dirStartBlock * BLOCK_SIZE;
            int numAllocBlocks = (int)(usableLen / allocUnit);
            Debug.Assert(numAllocBlocks == 128 || numAllocBlocks == 392);
            if (doBlocksWrap && numAllocBlocks == 128) {
                // 5.25" disks wrap around at the end.
                numAllocBlocks = 140;
            }

            // Scan for extents in the volume directory and see how many we find.  On a blank
            // disk we won't find anything at all, but the fact that it's filled with 0xe5 is a
            // pretty good indication that it's CP/M.  It does not, however, offer any clues to
            // the disk image's sector ordering.
            byte[] buf = new byte[BLOCK_SIZE];
            int extentsFound = 0;
            int badExtents = 0;
            for (uint block = dirStartBlock; block < dirStartBlock + dirBlockCount; block++) {
                chunks.ReadBlockCPM(block, buf, 0);

                for (int offset = 0; offset < BLOCK_SIZE; offset += DIR_ENTRY_LEN) {
                    byte status = buf[offset];
                    if (status == NO_DATA) {
                        // Empty directory slot.
                        continue;
                    } else if (status == RESERVED_SPACE) {
                        // Some disks have a special file that reserves space for the boot image
                        // or DOS hybrid tracks.  Ignore it here.
                        continue;
                    } else if (status > MAX_VALID_STATUS) {
                        badExtents++;
                        continue;
                    }
                    if (status > MAX_USER_NUM) {
                        // We don't interpret extents with special status values, so they don't
                        // count for or against.
                        //Debug.WriteLine("Ignoring extent with status=$" + status.ToString("x2"));
                        continue;
                    }

                    // Check the extent counter.  The low byte must be 0-31.
                    if (buf[offset + 0x0c] > 31) {
                        badExtents++;
                        continue;
                    }

                    // Scan the filename.  Sometimes the high bits are used for access flags,
                    // but control characters are never okay.
                    bool foundBad = false;
                    for (int i = 0; i < CPM_FileEntry.Extent.FILENAME_FIELD_LEN; i++) {
                        byte ch = buf[offset + 1 + i];
                        if ((ch & 0x7f) < 0x20) {
                            foundBad = true;
                            break;
                        }
                    }
                    if (foundBad) {
                        badExtents++;
                        continue;
                    }

                    // Screen the allocation block pointers.
                    foundBad = false;
                    if (numAllocBlocks <= 256) {
                        for (int i = 0; i < 16; i++) {
                            uint ptr = buf[offset + 0x10 + i];
                            if (ptr >= numAllocBlocks) {
                                foundBad = true;
                                break;
                            }
                        }
                    } else {
                        for (int i = 0; i < 16; i += 2) {
                            uint ptr = RawData.GetU16LE(buf, offset + 0x10 + i);
                            if (ptr >= numAllocBlocks) {
                                foundBad = true;
                                break;
                            }
                        }
                    }
                    if (foundBad) {
                        badExtents++;
                        continue;
                    }

                    extentsFound++;
                }
            }

            Debug.WriteLine("CP/M order=" + chunks.FileOrder + " extentsFound=" + extentsFound +
                " badExtents=" + badExtents);
            if (badExtents > 0) {
                return TestResult.No;
            } else if (extentsFound == 0) {
                return TestResult.Maybe;
            } else if (extentsFound <= 4) {
                return TestResult.Good;
            } else {
                return TestResult.Yes;
            }
        }

        // Delegate: returns true if the size (in bytes) is valid for this filesystem.
        public static bool IsSizeAllowed(long size) {
            return (size == 140 * 1024 || size == 800 * 1024);  // floppy disks only
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <remarks>
        /// Because the size of various disk structures is determined by the volume parameters,
        /// we can set up some fixed-size buffers here.
        /// </remarks>
        /// <param name="chunks">Chunk access object.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <exception cref="DAException">The filesystem isn't compatible with the chunk
        ///   source.</exception>
        public CPM(IChunkAccess chunks, AppHook appHook) {
            ChunkAccess = chunks;
            AppHook = appHook;

            RawAccess = new GatedChunkAccess(chunks);
            mVolDirEntry = IFileEntry.NO_ENTRY;

            if (!GetDiskParameters(ChunkAccess, out uint allocUnit, out uint dirStartBlock,
                    out uint dirBlockCount, out bool doBlocksWrap)) {
                throw new DAException("CP/M filesystem can't live here");
            }
            AllocUnitSize = allocUnit;
            DirStartBlock = dirStartBlock;
            DirBlockCount = dirBlockCount;
            DoBlocksWrap = doBlocksWrap;
            int totalBlocks = (int)((chunks.FormattedLength / BLOCK_SIZE) - DirStartBlock);
            Debug.Assert(totalBlocks == 256 || totalBlocks == 1568);
            if (totalBlocks == 256) {
                totalBlocks = 280;      // allow 5.25" disk wrap-around
            }
            int allocMult = (int)allocUnit / BLOCK_SIZE;
            TotalAllocBlocks = totalBlocks / allocMult;
            Debug.Assert(TotalAllocBlocks == 140 || TotalAllocBlocks == 392);

            // Create a buffer to hold the directory blocks.
            mDirectoryBuf = new byte[dirBlockCount * BLOCK_SIZE];
            // Allocate the associated dirty flags.
            mDirectoryDirtyFlags = new GroupBool[dirBlockCount];
            for (int i = 0; i < dirBlockCount; i++) {
                mDirectoryDirtyFlags[i] = new GroupBool();
            }
            // Allocate the extent table.
            Debug.Assert(BLOCK_SIZE % CPM_FileEntry.Extent.LENGTH == 0);
            int extentsPerBlock = BLOCK_SIZE / CPM_FileEntry.Extent.LENGTH;     // 16
            int numExtents = (int)dirBlockCount * extentsPerBlock;              // 64 or 256
            Extents = new CPM_FileEntry.Extent[numExtents];
            int ptrsPerExtent = (TotalAllocBlocks <= 256) ? 16 : 8;
            for (int i = 0; i < numExtents; i++) {
                Extents[i] = new CPM_FileEntry.Extent(i, ptrsPerExtent,
                    mDirectoryDirtyFlags[i / extentsPerBlock]);
            }
        }

        public override string ToString() {
            string rawStr = mVolDirEntry == IFileEntry.NO_ENTRY ? " (raw)" : "";
            return "[CP/M" + rawStr + "]";
        }

        // IDisposable generic finalizer.
        ~CPM() {
            Dispose(false);
        }
        // IDisposable generic Dispose() implementation.
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        private bool mDisposed;
        protected virtual void Dispose(bool disposing) {
            if (mDisposed) {
                AppHook.LogW("Attempting to dispose of CPM object twice");
                return;
            }
            if (!disposing) {
                // This is a GC finalization.  We can't know if the objects we have references
                // to have already been finalized, so all we can do is complain.
                AppHook.LogW("GC disposing of filesystem object " + this);
                if (mOpenFiles.Count != 0) {
                    AppHook.LogW("CPM FS finalized while " + mOpenFiles.Count + " files open");
                }
                return;
            }

            AppHook.LogD("CPM.Dispose(" + disposing + ")");

            // This can happen easily if we have the filesystem in a "using" block and
            // something throws with a file open.  Post a warning and close all files.
            if (mOpenFiles.Count != 0) {
                AppHook.LogI("CPM FS disposed with " + mOpenFiles.Count + " files open; closing");
                CloseAll();
            }

            try {
                Flush();
            } catch {
                AppHook.LogE("Failed while attempting to flush volume");
            }

            if (mVolDirEntry != IFileEntry.NO_ENTRY) {
                // Invalidate all associated file entry objects.
                InvalidateFileEntries();
            }

            RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.Closed;
            mDisposed = true;
        }

        // IFileSystem
        public void Flush() {
            mOpenFiles.FlushAll();
            FlushVolumeDir();
        }

        /// <summary>
        /// Flushes the contents of the volume directory, if they have been changed.
        /// </summary>
        internal void FlushVolumeDir() {
            // TODO
        }

        // IFileSystem
        public void PrepareFileAccess(bool doScan) {
            if (IsPreppedForFileAccess) {
                Debug.WriteLine("Volume already prepared for file access");
                return;
            }

            try {
                // Reset all values and scan the volume.
                IsDubious = false;
                Notes.Clear();
                ScanVolume();
                RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.ReadOnly;
            } catch (Exception ex) {
                // Failed; reset for raw.
                AppHook.LogE("Unable to prepare file access: " + ex.Message);
                PrepareRawAccess();
                throw new DAException("Unable to prepare file access", ex);
            }
        }

        // IFileSystem
        public void PrepareRawAccess() {
            if (mOpenFiles.Count != 0) {
                throw new DAException("Cannot switch to raw access mode with files open");
            }

            Flush();
            if (mVolDirEntry != IFileEntry.NO_ENTRY) {
                // Invalidate the FileEntry tree.  If we don't do this the application could
                // try to use a retained object after it was switched back to file access.
                InvalidateFileEntries();
            }

            mVolDirEntry = IFileEntry.NO_ENTRY;
            VolUsage = null;
            IsDubious = false;
            RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.Open;
        }

        /// <summary>
        /// Marks all file entry objects as invalid.
        /// </summary>
        private void InvalidateFileEntries() {
            Debug.Assert(mVolDirEntry != IFileEntry.NO_ENTRY);
            CPM_FileEntry volDir = (CPM_FileEntry)mVolDirEntry;
            if (!volDir.IsValid) {
                // Already done?  Shouldn't happen.
                return;
            }
            foreach (IFileEntry child in volDir) {
                CPM_FileEntry entry = (CPM_FileEntry)child;
                entry.SaveChanges();
                entry.Invalidate();
            }
            volDir.Invalidate();
        }

        private bool[] mIs525TrackReserved = new bool[35];
        public bool Check525TrackReserved(uint track) {
            if (track < mIs525TrackReserved.Length) {
                return mIs525TrackReserved[track];
            }
            return false;
        }

        /// <summary>
        /// Scans the contents of the volume directory.
        /// </summary>
        /// <exception cref="IOException">Disk access failure.</exception>
        /// <exception cref="DAException">Invalid filesystem.</exception>
        private void ScanVolume() {
            // Create volume usage map.  Assign "system" usage to the boot and directory blocks.
            VolUsage = new VolumeUsage(TotalAllocBlocks);
            for (uint block = 0; block < DirStartBlock + DirBlockCount; block++) {
                VolUsage.MarkInUse(block);
                VolUsage.SetUsage(block, IFileEntry.NO_ENTRY);
            }

            // Read the directory into memory.
            for (int i = 0; i < DirBlockCount; i++) {
                ChunkAccess.ReadBlockCPM(DirStartBlock + (uint)i, mDirectoryBuf, i * BLOCK_SIZE);
            }
            // Mark blocks as clean.
            foreach (GroupBool gb in mDirectoryDirtyFlags) {
                gb.IsSet = false;
            }
            // Load the extent records.
            foreach (CPM_FileEntry.Extent ext in Extents) {
                ext.Load(mDirectoryBuf);
            }

            // Scan the full catalog.
            mVolDirEntry = CPM_FileEntry.ScanDirectory(this);

            // Handle the "reserved space" entries on 140KB 5.25" disks.  Make a map, marking
            // entire tracks as reserved.  (The sector skew makes marking partial tracks tricky.)
            if (ChunkAccess.FormattedLength == 140 * 1024) {
                Array.Clear(mIs525TrackReserved);
                int allocCount = 0;
                foreach (CPM_FileEntry.Extent ext in Extents) {
                    if (ext.Status == RESERVED_SPACE) {
                        for (int i = 0; i < ext.PtrsPerExtent; i++) {
                            ushort allocBlock = ext[i];
                            if (allocBlock != 0) {
                                allocCount++;
                                uint track = 3 + allocBlock / 4U;
                                if (track >= 35) {
                                    track -= 35;
                                }
                                mIs525TrackReserved[track] = true;
                            }
                        }
                    }
                }

                if (allocCount != 0) {
                    bool doWarnUnused =
                        AppHook.GetOptionBool(DAAppHook.WARN_MARKED_BUT_UNUSED, false);
                    string msg = allocCount + " allocation blocks are reserved";
                    if (doWarnUnused) {
                        Notes.AddW(msg);
                    } else {
                        Notes.AddI(msg);
                    }
                }
            }
        }

        // IFileSystem
        public IMultiPart? FindEmbeddedVolumes() {
            return null;
        }

        // IFileSystem
        public void Format(string volumeName, int volumeNum, bool makeBootable) {
            // TODO: makeBootable should create a user=31 file that spans the boot area on
            //   5.25" disks.  Including boot image is optional.
            throw new NotImplementedException();
        }

        /// <summary>
        /// Performs general checks on file-access calls, throwing exceptions when something
        /// is amiss.  An exception here generally indicates an error in the program calling
        /// into the library.
        /// </summary>
        /// <param name="op">Short string describing the operation.</param>
        /// <param name="ientry">File being accessed.</param>
        /// <param name="wantWrite">True if this operation might modify the file.</param>
        /// <param name="part">Which part of the file we want access to.  Pass "Unknown" to
        ///   match on any part.</param>
        /// <exception cref="IOException">Various.</exception>
        /// <exception cref="ArgumentException">Various.</exception>
        private void CheckFileAccess(string op, IFileEntry ientry, bool wantWrite, FilePart part) {
            if (mDisposed) {
                throw new ObjectDisposedException("Object was disposed");
            }
            if (!IsPreppedForFileAccess) {
                throw new IOException("Filesystem object not prepared for file access");
            }
            if (wantWrite && IsReadOnly) {
                throw new IOException("Filesystem is read-only");
            }
            if (ientry == IFileEntry.NO_ENTRY) {
                throw new ArgumentException("Cannot operate on NO_ENTRY");
            }
            if (ientry.IsDamaged) {
                throw new IOException("File '" + ientry.FileName +
                    "' is too damaged to access");
            }
            if (ientry.IsDubious && wantWrite) {
                throw new IOException("File '" + ientry.FileName +
                    "' is too damaged to modify");
            }
            CPM_FileEntry? entry = ientry as CPM_FileEntry;
            if (entry == null || entry.FileSystem != this) {
                if (entry != null && entry.FileSystem == null) {
                    // Invalid entry; could be a deleted file, or from before a raw-mode switch.
                    throw new IOException("File entry is invalid");
                } else {
                    throw new FileNotFoundException("File entry is not part of this filesystem");
                }
            }
            if (part == FilePart.RsrcFork) {
                throw new IOException("File does not have a resource fork");
            }
            if (!mOpenFiles.CheckOpenConflict(entry, wantWrite, FilePart.Unknown)) {
                throw new IOException("File is already open; cannot " + op);
            }
        }

        // IFileSystem
        public IFileEntry GetVolDirEntry() {
            return mVolDirEntry;
        }

        // IFileSystem
        public DiskFileStream OpenFile(IFileEntry ientry, FileAccessMode mode, FilePart part) {
            if (part == FilePart.RawData) {
                part = FilePart.DataFork;   // do this before is-file-open check
            }
            CheckFileAccess("open", ientry, mode != FileAccessMode.ReadOnly, part);
            if (mode != FileAccessMode.ReadOnly && mode != FileAccessMode.ReadWrite) {
                throw new ArgumentException("Unknown file access mode " + mode);
            }
            if (part != FilePart.DataFork) {
                throw new ArgumentException("Requested file part not found");
            }

            CPM_FileEntry entry = (CPM_FileEntry)ientry;
            CPM_FileDesc pfd = CPM_FileDesc.CreateFD(entry, mode, part);
            mOpenFiles.Add(this, entry, pfd);
            return pfd;
        }

        /// <summary>
        /// Closes a file, removing it from our list.  Do not call this directly -- this is
        /// called from the file descriptor Dispose() call.
        /// </summary>
        /// <param name="ifd">Descriptor to close.</param>
        /// <exception cref="IOException">File descriptor was already closed, or was opened
        ///   by a different filesystem.</exception>
        internal void CloseFile(DiskFileStream ifd) {
            CPM_FileDesc fd = (CPM_FileDesc)ifd;
            if (fd.FileSystem != this) {
                // Should be impossible, though it could be null if previous close invalidated it.
                if (fd.FileSystem == null) {
                    throw new IOException("Invalid file descriptor");
                } else {
                    throw new IOException("File descriptor was opened by a different filesystem");
                }
            }

            // Find the file record, searching by descriptor.
            if (!mOpenFiles.RemoveDescriptor(ifd)) {
                throw new IOException("Open file record not found: " + fd);
            }

            // Take the opportunity to flush the volume directory, in case the file was modified.
            FlushVolumeDir();
        }

        // IFileSystem
        public void CloseAll() {
            mOpenFiles.CloseAll();
        }

        // IFileSystem
        public IFileEntry CreateFile(IFileEntry dirEntry, string fileName, CreateMode mode) {
            throw new NotImplementedException();
        }

        // IFileSystem
        public void AddRsrcFork(IFileEntry entry) {
            throw new IOException("Filesystem does not support resource forks");
        }

        // IFileSystem
        public void MoveFile(IFileEntry entry, IFileEntry destDir, string newFileName) {
            throw new NotImplementedException();
        }

        // IFileSystem
        public void DeleteFile(IFileEntry entry) {
            throw new NotImplementedException();
        }
    }
}
