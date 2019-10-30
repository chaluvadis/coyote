﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Coyote.Machines;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.TestingServices.Tests
{
    public class EntryPointMachineCreationTest : BaseTest
    {
        public EntryPointMachineCreationTest(ITestOutputHelper output)
            : base(output)
        {
        }

        private class M : StateMachine
        {
            [Start]
            private class Init : MachineState
            {
            }
        }

        private class N : StateMachine
        {
            [Start]
            private class Init : MachineState
            {
            }
        }

        [Fact(Timeout=5000)]
        public void TestEntryPointMachineCreation()
        {
            this.Test(r =>
            {
                MachineId m = r.CreateMachine(typeof(M));
                MachineId n = r.CreateMachine(typeof(N));
                r.Assert(m != null && m != null, "Machine ids are null.");
            });
        }
    }
}
