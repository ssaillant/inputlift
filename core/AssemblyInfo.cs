// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Runtime.CompilerServices;

[assembly: AssemblyTitle("InputLift.Core")]
[assembly: AssemblyDescription("Typed input events and decoding for one supported Bluetooth Fn profile")]
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]
// Test access is for synthetic parser tests, not a security boundary.
[assembly: InternalsVisibleTo("InputLift.CoreTests")]
