namespace Xilium.CefGlue
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using Xilium.CefGlue.Interop;
    
    /// <summary>
    /// Implemented by the client to observe preference changes and registered via
    /// CefPreferenceManager::AddPreferenceObserver. The methods of this class will
    /// be called on the browser process UI thread.
    /// </summary>
    public abstract unsafe partial class CefPreferenceObserver
    {
        private void on_preference_changed(cef_preference_observer_t* self, cef_string_t* name)
        {
            CheckSelf(self);
            throw new NotImplementedException(); // TODO: CefPreferenceObserver.OnPreferenceChanged
        }
        
        /// <summary>
        /// Called when a preference has changed. The new value can be retrieved using
        /// CefPreferenceManager::GetPreference.
        /// </summary>
        // protected abstract void OnPreferenceChanged(cef_string_t* name);
        
    }
}
