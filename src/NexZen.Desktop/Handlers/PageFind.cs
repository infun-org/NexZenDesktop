using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NexZen.Desktop.Controls;
using Xilium.CefGlue;
using Xilium.CefGlue.Avalonia;
using Xilium.CefGlue.Common.Handlers;

namespace NexZen.Desktop.Handlers;

/// <summary>
/// Find bar for a windowless page. The bar is a topmost popup because the CEF
/// bitmap covers controls placed inside the page host.
/// </summary>
public sealed class PageFindUi
{
    private readonly PageFind _find = new();
    private readonly FindBar _bar = new();

    public Control Anchor { get; }
    public Popup Popup { get; }
    public bool IsOpen => Popup.IsOpen;
    public bool IsPin => _find.IsPin;

    public PageFindUi()
    {
        Anchor = new Control
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Width = 0,
            Height = 0,
            Margin = new Thickness(0, 16, 20, 0),
            IsHitTestVisible = false
        };
        Popup = new Popup
        {
            PlacementTarget = Anchor,
            Placement = PlacementMode.BottomEdgeAlignedRight,
            Topmost = true,
            IsLightDismissEnabled = false,
            Child = _bar
        };

        _bar.QueryChanged += (_, text) =>
        {
            _bar.ClearMatch();
            _find.SetQuery(text);
        };
        _bar.NextRequested += (_, _) => _find.Next();
        _bar.PreviousRequested += (_, _) => _find.Previous();
        _bar.CloseRequested += (_, _) => Close();
        _find.Result += (count, ordinal) =>
        {
            if (Popup.IsOpen)
                _bar.ShowMatch(ordinal, count);
        };
    }

    public void Mount(Panel panel)
    {
        panel.Children.Add(Anchor);
        panel.Children.Add(Popup);
    }

    public void Attach(AvaloniaCefBrowser browser) => _find.Attach(browser);

    public bool Targets(AvaloniaCefBrowser? browser) => _find.Targets(browser);

    public bool ContainsFocus(InputElement? focused)
    {
        var visual = focused as Visual;
        while (visual != null)
        {
            if (ReferenceEquals(visual, _bar))
                return true;
            visual = visual.GetVisualParent();
        }

        return false;
    }

    public void Open(AvaloniaCefBrowser browser, bool pinned = false)
    {
        _find.Use(browser, pinned);
        Popup.IsOpen = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (Popup.IsOpen)
                _bar.FocusQuery();
        }, DispatcherPriority.Background);
    }

    public void RetargetOpenMain(AvaloniaCefBrowser? browser)
    {
        if (!Popup.IsOpen || _find.IsPin)
            return;
        if (browser == null)
        {
            Close();
            return;
        }

        _find.Use(browser, false);
    }

    public void ForgetTarget() => _find.Release();

    public void Close()
    {
        Popup.IsOpen = false;
        _find.Release();
        _bar.ClearMatch();
    }

    public bool HandleChromeKey(KeyEventArgs e, AvaloniaCefBrowser? page, InputElement? focused, bool pinned = false)
    {
        if (e.Key == Key.Escape)
        {
            if (!Popup.IsOpen)
                return false;
            if (focused is TextBox && !ContainsFocus(focused))
                return false;
            e.Handled = true;
            Close();
            return true;
        }

        if (!IsFindChord(e))
            return false;

        e.Handled = true;
        if (Popup.IsOpen && ContainsFocus(focused))
        {
            _bar.FocusQuery();
            return true;
        }

        if (page == null)
            return true;

        Open(page, pinned);
        return true;
    }

    public static bool IsFindChord(KeyEventArgs e)
    {
        if (e.Key != Key.F)
            return false;
        var mods = e.KeyModifiers;
        return mods.HasFlag(KeyModifiers.Control)
               && !mods.HasFlag(KeyModifiers.Shift)
               && !mods.HasFlag(KeyModifiers.Alt)
               && !mods.HasFlag(KeyModifiers.Meta);
    }
}

sealed class PageFind
{
    private AvaloniaCefBrowser? _browser;
    private string _text = "";
    private int _seenIdentifier;

    public bool IsPin { get; private set; }
    public event Action<int, int>? Result;

    public void Attach(AvaloniaCefBrowser browser)
    {
        browser.FindHandler = new PageFindHandler((identifier, count, ordinal) =>
            OnResult(browser, identifier, count, ordinal));
    }

    public bool Targets(AvaloniaCefBrowser? browser) => ReferenceEquals(_browser, browser);

    public void Use(AvaloniaCefBrowser browser, bool pinned)
    {
        var changed = !ReferenceEquals(_browser, browser);
        if (changed)
        {
            StopHighlight();
            _browser = browser;
            _seenIdentifier = 0;
        }

        IsPin = pinned;
        if (changed && _text.Length > 0)
            Restart();
    }

    public void SetQuery(string? text)
    {
        _text = text ?? "";
        if (_text.Length == 0)
        {
            StopHighlight();
            return;
        }

        Restart();
    }

    public void Next() => Step(true);

    public void Previous() => Step(false);

    public void Release()
    {
        StopHighlight();
        _browser = null;
        IsPin = false;
        _seenIdentifier = 0;
    }

    private void OnResult(AvaloniaCefBrowser browser, int identifier, int count, int ordinal)
    {
        if (!ReferenceEquals(_browser, browser))
            return;
        if (identifier < _seenIdentifier)
            return;
        _seenIdentifier = identifier;
        Result?.Invoke(count, ordinal);
    }

    private void Restart() => WithHost(host => host.Find(_text, true, false, false));

    private void Step(bool forward)
    {
        if (_text.Length == 0)
            return;
        WithHost(host => host.Find(_text, forward, false, true));
    }

    private void StopHighlight() => WithHost(host => host.StopFinding(true));

    private void WithHost(Action<CefBrowserHost> action)
    {
        var browser = _browser;
        if (browser == null)
            return;

        CefBrowserHost host;
        try
        {
            host = browser.GetBrowserHost();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Find] {ex.Message}");
            return;
        }

        if (host == null)
            return;

        try
        {
            action(host);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Find] {ex.Message}");
        }
        finally
        {
            host.Dispose();
        }
    }
}

sealed class PageFindHandler : FindHandler
{
    private readonly Action<int, int, int> _report;

    public PageFindHandler(Action<int, int, int> report) => _report = report;

    protected override void OnFindResult(CefBrowser browser, int identifier, int count, CefRectangle selectionRect, int activeMatchOrdinal, bool finalUpdate)
    {
        var id = identifier;
        var matches = count;
        var ordinal = activeMatchOrdinal;
        Dispatcher.UIThread.Post(() => _report(id, matches, ordinal));
    }
}
