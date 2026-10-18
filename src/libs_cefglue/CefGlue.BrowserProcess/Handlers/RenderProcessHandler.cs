#nullable enable annotations
﻿using System;
using Xilium.CefGlue.BrowserProcess.JavascriptExecution;
using Xilium.CefGlue.BrowserProcess.ObjectBinding;
using Xilium.CefGlue.Common.Shared.Helpers;
using Xilium.CefGlue.Common.Shared.RendererProcessCommunication;

namespace Xilium.CefGlue.BrowserProcess.Handlers
{
    internal class RenderProcessHandler : CefRenderProcessHandler
    {
        private CefBrowser _browser;
        private string _crashPipeName;
        private JavascriptExecutionEngineRenderSide _javascriptExecutionEngine;
        private JavascriptToNativeDispatcherRenderSide _javascriptToNativeDispatcher;
        
        private readonly MessageDispatcher _messageDispatcher = new MessageDispatcher();

        public RenderProcessHandler()
        {
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        }

        protected override void OnWebKitInitialized()
        {
            base.OnWebKitInitialized();
            _javascriptExecutionEngine = new JavascriptExecutionEngineRenderSide(_messageDispatcher);
            _javascriptToNativeDispatcher = new JavascriptToNativeDispatcherRenderSide(_messageDispatcher);
        }

        protected override bool OnProcessMessageReceived(CefBrowser browser, CefFrame frame, CefProcessId sourceProcess, CefProcessMessage message)
        {
            using (message)
            
            WithErrorHandling(() =>
            {
                using (CefObjectTracker.StartTracking())
                {
                    _messageDispatcher.DispatchMessage(browser, frame, sourceProcess, message);
                }
            }, frame);
            
            return base.OnProcessMessageReceived(browser, frame, sourceProcess, message);
        }

        protected override void OnContextCreated(CefBrowser browser, CefFrame frame, CefV8Context context)
        {
            WithErrorHandling(() =>
            {
                using (CefObjectTracker.StartTracking())
                {
                    TryInstallWebAudioDecodeShim(context, frame);
                    base.OnContextCreated(browser, frame, context);
                    _javascriptToNativeDispatcher.HandleContextCreated(context, frame.IsMain);

                    var message = new Messages.JsContextCreated();
                    var cefMessage = message.ToCefProcessMessage();
                    frame.SendProcessMessage(CefProcessId.Browser, cefMessage);
                }
            }, frame);
        }

        protected override void OnContextReleased(CefBrowser browser, CefFrame frame, CefV8Context context)
        {
            WithErrorHandling(() =>
            {
                using (CefObjectTracker.StartTracking())
                {
                    _javascriptToNativeDispatcher.HandleContextReleased(context, frame.IsMain);
                    base.OnContextReleased(browser, frame, context);

                    var message = new Messages.JsContextReleased();
                    var cefMessage = message.ToCefProcessMessage();
                    frame.SendProcessMessage(CefProcessId.Browser, cefMessage);
                }
            }, frame);
        }

        protected override void OnUncaughtException(CefBrowser browser, CefFrame frame, CefV8Context context, CefV8Exception exception, CefV8StackTrace stackTrace)
        {
            WithErrorHandling(() =>
            {
                using (CefObjectTracker.StartTracking())
                {
                    var frames = new Messages.JsStackFrame[stackTrace.FrameCount];
                    for (var i = 0; i < stackTrace.FrameCount; i++)
                    {
                        var stackFrame = stackTrace.GetFrame(i);
                        frames[i] = new Messages.JsStackFrame()
                        {
                            FunctionName = stackFrame.FunctionName,
                            ScriptNameOrSourceUrl = stackFrame.ScriptNameOrSourceUrl,
                            LineNumber = stackFrame.LineNumber,
                            Column = stackFrame.Column
                        };
                    }

                    var message = new Messages.JsUncaughtException()
                    {
                        Message = exception.Message,
                        StackFrames = frames
                    };

                    var cefMessage = message.ToCefProcessMessage();
                    frame.SendProcessMessage(CefProcessId.Browser, cefMessage);

                    base.OnUncaughtException(browser, frame, context, exception, stackTrace);
                }
            }, frame);
        }


        protected override void OnBrowserCreated(CefBrowser browser, CefDictionaryValue? extraInfo)
        {
            _crashPipeName = extraInfo?.GetString(Constants.CrashPipeNameKey);
            _browser = browser;
            base.OnBrowserCreated(browser, extraInfo);
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            CefFrame frame = null;
            var exception = (Exception)e.ExceptionObject;
            try
            {
                frame = _browser?.FrameCount > 0 ? _browser?.GetMainFrame() : null;
            } 
            catch
            {
                // ignore
            }
            HandleException(exception, frame);
        }

        // Unity WebGL encodes clips as AAC. CEF Linux minimal has no AAC decoder, so
        // decodeAudioData throws EncodingError and Unity retries every frame until OOM.
        // Return a short silent buffer so the clip "loads" and the retry loop stops.
        private const string WebAudioDecodeShimScript =
            "(function(){if(globalThis.__nexzenDecodeAudioShim)return;globalThis.__nexzenDecodeAudioShim=1;" +
            "function silent(ctx){var sr=44100;try{sr=ctx.sampleRate||44100;}catch(e){}return ctx.createBuffer(2,Math.max(1,sr>>2),sr);}" +
            "function install(proto){if(!proto||typeof proto.decodeAudioData!=='function'||proto.decodeAudioData.__nexzen)return;" +
            "var orig=proto.decodeAudioData;" +
            "function shim(data,ok,err){var ctx=this,p;try{p=orig.call(ctx,data);}catch(e){var b=silent(ctx);if(typeof ok==='function')ok(b);return Promise.resolve(b);}" +
            "var w=Promise.resolve(p).then(function(b){return b;},function(){return silent(ctx);});" +
            "if(typeof ok==='function')w.then(function(b){ok(b);});return w;}" +
            "shim.__nexzen=1;try{Object.defineProperty(proto,'decodeAudioData',{value:shim,writable:true,configurable:true});}catch(e){proto.decodeAudioData=shim;}}" +
            "install(globalThis.BaseAudioContext&&globalThis.BaseAudioContext.prototype);" +
            "install(globalThis.AudioContext&&globalThis.AudioContext.prototype);" +
            "install(globalThis.webkitAudioContext&&globalThis.webkitAudioContext.prototype);" +
            "install(globalThis.OfflineAudioContext&&globalThis.OfflineAudioContext.prototype);" +
            "try{window.addEventListener('unhandledrejection',function(e){var s=e&&e.reason?(e.reason.message||String(e.reason)):'';if(s.indexOf('decode audio')!==-1||s.indexOf('EncodingError')!==-1){e.preventDefault();e.stopImmediatePropagation();}},true);" +
            "var _alert=window.alert;window.alert=function(msg){var s=String(msg||'');if(s.indexOf('decode audio')!==-1||s.indexOf('EncodingError')!==-1)return;return _alert.apply(this,arguments);};}catch(e){}})();";

        private static void TryInstallWebAudioDecodeShim(CefV8Context context, CefFrame frame)
        {
            if (context == null || !context.IsValid)
                return;
            var entered = false;
            try
            {
                entered = context.Enter();
                var url = frame != null && frame.IsValid ? frame.Url : "about:blank";
                context.TryEval(WebAudioDecodeShimScript, string.IsNullOrEmpty(url) ? "about:blank" : url, 0, out var retval, out var exception);
                retval?.Dispose();
                exception?.Dispose();
            }
            catch
            {
            }
            finally
            {
                if (entered)
                {
                    try { context.Exit(); } catch { }
                }
            }
        }

        private void WithErrorHandling(Action action, CefFrame frame)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                HandleException(e, frame);
            }
        }

        private void HandleException(Exception e, CefFrame frame)
        {
            if (frame != null)
            {
                try
                {
                    using (CefObjectTracker.StartTracking())
                    {
                        var exceptionMessage = new Messages.UnhandledException()
                        {
                            ExceptionType = e.GetType().FullName,
                            Message = e.Message,
                            StackTrace = e.StackTrace
                        };
                        var message = exceptionMessage.ToCefProcessMessage();
                        frame.SendProcessMessage(CefProcessId.Browser, message);
                    }
                    return;
                }
                catch
                {
                    // ignore, lets try an alternative method using the crash pipe
                }
            }
            SendExceptionToParentProcess(e);
        }

        /// <summary>
        /// Alternative way to send the exception to the parent process using a side named pipe.
        /// </summary>
        /// <param name="e"></param>
        private void SendExceptionToParentProcess(Exception e)
        {
            try
            {
                if (string.IsNullOrEmpty(_crashPipeName))
                {
                    return; // not initialized yet
                }

                var serializableException = new SerializableException()
                {
                    ExceptionType = e.GetType().ToString(),
                    Message = e.Message,
                    StackTrace = e.StackTrace
                };

                
                using (var pipeClient = new PipeClient(_crashPipeName))
                {
                    pipeClient.SendMessage(serializableException.SerializeToString());
                }
            }
            catch
            {
                // failed at failing
            }
        }
    }
}
