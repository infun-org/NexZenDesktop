using System;
using System.Reflection;
using Avalonia.Input;
class Program {
    static void Main() {
        Console.WriteLine("DataFormat fields:");
        foreach(var f in typeof(DataFormat).GetFields()) Console.WriteLine(f.Name);
        Console.WriteLine("IDataObject methods:");
        foreach(var m in typeof(IDataObject).GetMethods()) Console.WriteLine(m.Name);
    }
}
