using System.Runtime.InteropServices;

// Load native libraries (gdi32, user32, comctl32, SQLite...) only from System32 / the app's own trusted locations by
// name, never from the current directory or other writable folders where an attacker could plant a look-alike DLL.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

// Lets the test project exercise the internal validation helpers directly.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("FontA.Tests")]
