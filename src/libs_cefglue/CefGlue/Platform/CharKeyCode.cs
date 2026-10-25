namespace Xilium.CefGlue
{
    /// <summary>
    /// The Windows virtual key that types a character, for Char key events. Using the
    /// character itself as the key code clashes with real keys (e.g. 'g' = 103 = VK_NUMPAD7,
    /// '.' = 46 = VK_DELETE), so pages saw keypress with a wrong key. Non-ASCII characters
    /// keep their own value, as before.
    /// </summary>
    public static class CharKeyCode
    {
        public static int WindowsKeyCode(char c)
        {
            if (c >= 'a' && c <= 'z')
                return c - 'a' + 'A';                 // VK_A..VK_Z
            if (c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == ' ')
                return c;                              // VK_A..VK_Z, VK_0..VK_9, VK_SPACE
            switch (c)
            {
                case ')': return '0';
                case '!': return '1';
                case '@': return '2';
                case '#': return '3';
                case '$': return '4';
                case '%': return '5';
                case '^': return '6';
                case '&': return '7';
                case '*': return '8';
                case '(': return '9';
                case ';': case ':': return 0xBA;       // VK_OEM_1
                case '=': case '+': return 0xBB;       // VK_OEM_PLUS
                case ',': case '<': return 0xBC;       // VK_OEM_COMMA
                case '-': case '_': return 0xBD;       // VK_OEM_MINUS
                case '.': case '>': return 0xBE;       // VK_OEM_PERIOD
                case '/': case '?': return 0xBF;       // VK_OEM_2
                case '`': case '~': return 0xC0;       // VK_OEM_3
                case '[': case '{': return 0xDB;       // VK_OEM_4
                case '\\': case '|': return 0xDC;      // VK_OEM_5
                case ']': case '}': return 0xDD;       // VK_OEM_6
                case '\'': case '"': return 0xDE;      // VK_OEM_7
                case '\r': return 0x0D;
                case '\t': return 0x09;
            }
            return c;
        }

        /// <summary>True for characters typed with Shift on a US layout (upper case, shifted symbols).</summary>
        public static bool NeedsShift(char c)
            => c >= 'A' && c <= 'Z' || "~!@#$%^&*()_+{}|:\"<>?".IndexOf(c) >= 0;

        public static CefEventFlags Modifiers(char c)
            => NeedsShift(c) ? CefEventFlags.ShiftDown : CefEventFlags.None;
    }
}
