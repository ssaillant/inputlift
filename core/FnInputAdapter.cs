// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

namespace InputLift.Core
{
    /// <summary>
    /// Converts the supported decoder's Fn transitions into generic control
    /// events. This is an in-memory mapping, not a capture/output backend.
    /// </summary>
    public sealed class FnInputAdapter
    {
        /// <summary>
        /// Caps accepted element count and result size. A caller-provided
        /// enumerator can still block; this is not a wall-clock execution limit.
        /// </summary>
        public const int MaximumBatchSize = 1024;
        private readonly string sourceId, controlId;

        public FnInputAdapter(string sourceId, string controlId = "fn")
        {
            InputEvent.ValidateToken(sourceId, "sourceId");
            InputEvent.ValidateToken(controlId, "controlId");
            this.sourceId = sourceId; this.controlId = controlId;
        }

        /// <summary>
        /// Preserves order/timestamps. Fn Reset becomes a source-wide Reset;
        /// consumers must invalidate other controls assigned to the same source.
        /// No partial result is returned if enumeration or validation fails.
        /// </summary>
        public IList<InputEvent> Convert(IEnumerable<FnTransition> transitions)
        {
            if (transitions == null) throw new ArgumentNullException("transitions");
            List<InputEvent> result = new List<InputEvent>();
            foreach (FnTransition transition in transitions)
            {
                if (result.Count == MaximumBatchSize)
                    throw new ArgumentException("Transition batch exceeds its size bound.", "transitions");
                if (transition == null) throw new ArgumentException("A transition must not be null.", "transitions");
                switch (transition.Kind)
                {
                    case FnTransitionKind.Pressed:
                        result.Add(InputEvent.Button(sourceId, controlId, true, transition.Timestamp)); break;
                    case FnTransitionKind.Released:
                        result.Add(InputEvent.Button(sourceId, controlId, false, transition.Timestamp)); break;
                    case FnTransitionKind.Reset:
                        result.Add(InputEvent.Reset(sourceId, transition.Timestamp)); break;
                    default:
                        throw new ArgumentException("Unsupported Fn transition kind.", "transitions");
                }
            }
            return result.AsReadOnly();
        }
    }
}
