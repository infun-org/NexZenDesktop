using System;
using System.Reflection;
using Avalonia.Input;
class Program { static void Main() { 
    foreach(var prop in typeof(DragEventArgs).GetProperties()) Console.WriteLine("Prop: " + prop.Name + " type " + prop.PropertyType.FullName);
} }
