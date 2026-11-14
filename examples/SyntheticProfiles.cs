// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
// Hand-authored logical events only. These are not device decoders or live sources.
using System.Collections.Generic;
using InputLift.Core;

namespace InputLift.Examples
{
    internal static class SyntheticProfiles
    {
        internal const string Source = "demo-device", Button = "button", Value = "level";
        internal static IEnumerable<InputEvent> Hold()
        {
            yield return InputEvent.Button(Source, Button, true, 0);
            yield return InputEvent.Button(Source, Button, true, 10); // Duplicate state.
            yield return InputEvent.Button(Source, Button, false, 100);
            yield return InputEvent.Button(Source, Button, true, 200);
            yield return InputEvent.Reset(Source, 210); // Simulated connection loss.
        }
        internal static IEnumerable<InputEvent> Toggle()
        {
            yield return InputEvent.Button(Source, Button, true, 0);
            yield return InputEvent.Button(Source, Button, true, 10);
            yield return InputEvent.Button(Source, Button, false, 20);
            yield return InputEvent.Button(Source, Button, true, 30);
            yield return InputEvent.Button(Source, Button, false, 40);
            yield return InputEvent.Button(Source, Button, true, 50);
            yield return InputEvent.Reset(Source, 60);
        }
        internal static IEnumerable<InputEvent> Values()
        {
            yield return InputEvent.Absolute(Source, Value, 0.5, 0);
            yield return InputEvent.Relative(Source, Value, 2, 10);
            yield return InputEvent.Relative(Source, Value, int.MaxValue, 20);
            yield return InputEvent.Relative(Source, Value, int.MinValue, 30);
            yield return InputEvent.Reset(Source, 40);
        }
        internal static IEnumerable<InputEvent> All()
        {
            yield return InputEvent.Button(Source, Button, true, 0);
            yield return InputEvent.Button(Source, Button, false, 10);
            yield return InputEvent.Absolute(Source, Value, 0.5, 20);
            yield return InputEvent.Relative(Source, Value, -1, 30);
            yield return InputEvent.Reset(Source, 40);
        }
    }
}
