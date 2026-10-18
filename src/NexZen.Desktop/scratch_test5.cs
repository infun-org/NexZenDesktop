using Xilium.CefGlue.Avalonia;
public class ScratchTest5 {
    public void Test() {
        var b = new AvaloniaCefBrowser();
        b.TitleChanged += (s, e) => {
            var t = e;
        };
    }
}
