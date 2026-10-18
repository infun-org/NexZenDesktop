using System;
using System.Reflection;
using Xilium.CefGlue.Avalonia;

var type = typeof(AvaloniaCefBrowser);
foreach (var evt in type.GetEvents())
{
    Console.WriteLine(evt.Name);
}
