using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;

namespace Xilium.CefGlue.Avalonia.Platform
{
    /// <summary>
    /// Enhanced version of an Avalonia popup.
    /// Fixes some problems of the Avalonia Popup implementation.
    /// TODO: 
    /// - window shows a black border: https://github.com/AvaloniaUI/Avalonia/issues/2401
    /// </summary>
    internal class ExtendedAvaloniaPopup : global::Avalonia.Controls.Primitives.Popup
    {
        public ExtendedAvaloniaPopup()
        {
            IsLightDismissEnabled = false;
        }
        
        public new IAvaloniaList<Visual> VisualChildren => base.VisualChildren;
    }
}
