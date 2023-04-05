﻿using LibHac.Loader;
using LibHac.Ns;
using Ryujinx.Common.Logging;
using Ryujinx.Cpu;
using Ryujinx.HLE.HOS.SystemState;
using Ryujinx.HLE.Loaders.Processes.Extensions;
using Ryujinx.Horizon.Common;
using System.Linq;

namespace Ryujinx.HLE.Loaders.Processes
{
    public struct ProcessResult
    {
        public static ProcessResult Failed => new(null, new ApplicationControlProperty(), false, false, null, 0, 0, 0, TitleLanguage.AmericanEnglish);

        private readonly byte _mainThreadPriority;
        private readonly uint _mainThreadStackSize;

        public readonly IDiskCacheLoadState DiskCacheLoadState;

        public readonly MetaLoader                 MetaLoader;
        public readonly ApplicationControlProperty ApplicationControlProperties;

        public readonly ulong  ProcessId;
        public readonly string Name;
        public readonly string DisplayVersion;
        public readonly ulong  ProgramId;
        public readonly string ProgramIdText;
        public readonly bool   Is64Bit;
        public readonly bool   DiskCacheEnabled;
        public readonly bool   AllowCodeMemoryForJit;

        public ProcessResult(
            MetaLoader                 metaLoader,
            ApplicationControlProperty applicationControlProperties,
            bool                       diskCacheEnabled,
            bool                       allowCodeMemoryForJit,
            IDiskCacheLoadState        diskCacheLoadState,
            ulong                      pid,
            byte                       mainThreadPriority,
            uint                       mainThreadStackSize,
            TitleLanguage              titleLanguage)
        {
            _mainThreadPriority  = mainThreadPriority;
            _mainThreadStackSize = mainThreadStackSize;

            DiskCacheLoadState = diskCacheLoadState;
            ProcessId          = pid;

            MetaLoader                   = metaLoader;
            ApplicationControlProperties = applicationControlProperties;

            if (metaLoader is not null)
            {
                ulong programId = metaLoader.GetProgramId();

                if (ApplicationControlProperties.Title.ItemsRo.Length > 0)
                {
                    var langIndex = ApplicationControlProperties.Title.ItemsRo.Length > (int)titleLanguage ? (int)titleLanguage : 0;

                    Name = ApplicationControlProperties.Title[langIndex].NameString.ToString();

                    if (string.IsNullOrWhiteSpace(Name))
                    {
                        Name = ApplicationControlProperties.Title.ItemsRo.ToArray().FirstOrDefault(x => x.Name[0] != 0).NameString.ToString();
                    }
                }
                else
                {
                    Name = metaLoader.GetProgramName();
                }

                DisplayVersion = ApplicationControlProperties.DisplayVersionString.ToString();
                ProgramId      = programId;
                ProgramIdText  = $"{programId:x16}";
                Is64Bit        = metaLoader.IsProgram64Bit();
            }

            DiskCacheEnabled      = diskCacheEnabled;
            AllowCodeMemoryForJit = allowCodeMemoryForJit;
        }

        public bool Start(Switch device)
        {
            device.Configuration.ContentManager.LoadEntries(device);

            Result result = device.System.KernelContext.Processes[ProcessId].Start(_mainThreadPriority, _mainThreadStackSize);
            if (result != Result.Success)
            {
                Logger.Error?.Print(LogClass.Loader, $"Process start returned error \"{result}\".");

                return false;
            }

            // TODO: LibHac npdm currently doesn't support version field.
            string version = ProgramId > 0x0100000000007FFF ? DisplayVersion : device.System.ContentManager.GetCurrentFirmwareVersion().VersionString;

            Logger.Info?.Print(LogClass.Loader, $"Application Loaded: {Name} v{version} [{ProgramIdText}] [{(Is64Bit ? "64-bit" : "32-bit")}]");

            return true;
        }
    }
}