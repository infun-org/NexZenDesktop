//
// This file manually written from cef/include/internal/cef_types.h.
// C API name: cef_log_items_t.
//
namespace Xilium.CefGlue
{
    /// <summary>
    /// Items prepended to each CEF log line.
    /// </summary>
    [System.Flags]
    public enum CefLogItems
    {
        Default = 0,
        None = 1,
        ProcessId = 1 << 1,
        ThreadId = 1 << 2,
        Timestamp = 1 << 3,
        TickCount = 1 << 4,
    }
}
