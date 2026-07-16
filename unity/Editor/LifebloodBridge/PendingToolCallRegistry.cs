using System;
using System.Collections.Generic;
using System.Threading.Tasks;

#nullable disable

namespace Lifeblood.UnityBridge
{
    internal enum PendingCallAdmission
    {
        Started,
        Coalesced,
        ConflictingArguments,
    }

    internal enum PendingCallPollState
    {
        Missing,
        Pending,
        Completed,
    }

    /// <summary>
    /// Owns the Unity bridge's pollable-call identity. Coplay status requests
    /// carry the tool name plus <c>action:status</c>, not the original payload,
    /// so one tool name owns at most one unconsumed call. Argument identity is
    /// retained only to coalesce exact retries and reject ambiguous replacements.
    /// </summary>
    internal sealed class PendingToolCallRegistry<T>
    {
        private sealed class Entry
        {
            public Entry(string requestIdentity, Task<T> task)
            {
                RequestIdentity = requestIdentity;
                Task = task;
            }

            public string RequestIdentity { get; }
            public Task<T> Task { get; }
        }

        private readonly object _sync = new object();
        private readonly Dictionary<string, Entry> _calls = new Dictionary<string, Entry>();

        public PendingCallAdmission Admit(
            string toolName,
            string requestIdentity,
            Func<Task<T>> start,
            out Task<T> task)
        {
            lock (_sync)
            {
                if (_calls.TryGetValue(toolName, out var existing))
                {
                    task = existing.Task;
                    return string.Equals(
                        existing.RequestIdentity,
                        requestIdentity,
                        StringComparison.Ordinal)
                        ? PendingCallAdmission.Coalesced
                        : PendingCallAdmission.ConflictingArguments;
                }

                task = start() ?? throw new InvalidOperationException(
                    "A pending-call factory returned null.");
                _calls.Add(toolName, new Entry(requestIdentity, task));
                return PendingCallAdmission.Started;
            }
        }

        public PendingCallPollState Poll(string toolName, out Task<T> task)
        {
            lock (_sync)
            {
                if (!_calls.TryGetValue(toolName, out var entry))
                {
                    task = null;
                    return PendingCallPollState.Missing;
                }

                task = entry.Task;
                if (!task.IsCompleted)
                    return PendingCallPollState.Pending;

                _calls.Remove(toolName);
                return PendingCallPollState.Completed;
            }
        }
    }
}
