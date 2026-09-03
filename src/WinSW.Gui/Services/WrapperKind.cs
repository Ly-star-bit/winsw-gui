using System;
using System.IO;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// Which of the published wrapper builds a given executable is, so an upgrade fetches
    /// the matching asset: the .NET Framework build, or a native x64 / x86 / arm64 one.
    /// </summary>
    public static class WrapperKind
    {
        /// <summary>
        /// Native single-file builds carry the runtime and are tens of megabytes; the
        /// .NET Framework build is a few hundred kilobytes. Size is a reliable tell.
        /// </summary>
        private const long FrameworkBuildMaxBytes = 4 * 1024 * 1024;

        /// <returns>The release asset name, e.g. <c>WinSW-x64.exe</c>, or null if unrecognised.</returns>
        public static string? ReleaseAssetFor(string executablePath)
        {
            try
            {
                var file = new FileInfo(executablePath);
                if (!file.Exists)
                {
                    return null;
                }

                if (file.Length < FrameworkBuildMaxBytes)
                {
                    // Upstream's own name for the .NET Framework build. It stays net461 even
                    // though this repository now builds net462: this names a file to download
                    // from their releases, not the one built here.
                    return "WinSW-net461.exe";
                }

                return ReadMachine(executablePath) switch
                {
                    0x8664 => "WinSW-x64.exe",
                    0x014C => "WinSW-x86.exe",
                    0xAA64 => "WinSW-arm64.exe",
                    _ => null,
                };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>IMAGE_FILE_HEADER.Machine, read straight from the PE header.</summary>
        private static int ReadMachine(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);

            if (reader.ReadUInt16() != 0x5A4D)
            {
                return 0;
            }

            stream.Position = 0x3C;
            int peOffset = reader.ReadInt32();
            stream.Position = peOffset;

            if (reader.ReadUInt32() != 0x00004550)
            {
                return 0;
            }

            return reader.ReadUInt16();
        }
    }
}
