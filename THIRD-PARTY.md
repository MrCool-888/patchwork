# Third-party library

Patchwork 0.3.0 includes Mono.Cecil 0.11.6, an assembly inspection and rewriting library by Jb Evain and contributors, licensed under MIT/X11. The complete license is in `source/lib/Mono.Cecil.LICENSE.txt` and is installed alongside `Mono.Cecil.dll`.

Source: https://github.com/jbevain/cecil/tree/0.11.6
Commit: `5de7d8cbc91f6fd98dcc24b26b8c398db497809c`

The included DLL was built locally from this tag for .NET Framework. A few newer C# syntax forms were converted to equivalent C# 5 constructs (expression bodies, null-conditional access, pattern matching, static lambdas, and an out discard) to use the installed Framework compiler. It is an unsigned build. The compatibility patch and rebuild script are in `source/lib`.

Proton patch files and their generator live in the separate patch repository. No Proton application binaries or patch definitions are included in the Patchwork installer.
