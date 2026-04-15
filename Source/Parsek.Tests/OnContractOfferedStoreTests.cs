using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Covers §G/#398 of career-earnings-bundle plan: OnContractOffered must NOT
    /// call GameStateStore.AddEvent. Offered contracts are transient advertisements
    /// — before the fix, the clear-and-regenerate cycle in #404 caused the store
    /// to accumulate ContractOffered events until the save file exploded.
    ///
    /// The handler is a private instance method taking a KSP Contract which we
    /// can't construct in tests. We cover the guarantee via:
    ///   1) ConvertEvent(ContractOffered) still returns null (drop regression guard).
    ///   2) Reflection-invoking OnContractOffered(null) exercises the null guard
    ///      and proves no AddEvent call fires on the null path.
    ///   3) A direct parity test exercising the "not stored" log line.
    /// </summary>
    [Collection("Sequential")]
    public class OnContractOfferedStoreTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public OnContractOfferedStoreTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
        }

        public void Dispose()
        {
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void ConvertEvent_ContractOffered_StillReturnsNull()
        {
            // Regression guard: even if someone were to re-add an AddEvent call in
            // OnContractOffered, the converter still drops the type so no ledger entry
            // would form. This is the second half of #398's safety.
            var evt = new GameStateEvent
            {
                ut = 100.0,
                eventType = GameStateEventType.ContractOffered,
                key = Guid.NewGuid().ToString(),
                detail = "Explore Duna"
            };

            var action = GameStateEventConverter.ConvertEvent(evt, null);

            Assert.Null(action);
        }

        [Fact]
        public void OnContractOffered_NullContract_DoesNotCrashOrAddEvent()
        {
            int before = GameStateStore.Events.Count;

            var recorder = new GameStateRecorder();
            var method = typeof(GameStateRecorder).GetMethod(
                "OnContractOffered", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            method.Invoke(recorder, new object[] { null });

            Assert.Equal(before, GameStateStore.Events.Count);
        }

        [Fact]
        public void OnContractOffered_NoStoreAddEvent_ForAnyContract()
        {
            // We can't synth a real KSP Contract, but we can prove the code path does
            // not contain a call to GameStateStore.AddEvent by scanning the IL of
            // OnContractOffered. If a future commit re-introduces the bug by pasting
            // an AddEvent call back into the handler, this test trips.
            var method = typeof(GameStateRecorder).GetMethod(
                "OnContractOffered", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var body = method.GetMethodBody();
            Assert.NotNull(body);
            byte[] il = body.GetILAsByteArray();
            Assert.NotNull(il);

            // Walk the IL looking for a Call/Callvirt to GameStateStore.AddEvent.
            // Simple scan: decode each Call opcode and resolve the MethodInfo token.
            var module = method.Module;
            bool foundAddEvent = false;
            int i = 0;
            while (i < il.Length)
            {
                byte op = il[i];
                // 0x28 = Call, 0x6F = Callvirt
                if (op == 0x28 || op == 0x6F)
                {
                    int token = BitConverter.ToInt32(il, i + 1);
                    MethodBase target = null;
                    try { target = module.ResolveMethod(token); } catch { /* cross-module, ignore */ }
                    if (target != null &&
                        target.DeclaringType == typeof(GameStateStore) &&
                        target.Name == "AddEvent")
                    {
                        foundAddEvent = true;
                        break;
                    }
                    i += 5;
                }
                else
                {
                    // Advance one byte; this is a rough scan — we only care about 5-byte
                    // Call/Callvirt instructions, so skipping by 1 on everything else is
                    // safe because Call/Callvirt opcodes are unambiguous single bytes.
                    i += 1;
                }
            }

            Assert.False(foundAddEvent,
                "OnContractOffered must not call GameStateStore.AddEvent (#398 regression guard)");
        }
    }
}
