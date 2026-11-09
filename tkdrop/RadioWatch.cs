using System;
using System.Runtime.InteropServices;

namespace TkDrop
{
    /// <summary>
    /// Direct Win32 check of whether a Bluetooth radio is present and powered on.
    /// Windows stops enumerating a radio that is switched off, so "no radio found" == off or absent.
    /// (32feet's BluetoothRadio.Mode does the same but leaks a radio handle per read, and
    /// BluetoothRadio.Default caches the first successful instance, so neither suits periodic polling.)
    /// </summary>
    public static class RadioWatch
    {
        /// <summary>true = on, false = off/absent, null = could not determine (API missing, e.g. not Windows).</summary>
        public static bool? IsRadioOn()
        {
            IntPtr find = IntPtr.Zero, radio = IntPtr.Zero;
            try
            {
                var p = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = Marshal.SizeOf(typeof(BLUETOOTH_FIND_RADIO_PARAMS)) };
                find = BluetoothFindFirstRadio(ref p, out radio);
                return find != IntPtr.Zero && radio != IntPtr.Zero;
            }
            catch (DllNotFoundException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
            catch { return null; }
            finally
            {
                if (radio != IntPtr.Zero) CloseHandle(radio);
                if (find != IntPtr.Zero) BluetoothFindRadioClose(find);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLUETOOTH_FIND_RADIO_PARAMS
        {
            public int dwSize;
        }

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp, out IntPtr phRadio);

        [DllImport("bthprops.cpl", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BluetoothFindRadioClose(IntPtr hFind);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
