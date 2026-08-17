// Read the game's own method bodies instead of guessing at them.
//
// CLAUDE.md: reflection load fails on the publicised assembly, and every attempt in this
// repository that guessed at a game API name was wrong. The client throws a
// NullReferenceException inside SuitLocker.UnequipFrom, and Mono inlined the frame away,
// so the only place the answer exists is the method body.
//
// usage: decomp <assembly> <TypeName> [MemberName]

using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: decomp <assembly.dll> <TypeName> [MemberName]");
    return 2;
}

string asmPath = args[0];
string typeName = args[1];
string member = args.Length > 2 ? args[2] : null;

var settings = new DecompilerSettings(LanguageVersion.CSharp10_0)
{
    ThrowOnAssemblyResolveErrors = false,
};

var decompiler = new CSharpDecompiler(asmPath, settings);
var typeSystem = decompiler.TypeSystem;

ITypeDefinition type = typeSystem.MainModule.TypeDefinitions
    .FirstOrDefault(t => t.Name == typeName || t.FullName == typeName);

if (type == null)
{
    Console.Error.WriteLine($"type '{typeName}' not found");
    return 3;
}

if (member == null)
{
    Console.WriteLine(decompiler.DecompileTypeAsString(new FullTypeName(type.FullName)));
    return 0;
}

var handles = type.Members
    .Where(m => m.Name == member)
    .Select(m => m.MetadataToken)
    .Where(h => !h.IsNil)
    .ToList();

if (handles.Count == 0)
{
    Console.Error.WriteLine($"member '{member}' not found on {type.FullName}. members:");
    foreach (var m in type.Members.Select(m => m.Name).Distinct().OrderBy(n => n))
        Console.Error.WriteLine("  " + m);
    return 4;
}

Console.WriteLine(decompiler.DecompileAsString(handles));
return 0;
