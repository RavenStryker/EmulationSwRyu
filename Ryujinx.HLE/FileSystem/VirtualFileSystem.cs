using LibHac;
using LibHac.Common;
using LibHac.Common.Keys;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.Fs.Shim;
using LibHac.FsSrv;
using LibHac.FsSystem;
using LibHac.Ncm;
using LibHac.Spl;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.FileSystem.Content;
using Ryujinx.HLE.HOS;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using RightsId = LibHac.Fs.RightsId;

namespace Ryujinx.HLE.FileSystem
{
    public class VirtualFileSystem : IDisposable
    {
        public const string NandPath   = AppDataManager.DefaultNandDir;
        public const string SdCardPath = AppDataManager.DefaultSdcardDir;

        public static string SafeNandPath   = Path.Combine(NandPath, "safe");
        public static string SystemNandPath = Path.Combine(NandPath, "system");
        public static string UserNandPath   = Path.Combine(NandPath, "user");
        
        private static bool _isInitialized = false;

        public KeySet           KeySet   { get; private set; }
        public EmulatedGameCard GameCard { get; private set; }
        public EmulatedSdCard   SdCard   { get; private set; }

        public ModLoader ModLoader { get; private set; }

        private VirtualFileSystem()
        {
            ReloadKeySet();
            ModLoader = new ModLoader(); // Should only be created once
        }

        public Stream RomFs { get; private set; }

        public void LoadRomFs(string fileName)
        {
            RomFs = new FileStream(fileName, FileMode.Open, FileAccess.Read);
        }

        public void SetRomFs(Stream romfsStream)
        {
            RomFs?.Close();
            RomFs = romfsStream;
        }

        public string GetFullPath(string basePath, string fileName)
        {
            if (fileName.StartsWith("//"))
            {
                fileName = fileName.Substring(2);
            }
            else if (fileName.StartsWith('/'))
            {
                fileName = fileName.Substring(1);
            }
            else
            {
                return null;
            }

            string fullPath = Path.GetFullPath(Path.Combine(basePath, fileName));

            if (!fullPath.StartsWith(GetBasePath()))
            {
                return null;
            }

            return fullPath;
        }

        internal string GetBasePath() => AppDataManager.BaseDirPath;
        internal string GetSdCardPath() => MakeFullPath(SdCardPath);
        public string GetNandPath() => MakeFullPath(NandPath);

        public string GetFullPartitionPath(string partitionPath)
        {
            return MakeFullPath(partitionPath);
        }

        public string SwitchPathToSystemPath(string switchPath)
        {
            string[] parts = switchPath.Split(":");

            if (parts.Length != 2)
            {
                return null;
            }

            return GetFullPath(MakeFullPath(parts[0]), parts[1]);
        }

        public string SystemPathToSwitchPath(string systemPath)
        {
            string baseSystemPath = GetBasePath() + Path.DirectorySeparatorChar;

            if (systemPath.StartsWith(baseSystemPath))
            {
                string rawPath              = systemPath.Replace(baseSystemPath, "");
                int    firstSeparatorOffset = rawPath.IndexOf(Path.DirectorySeparatorChar);

                if (firstSeparatorOffset == -1)
                {
                    return $"{rawPath}:/";
                }

                string basePath = rawPath.Substring(0, firstSeparatorOffset);
                string fileName = rawPath.Substring(firstSeparatorOffset + 1);

                return $"{basePath}:/{fileName}";
            }
            return null;
        }

        private string MakeFullPath(string path, bool isDirectory = true)
        {
            // Handles Common Switch Content Paths
            switch (path)
            {
                case ContentPath.SdCard:
                case "@Sdcard":
                    path = SdCardPath;
                    break;
                case ContentPath.User:
                    path = UserNandPath;
                    break;
                case ContentPath.System:
                    path = SystemNandPath;
                    break;
                case ContentPath.SdCardContent:
                    path = Path.Combine(SdCardPath, "Nintendo", "Contents");
                    break;
                case ContentPath.UserContent:
                    path = Path.Combine(UserNandPath, "Contents");
                    break;
                case ContentPath.SystemContent:
                    path = Path.Combine(SystemNandPath, "Contents");
                    break;
            }

            string fullPath = Path.Combine(GetBasePath(), path);

            if (isDirectory)
            {
                if (!Directory.Exists(fullPath))
                {
                    Directory.CreateDirectory(fullPath);
                }
            }

            return fullPath;
        }

        public DriveInfo GetDrive()
        {
            return new DriveInfo(Path.GetPathRoot(GetBasePath()));
        }

        public void InitializeFsServer(LibHac.Horizon horizon, out HorizonClient fsServerClient)
        {
            LocalFileSystem serverBaseFs = new LocalFileSystem(GetBasePath());

            fsServerClient = horizon.CreatePrivilegedHorizonClient();
            var fsServer = new FileSystemServer(fsServerClient);

            DefaultFsServerObjects fsServerObjects = DefaultFsServerObjects.GetDefaultEmulatedCreators(serverBaseFs, KeySet, fsServer);

            GameCard = fsServerObjects.GameCard;
            SdCard = fsServerObjects.SdCard;

            SdCard.SetSdCardInsertionStatus(true);

            var fsServerConfig = new FileSystemServerConfig
            {
                DeviceOperator = fsServerObjects.DeviceOperator,
                ExternalKeySet = KeySet.ExternalKeySet,
                FsCreators = fsServerObjects.FsCreators
            };

            FileSystemServerInitializer.InitializeWithConfig(fsServerClient, fsServer, fsServerConfig);
        }

        public void ReloadKeySet()
        {
            KeySet ??= KeySet.CreateDefaultKeySet();

            string keyFile        = null;
            string titleKeyFile   = null;
            string consoleKeyFile = null;

            if (AppDataManager.Mode == AppDataManager.LaunchMode.UserProfile)
            {
                LoadSetAtPath(AppDataManager.KeysDirPathUser);
            }

            LoadSetAtPath(AppDataManager.KeysDirPath);

            void LoadSetAtPath(string basePath)
            {
                string localKeyFile        = Path.Combine(basePath, "prod.keys");
                string localTitleKeyFile   = Path.Combine(basePath, "title.keys");
                string localConsoleKeyFile = Path.Combine(basePath, "console.keys");

                if (File.Exists(localKeyFile))
                {
                    keyFile = localKeyFile;
                }

                if (File.Exists(localTitleKeyFile))
                {
                    titleKeyFile = localTitleKeyFile;
                }

                if (File.Exists(localConsoleKeyFile))
                {
                    consoleKeyFile = localConsoleKeyFile;
                }
            }

            ExternalKeyReader.ReadKeyFile(KeySet, keyFile, titleKeyFile, consoleKeyFile, null);
        }

        public void ImportTickets(IFileSystem fs)
        {
            foreach (DirectoryEntryEx ticketEntry in fs.EnumerateEntries("/", "*.tik"))
            {
                Result result = fs.OpenFile(out IFile ticketFile, ticketEntry.FullPath.ToU8Span(), OpenMode.Read);

                if (result.IsSuccess())
                {
                    Ticket ticket = new Ticket(ticketFile.AsStream());

                    if (ticket.TitleKeyType == TitleKeyType.Common)
                    {
                        KeySet.ExternalKeySet.Add(new RightsId(ticket.RightsId), new AccessKey(ticket.GetTitleKey(KeySet)));
                    }
                }
            }
        }

        // Save data created before we supported extra data in directory save data will not work properly if
        // given empty extra data. Luckily some of that extra data can be created using the data from the
        // save data indexer, which should be enough to check access permissions for user saves.
        // Every single save data's extra data will be checked and fixed if needed each time the emulator is opened.
        // Consider removing this at some point in the future when we don't need to worry about old saves.
        public static Result FixExtraData(HorizonClient hos)
        {
            Result rc = FixExtraDataInSpaceId(hos, SaveDataSpaceId.System);
            if (rc.IsFailure()) return rc;

            rc = FixExtraDataInSpaceId(hos, SaveDataSpaceId.User);
            if (rc.IsFailure()) return rc;

            rc = FixExtraDataInSpaceId(hos, SaveDataSpaceId.SdCache);
            if (rc.IsFailure()) return rc;

            return Result.Success;
        }

        private static Result FixExtraDataInSpaceId(HorizonClient hos, SaveDataSpaceId spaceId)
        {
            Span<SaveDataInfo> info = stackalloc SaveDataInfo[8];

            Result rc = hos.Fs.OpenSaveDataIterator(out var iterator, spaceId);
            if (rc.IsFailure()) return rc;

            while (true)
            {
                rc = iterator.ReadSaveDataInfo(out long count, info);
                if (rc.IsFailure()) return rc;

                if (count == 0)
                    return Result.Success;

                for (int i = 0; i < count; i++)
                {
                    rc = FixExtraData(out bool wasFixNeeded, hos, in info[i]);

                    if (rc.IsFailure())
                    {
                        Logger.Warning?.Print(LogClass.Application, $"Error {rc.ToStringWithName()} when fixing extra data for save 0x{info[i].SaveDataId:x} in the {spaceId} save data space");
                    }
                    else if (wasFixNeeded)
                    {
                        Logger.Info?.Print(LogClass.Application, $"Tried to rebuild extra data for save data 0x{info[i].SaveDataId:x} in the {spaceId} save data space");
                    }
                }
            }
        }

        private static Result FixExtraData(out bool wasFixNeeded, HorizonClient hos, in SaveDataInfo info)
        {
            wasFixNeeded = true;

            Result rc = hos.Fs.Impl.ReadSaveDataFileSystemExtraData(out SaveDataExtraData extraData, info.SpaceId,
                info.SaveDataId);
            if (rc.IsFailure()) return rc;

            // The extra data should have program ID or static save data ID set if it's valid.
            // We only try to fix the extra data if the info from the save data indexer has a program ID or static save data ID.
            bool canFixByProgramId = extraData.Attribute.ProgramId == ProgramId.InvalidId &&
                                       info.ProgramId != ProgramId.InvalidId;

            bool canFixBySaveDataId = extraData.Attribute.StaticSaveDataId == 0 && info.StaticSaveDataId != 0;

            if (!canFixByProgramId && !canFixBySaveDataId)
            {
                wasFixNeeded = false;
                return Result.Success;
            }

            // The save data attribute struct can be completely created from the save data info.
            extraData.Attribute.ProgramId = info.ProgramId;
            extraData.Attribute.UserId = info.UserId;
            extraData.Attribute.StaticSaveDataId = info.StaticSaveDataId;
            extraData.Attribute.Type = info.Type;
            extraData.Attribute.Rank = info.Rank;
            extraData.Attribute.Index = info.Index;

            // The rest of the extra data can't be created from the save data info.
            // On user saves the owner ID will almost certainly be the same as the program ID.
            if (info.Type != LibHac.Fs.SaveDataType.System)
            {
                extraData.OwnerId = info.ProgramId.Value;
            }

            // Make a mask for writing the entire extra data
            Unsafe.SkipInit(out SaveDataExtraData extraDataMask);
            SpanHelpers.AsByteSpan(ref extraDataMask).Fill(0xFF);

            return hos.Fs.Impl.WriteSaveDataFileSystemExtraData(info.SpaceId, info.SaveDataId, in extraData, in extraDataMask);
        }

        public void Unload()
        {
            RomFs?.Dispose();
        }

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                Unload();
            }
        }

        public static VirtualFileSystem CreateInstance()
        {
            if (_isInitialized)
            {
                throw new InvalidOperationException("VirtualFileSystem can only be instantiated once!");
            }

            _isInitialized = true;

            return new VirtualFileSystem();
        }
    }
}