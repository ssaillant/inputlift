// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
// TEST BUILD ONLY. Production DailyPaths.cs is not compiled into this fixture.
using System;
using System.Security.Principal;

internal static class DailyPaths
{
    internal static string ReaderSid;
    internal static string OwnerSid
    {
        get
        {
            if (ReaderSid == null) throw new InvalidOperationException("Select the temporary CI reader before constructing the pipe.");
            return new SecurityIdentifier(ReaderSid).Value;
        }
    }
    internal static bool ValidNonce(string value)
    {
        Guid parsed;
        return value != null && value.Length == 32 && Guid.TryParseExact(value, "N", out parsed);
    }
}
