// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Client.IoC;
using System.Linq;
using System.IO;
using Content.Shared._NC.Meta.UI;
using Robust.Shared.Serialization;
using Content.Client.Parallax.Managers;
using Robust.Client;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests.Utility;

public sealed class MetaSandboxTest
{
    [Test]
    public async Task TheOrdinaryClientKeepsTheCompilerAndVmOnTheServerAndPassesSandbox()
    {
        var shipped = new[] { typeof(Client.Entry.EntryPoint).Assembly, typeof(Shared.Entry.EntryPoint).Assembly,
            typeof(Shared.Database.LogType).Assembly };
        foreach (var assembly in shipped)
            Assert.That(assembly.GetReferencedAssemblies().Any(reference => reference.Name == "Content.Meta"), Is.False,
                "The ordinary client must not reference the server compiler/VM.");
        var logHandler = new PoolTestLogHandler("CLIENT");
        logHandler.ActivateContext(TestContext.Out);
        var options = new RobustIntegrationTest.ClientIntegrationOptions
        {
            ContentStart = true,
            OverrideLogHandler = () => logHandler,
            ContentAssemblies =
            [
                typeof(Shared.Entry.EntryPoint).Assembly,
                typeof(Client.Entry.EntryPoint).Assembly,
            ],
            Options = new GameControllerOptions { LoadConfigAndUserData = false },
        };

        options.BeforeStart += () =>
        {
            IoCManager.Resolve<IModLoader>().SetModuleBaseCallbacks(new ClientModuleTestingCallbacks
            {
                ClientBeforeIoC = () =>
                {
                    IoCManager.Register<IParallaxManager, DummyParallaxManager>(true);
                    IoCManager.Resolve<ILogManager>().GetSawmill("loc").Level = LogLevel.Error;
                    IoCManager.Resolve<IConfigurationManager>()
                        .OnValueChanged(RTCVars.FailureLogLevel, value => logHandler.FailureLevel = value, true);
                },
            });
        };

        using var client = new RobustIntegrationTest.ClientIntegrationInstance(options);
        await client.WaitIdleAsync();
        foreach (var assembly in shipped) await client.CheckSandboxed(assembly);
        await client.WaitAssertion(() =>
        {
            var serializer = client.ResolveDependency<IRobustSerializer>();
            var values = new NCMetaUiObject();
            values["message"] = new NCMetaUiScalar { ValueKind = NCMetaUiValueKind.String, Text = "Привет 🚀" };
            var numbers = new NCMetaUiArray();
            numbers.Items.Add(new NCMetaUiScalar { ValueKind = NCMetaUiValueKind.Integer, Integer = long.MaxValue });
            values["numbers"] = numbers;
            var original = new NCMetaAppState { SessionId = Guid.NewGuid(), Values = values, StateJson = "server-only", Document = "server-only" };
            using var stream = new MemoryStream();
            serializer.SerializeDirect(stream, original);
            stream.Position = 0;
            serializer.DeserializeDirect(stream, out NCMetaAppState copy);
            Assert.That(copy.SessionId, Is.EqualTo(original.SessionId));
            Assert.That(((NCMetaUiScalar)((NCMetaUiObject)copy.Values!)["message"]!).Text, Is.EqualTo("Привет 🚀"));
            Assert.That(((NCMetaUiScalar)((NCMetaUiArray)((NCMetaUiObject)copy.Values)["numbers"]!)[0]).Integer, Is.EqualTo(long.MaxValue));
            Assert.That(copy.StateJson, Is.Not.EqualTo(original.StateJson), "Legacy JSON and markup must not be transmitted alongside typed presentation data.");
            Assert.That(copy.Document, Is.Null.Or.Empty);
            using var command = new MemoryStream();
            serializer.SerializeDirect(command, new NCMetaWorkspaceRequestMessage(NCMetaWorkspaceOperation.QuoteHosting,
                Guid.NewGuid(), payload: new NCMetaHostingQuotePayload("garage.nc", "home", 1)));
            command.Position = 0;
            serializer.DeserializeDirect(command, out NCMetaWorkspaceRequestMessage request);
            Assert.That(request.Payload, Is.EqualTo(new NCMetaHostingQuotePayload("garage.nc", "home", 1)));
        });
    }
}
