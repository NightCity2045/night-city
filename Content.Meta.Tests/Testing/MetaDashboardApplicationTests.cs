// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Language;
using Content.Meta.Mui;
using Content.Meta.Runtime;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Testing;

[TestFixture]
public sealed class MetaDashboardApplicationTests
{
    private static string Root
    {
        get
        {
            var folder = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (folder != null && !Directory.Exists(Path.Combine(folder.FullName, "Resources", "Meta", "Dashboard"))) folder = folder.Parent;
            return folder!.FullName;
        }
    }
    private static void Drain(MetaProcess process, long now)
    {
        for (var count = 0; count < 1000 && process.State is MetaProcessState.Runnable or MetaProcessState.Collecting; count++) process.RunSlice(128, now);
        Assert.That(process.Fault, Is.Null);
        Assert.That(process.State, Is.EqualTo(MetaProcessState.Idle));
    }

    [TestCase("en-US", "Speed:")]
    [TestCase("ru-RU", "Скорость:")]
    public void NativeTelemetryUpdatesARealPublicAppViewAndStaleSamplesDisappear(string locale, string speedLabel)
    {
        var path = Path.Combine(Root, "Resources", "Meta", "Dashboard");
        var request = new MetaCompilationRequest([new("main.meta", File.ReadAllText(Path.Combine(path, "main.meta")))], "examples.dashboard");
        var compiler = new MetaCompiler(); var compiled = compiler.Compile(request);
        Assert.That(compiled.Success, Is.True, string.Join(',', compiled.Diagnostics.Select(d => d.Code + ":" + string.Join(',', d.Arguments))));
        var artifact = MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!));
        var exports = compiler.VerifyExports(request, artifact);
        var contract = File.ReadAllText(Path.Combine(path, "app.json"));
        var documents = new Dictionary<string, string> { ["ui/dashboard.mui"] = File.ReadAllText(Path.Combine(path, "dashboard.mui")) };
        var digest = MuiVerifiedApplication.ComputePackageDigest(artifact.Digest, contract, documents, "");
        Assert.That(MuiVerifiedApplication.TryAdmit(artifact, exports, contract, documents, "", digest, out var app, out var error), Is.True, error);
        var process = new MetaProcess(artifact);
        for (var id = 0; id < artifact.GetLinks().Length; id++) Assert.That(process.TryBindLink(id, (ulong) id + 1), Is.True);
        process.SetClock(1, 60, 1000.0 / 60);
        Assert.That(process.TryStartMain(), Is.True); Drain(process, 1000);

        JsonDocument View(long now)
        {
            var context = process.CreateRecord(app!.ContextTypeId, [process.CreateOpaque(app.UserRefTypeId, 11), process.CreateOpaque(app.SessionRefTypeId, 22),
                process.CreateString("dashboard"), process.CreateString("dashboard"), process.CreateString(locale)]);
            Assert.That(process.TryStart(app.Views["dashboard"].FunctionId, [context]), Is.True); Drain(process, now);
            Assert.That(MuiDtoCodec.TryProject(process, artifact, app.Views["dashboard"].StateTypeId, process.LastResult!.Value,
                app.Contract.Views["dashboard"].Fields, out var json), Is.True);
            return JsonDocument.Parse(json);
        }
        using (var initial = View(1000)) Assert.That(initial.RootElement.GetProperty("fresh").GetBoolean(), Is.False);
        MetaValue Stamp(long sequence) => process.CreateRecord(process.GetTypeId("meta.system.SampleStamp"), [MetaValue.Int(1), MetaValue.Int(sequence), MetaValue.Int(60)]);
        var engine = process.CreateRecord(process.GetTypeId("meta.vehicle.EngineState"), [MetaValue.Float(90), MetaValue.Int(2400), MetaValue.Float(300), MetaValue.Float(0.8),
            process.CreateArray(process.GetTypeId("array<string>"), []), process.CreateEnum(process.GetTypeId("meta.vehicle.EngineMode"), 0), Stamp(1)]);
        Assert.That(process.TryEnqueueEvent(0, [engine], 1) && process.TryStartNextEvent(), Is.True); Drain(process, 1000);
        var vehicle = process.CreateRecord(process.GetTypeId("meta.vehicle.DashboardSample"), [MetaValue.Float(10), MetaValue.Float(25), MetaValue.Float(0.75),
            MetaValue.Int(3), MetaValue.Bool(true), MetaValue.Bool(true), Stamp(2)]);
        Assert.That(process.TryEnqueueEvent(1, [vehicle], 2) && process.TryStartNextEvent(), Is.True); Drain(process, 1000);
        using (var current = View(1000))
        {
            Assert.That(current.RootElement.GetProperty("fresh").GetBoolean(), Is.True);
            Assert.That(current.RootElement.GetProperty("speed").GetString(), Does.StartWith(speedLabel).And.Contain("36"));
            Assert.That(current.RootElement.GetProperty("rpm").GetString(), Does.Contain("2400"));
            Assert.That(current.RootElement.GetProperty("history").GetArrayLength(), Is.EqualTo(1), "History contains only actual native samples.");
        }
        using (var stale = View(2501))
        {
            Assert.That(stale.RootElement.GetProperty("fresh").GetBoolean(), Is.False);
            Assert.That(stale.RootElement.GetProperty("speed").GetString(), Does.Not.Contain("36"));
        }
    }
}
