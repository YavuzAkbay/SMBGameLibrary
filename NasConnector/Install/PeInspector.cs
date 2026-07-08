using System;
using System.IO;

namespace NasConnector
{
    // Windows subsystem a PE (.exe) declares in its optional header. Games run as GUI
    // apps; console-subsystem exes are almost always tools/helpers (ffmpeg, python,
    // crash handlers), so the exe scorer uses this to demote them.
    public enum PeSubsystem
    {
        Unknown = 0,
        Gui = 1,
        Console = 2
    }

    // Minimal, dependency-free PE header reader. No P/Invoke, no NuGet — just reads the
    // handful of bytes needed to find the optional header's Subsystem field. Any malformed
    // file or IO error yields Unknown; this never throws into the caller.
    public static class PeInspector
    {
        // IMAGE_SUBSYSTEM values from winnt.h
        private const ushort IMAGE_SUBSYSTEM_WINDOWS_GUI = 2;
        private const ushort IMAGE_SUBSYSTEM_WINDOWS_CUI = 3;

        public static PeSubsystem GetSubsystem(string exePath)
        {
            try
            {
                using (var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite))
                using (var reader = new BinaryReader(fs))
                {
                    // DOS header: 'MZ' magic, then e_lfanew (offset to PE header) at 0x3C.
                    if (fs.Length < 0x40)
                        return PeSubsystem.Unknown;
                    if (reader.ReadUInt16() != 0x5A4D) // 'MZ'
                        return PeSubsystem.Unknown;

                    fs.Seek(0x3C, SeekOrigin.Begin);
                    uint peHeaderOffset = reader.ReadUInt32();

                    // PE signature 'PE\0\0' (4) + COFF file header (20) = 24 bytes,
                    // then the optional header begins. Subsystem sits at offset 0x44
                    // inside the optional header (same for PE32 and PE32+).
                    long subsystemOffset = peHeaderOffset + 4 + 20 + 0x44;
                    if (subsystemOffset + 2 > fs.Length)
                        return PeSubsystem.Unknown;

                    fs.Seek(peHeaderOffset, SeekOrigin.Begin);
                    if (reader.ReadUInt32() != 0x00004550) // 'PE\0\0'
                        return PeSubsystem.Unknown;

                    fs.Seek(subsystemOffset, SeekOrigin.Begin);
                    ushort subsystem = reader.ReadUInt16();

                    switch (subsystem)
                    {
                        case IMAGE_SUBSYSTEM_WINDOWS_GUI:
                            return PeSubsystem.Gui;
                        case IMAGE_SUBSYSTEM_WINDOWS_CUI:
                            return PeSubsystem.Console;
                        default:
                            return PeSubsystem.Unknown;
                    }
                }
            }
            catch (Exception)
            {
                return PeSubsystem.Unknown;
            }
        }
    }
}
