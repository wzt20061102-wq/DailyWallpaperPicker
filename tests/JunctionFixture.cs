// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DailyWallpaperPicker
{
    // Creates a real mount-point reparse fixture without launching a shell or requiring symlink privilege.
    // Layout: https://learn.microsoft.com/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_reparse_data_buffer
    internal static class JunctionFixture
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
            IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input,
            int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);

        internal static void Create(string link, string target)
        {
            if (Directory.Exists(link) || File.Exists(link)) throw new IOException("Junction fixture path already exists.");
            string fullTarget = Path.GetFullPath(target);
            if (!Directory.Exists(fullTarget)) throw new IOException("Junction target must exist.");
            string substitute = @"\??\" + fullTarget;
            byte[] names = Encoding.Unicode.GetBytes(substitute + "\0" + fullTarget + "\0");
            byte[] data = new byte[16 + names.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(0xA0000003u), 0, data, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(checked((ushort)(8 + names.Length))), 0, data, 4, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(checked((ushort)(substitute.Length * 2))), 0, data, 10, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(checked((ushort)((substitute.Length + 1) * 2))), 0, data, 12, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(checked((ushort)(fullTarget.Length * 2))), 0, data, 14, 2);
            Buffer.BlockCopy(names, 0, data, 16, names.Length);
            Directory.CreateDirectory(link);
            using (var handle = CreateFile(link, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                int returned;
                if (!DeviceIoControl(handle, 0x000900A4, data, data.Length, IntPtr.Zero, 0, out returned, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }
}
