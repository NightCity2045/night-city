// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Generic;
using Content.Server._NC.Meta.Runtime;
using NUnit.Framework;

namespace Content.Tests.Server._NC.Meta;

[TestFixture]
public sealed class NCMetaRuntimeClockTest
{
    [Test]
    public void ColdRuntimeInstancesDoNotReissueTheSameSampleStampNamespace()
    {
        var namespaces = new HashSet<long>();
        for (var instance = 0; instance < 32; instance++)
        {
            var runtime = new NCMetaRuntimeSystem();
            Assert.That(runtime.SchedulerEpoch, Is.InRange(0L, (1L << 62) - 1),
                "Cold namespaces must leave bounded headroom for monotonic world transitions.");
            Assert.That(namespaces.Add(runtime.SchedulerEpoch), Is.True,
                "A saved SampleStamp must not become fresh under a new runtime's fixed initial epoch.");
        }
    }
}
