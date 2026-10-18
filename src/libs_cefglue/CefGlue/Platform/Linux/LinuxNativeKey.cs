using System;
using System.Runtime.InteropServices;

namespace Xilium.CefGlue
{
    /// <summary>
    /// CEF 152 on Linux treats native_key_code as an X keycode and aborts
    /// the browser process when that code is 0.
    /// </summary>
    public static class LinuxNativeKey
    {
        private static IntPtr _display;

        public static int FromVirtualKey(int windowsKeyCode)
        {
            var keysym = KeysymFromVirtualKey(windowsKeyCode);
            return keysym == 0 ? 0 : Keycode(keysym);
        }

        public static int FromCharacter(char character)
        {
            if (character >= 'A' && character <= 'Z')
                return Keycode((uint)(character - 'A' + 'a'));
            if (character >= 'a' && character <= 'z' || character >= '0' && character <= '9' || character == ' ')
                return Keycode(character);
            return 0;
        }

        private static uint KeysymFromVirtualKey(int key)
        {
            if (key >= 0x41 && key <= 0x5A)
                return (uint)('a' + (key - 0x41));
            if (key >= 0x30 && key <= 0x39)
                return (uint)('0' + (key - 0x30));

            return key switch
            {
                0x20 => 0x20,
                0x0D => 0xff0d,
                0x08 => 0xff08,
                0x09 => 0xff09,
                0x1B => 0xff1b,
                0x2E => 0xffff,
                0x25 => 0xff51,
                0x26 => 0xff52,
                0x27 => 0xff53,
                0x28 => 0xff54,
                0x10 or 0xA0 => 0xffe1,
                0xA1 => 0xffe2,
                0x11 or 0xA2 => 0xffe3,
                0xA3 => 0xffe4,
                0x12 or 0xA4 => 0xffe9,
                0xA5 => 0xffea,
                _ => 0,
            };
        }

        private static int Keycode(uint keysym)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return 0;
            if (_display == IntPtr.Zero)
                _display = XOpenDisplay(IntPtr.Zero);
            if (_display == IntPtr.Zero)
                return 0;
            return XKeysymToKeycode(_display, keysym);
        }

        [DllImport("libX11")]
        private static extern IntPtr XOpenDisplay(IntPtr display);

        [DllImport("libX11")]
        private static extern byte XKeysymToKeycode(IntPtr display, uint keysym);
    }
}
