﻿using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Shader.Cache.Definition;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Ryujinx.Graphics.Gpu.Shader.Cache
{
    static class CacheMigration
    {
        public static bool NeedHashRecompute(ulong version, out ulong newVersion)
        {
            const ulong TargetBrokenVersion = 1717;
            const ulong TargetFixedVersion = 1759;

            newVersion = TargetFixedVersion;

            if (version == TargetBrokenVersion)
            {
                return true;
            }

            return false;
        }

        private static void MoveEntry(ZipArchive archive, Hash128 oldKey, Hash128 newKey)
        {
            ZipArchiveEntry oldGuestEntry = archive.GetEntry($"{oldKey}");

            if (oldGuestEntry != null)
            {
                ZipArchiveEntry newGuestEntry = archive.CreateEntry($"{newKey}");

                using (Stream oldStream = oldGuestEntry.Open())
                using (Stream newStream = newGuestEntry.Open())
                {
                    oldStream.CopyTo(newStream);
                }

                oldGuestEntry.Delete();
            }
        }

        private static void RecomputeHashes(string guestBaseCacheDirectory, string hostBaseCacheDirectory, CacheGraphicsApi graphicsApi, CacheHashType hashType, ulong newVersion)
        {
            string guestManifestPath = CacheHelper.GetManifestPath(guestBaseCacheDirectory);
            string hostManifestPath = CacheHelper.GetManifestPath(hostBaseCacheDirectory);

            if (CacheHelper.TryReadManifestFile(guestManifestPath, CacheGraphicsApi.Guest, hashType, out _, out HashSet<Hash128> guestEntries))
            {
                CacheHelper.TryReadManifestFile(hostManifestPath, graphicsApi, hashType, out _, out HashSet<Hash128> hostEntries);

                Logger.Info?.Print(LogClass.Gpu, "Shader cache hashes need to be recomputed, performing migration...");

                string guestArchivePath = CacheHelper.GetArchivePath(guestBaseCacheDirectory);
                string hostArchivePath = CacheHelper.GetArchivePath(hostBaseCacheDirectory);

                ZipArchive guestArchive = ZipFile.Open(guestArchivePath, ZipArchiveMode.Update);
                ZipArchive hostArchive = ZipFile.Open(hostArchivePath, ZipArchiveMode.Update);

                CacheHelper.EnsureArchiveUpToDate(guestBaseCacheDirectory, guestArchive, guestEntries);
                CacheHelper.EnsureArchiveUpToDate(hostBaseCacheDirectory, hostArchive, hostEntries);

                int programIndex = 0;

                HashSet<Hash128> newEntries = new HashSet<Hash128>();

                foreach (Hash128 oldHash in guestEntries)
                {
                    byte[] guestProgram = CacheHelper.ReadFromArchive(guestArchive, oldHash);

                    Logger.Info?.Print(LogClass.Gpu, $"Migrating shader {oldHash} ({programIndex + 1} / {guestEntries.Count})");

                    if (guestProgram != null)
                    {
                        ReadOnlySpan<byte> guestProgramReadOnlySpan = guestProgram;

                        ReadOnlySpan<GuestShaderCacheEntry> cachedShaderEntries = GuestShaderCacheEntry.Parse(ref guestProgramReadOnlySpan, out GuestShaderCacheHeader fileHeader);

                        TransformFeedbackDescriptor[] tfd = CacheHelper.ReadTransformationFeedbackInformations(ref guestProgramReadOnlySpan, fileHeader);

                        Hash128 newHash = CacheHelper.ComputeGuestHashFromCache(cachedShaderEntries, tfd);

                        if (newHash != oldHash)
                        {
                            MoveEntry(guestArchive, oldHash, newHash);
                            MoveEntry(hostArchive, oldHash, newHash);
                        }
                        else
                        {
                            Logger.Warning?.Print(LogClass.Gpu, $"Same hashes for shader {oldHash}");
                        }

                        newEntries.Add(newHash);
                    }

                    programIndex++;
                }

                byte[] newGuestManifestContent = CacheHelper.ComputeManifest(newVersion, CacheGraphicsApi.Guest, hashType, newEntries);
                byte[] newHostManifestContent = CacheHelper.ComputeManifest(newVersion, graphicsApi, hashType, newEntries);

                File.WriteAllBytes(guestManifestPath, newGuestManifestContent);
                File.WriteAllBytes(hostManifestPath, newHostManifestContent);

                guestArchive.Dispose();
                hostArchive.Dispose();
            }
        }

        public static void Run(string baseCacheDirectory, CacheGraphicsApi graphicsApi, CacheHashType hashType, string shaderProvider)
        {
            string guestBaseCacheDirectory = CacheHelper.GenerateCachePath(baseCacheDirectory, CacheGraphicsApi.Guest, "", "program");
            string hostBaseCacheDirectory = CacheHelper.GenerateCachePath(baseCacheDirectory, graphicsApi, shaderProvider, "host");

            if (CacheHelper.TryReadManifestHeader(CacheHelper.GetManifestPath(guestBaseCacheDirectory), out CacheManifestHeader header))
            {
                if (NeedHashRecompute(header.Version, out ulong newVersion))
                {
                    RecomputeHashes(guestBaseCacheDirectory, hostBaseCacheDirectory, graphicsApi, hashType, newVersion);
                }
            }
        }
    }
}
