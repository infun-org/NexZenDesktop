using Xilium.CefGlue.Avalonia;

namespace NexZen.Desktop.Helpers;

/// <summary>
/// Unity WebGL (itch.io) decodes clips as AAC. CEF Linux minimal has no AAC
/// decoder, so decodeAudioData throws and Unity retries until the process OOMs.
/// Patch decodeAudioData in every frame to return silence on failure.
/// </summary>
internal static class WebAudioDecodeShim
{
    internal const string Script =
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

    public static void Attach(AvaloniaCefBrowser browser)
    {
        browser.JavascriptContextCreated += (_, e) => Inject(e.Frame);
        browser.LoadStart += (_, e) => Inject(e.Frame);
        browser.LoadEnd += (_, e) => Inject(e.Frame);
    }

    private static void Inject(Xilium.CefGlue.CefFrame? frame)
    {
        if (frame == null || !frame.IsValid)
            return;
        try
        {
            frame.ExecuteJavaScript(Script, frame.Url ?? "about:blank", 0);
        }
        catch
        {
        }
    }
}
