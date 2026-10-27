// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License").
//  You may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//  See the License for the specific language governing permissions and
//  limitations under the License.
//
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Boutquin.Storage.Infrastructure.KeyValueStore;

internal static partial class DurableFile
{
    // FILE_RENAME_INFO follows native pointer alignment: FileName begins at byte 20 on
    // 64-bit Windows and byte 12 on 32-bit Windows. Marshal supplies that ABI layout.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowsRenameInformation
    {
        public uint Flags;
        public nint RootDirectory;
        public uint FileNameLength;
        public char FileName;
    }

    internal static byte[] BuildWindowsRenameInformation(string target)
    {
        var nameBytes = checked(target.Length * sizeof(char));
        var nameOffset = (int)Marshal.OffsetOf<WindowsRenameInformation>(nameof(WindowsRenameInformation.FileName));
        var bytes = new byte[checked(Marshal.SizeOf<WindowsRenameInformation>() + nameBytes)];
        var header = new WindowsRenameInformation
        {
            Flags = 0x00000001 | 0x00000002, // REPLACE_IF_EXISTS | POSIX_SEMANTICS
            RootDirectory = 0,
            FileNameLength = checked((uint)nameBytes),
            FileName = '\0',
        };
        MemoryMarshal.Write(bytes, in header);
        // Windows names are UTF-16 code units. Copy them without an encoding fallback that
        // could replace an unpaired surrogate and address a different filename.
        MemoryMarshal.AsBytes(target.AsSpan()).CopyTo(bytes.AsSpan(nameOffset));
        return bytes;
    }

    [SupportedOSPlatform("windows")]
    private static unsafe void AtomicReplaceWindows(string source, string target)
    {
        const uint DeleteAccess = 0x00010000;
        const uint OpenExisting = 3;
        const uint NormalAttributes = 0x00000080;
        const int FileRenameInfoEx = 22;

        using var handle = NativeCreateFile(ToExtendedWindowsPath(Path.GetFullPath(source)), DeleteAccess,
            FileShare.ReadWrite | FileShare.Delete, 0, OpenExisting, NormalAttributes, 0);
        if (handle.IsInvalid)
        {
            throw WindowsRenameError(Marshal.GetLastPInvokeError(), source, target);
        }

        var information = BuildWindowsRenameInformation(ToExtendedWindowsPath(target));
        fixed (byte* buffer = information)
        {
            if (!NativeSetFileInformationByHandle(handle, FileRenameInfoEx, buffer, checked((uint)information.Length)))
            {
                throw WindowsRenameError(Marshal.GetLastPInvokeError(), source, target);
            }
        }
    }

    private static string ToExtendedWindowsPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
    }

    private static Exception WindowsRenameError(int error, string source, string target)
    {
        var detail = new Win32Exception(error);
        var message = $"Cannot atomically rename '{source}' to '{target}' (Win32 error {error}): {detail.Message}";
        return error switch
        {
            2 => new FileNotFoundException(message, source, detail),
            3 => new DirectoryNotFoundException(message, detail),
            5 => new UnauthorizedAccessException(message, detail),
            206 => new PathTooLongException(message, detail),
            _ => new IOException(message, unchecked((int)(0x80070000u | (uint)error))),
        };
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle NativeCreateFile(string fileName, uint desiredAccess, FileShare shareMode,
        nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool NativeSetFileInformationByHandle(
        SafeFileHandle file, int informationClass, byte* information, uint bufferSize);
}
