// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Language;
using Content.Meta.Mui;
using Content.Meta.Runtime;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Testing;

[TestFixture]
public sealed class MetaStockApplicationTests
{
    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Content.Meta.Tests", "Fixtures", "Meta", "Projects"))) directory = directory.Parent;
            return directory!.FullName;
        }
    }

    public static IEnumerable<TestCaseData> LocalizedApps()
    {
        foreach (var id in new[] { "meta-stock-device-control", "meta-stock-security-journal", "meta-tutorial-threshold",
                     "meta-tutorial-hysteresis", "meta-tutorial-file", "meta-tutorial-panel", "meta-tutorial-installation" })
        foreach (var locale in new[] { "en-US", "ru-RU" }) yield return new(id, locale);
    }

    private sealed class Harness
    {
        public readonly MuiVerifiedApplication App;
        public readonly MetaProcess Process;
        public readonly Host Host = new();
        private long _nowMs = 1000;
        public Harness(string id)
        {
            var folder = Path.Combine(Root, "Content.Meta.Tests", "Fixtures", "Meta", "Projects", id);
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
            var request = new MetaCompilationRequest([new("main.meta", File.ReadAllText(Path.Combine(folder, "main.meta")))],
                manifest.RootElement.GetProperty("rootModule").GetString()!);
            var compiler = new MetaCompiler(); var compiled = compiler.Compile(request);
            Assert.That(compiled.Success, Is.True, string.Join(';', compiled.Diagnostics.Select(value => value.Code + ":" + string.Join(',', value.Arguments))));
            var artifact = MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!));
            var contract = File.ReadAllText(Path.Combine(folder, "app.json"));
            var documents = new Dictionary<string, string> { ["ui/main.mui"] = File.ReadAllText(Path.Combine(folder, "ui/main.mui")) };
            var digest = MuiVerifiedApplication.ComputePackageDigest(artifact.Digest, contract, documents, "");
            Assert.That(MuiVerifiedApplication.TryAdmit(artifact, compiler.VerifyExports(request, artifact), contract, documents, "", digest,
                out var app, out var error), Is.True, error);
            App = app!; Process = new(artifact, Host, new() { RetainForInvocations = true });
            for (var link = 0; link < artifact.GetLinks().Length; link++) Process.TryBindLink(link, (ulong) link + 1);
            SetTime(60, 1000); Assert.That(Process.TryStartMain(), Is.True); Drain();
        }
        public void SetTime(long tick, long milliseconds)
        {
            Process.SetClock(1, tick, 1000.0 / 60);
            _nowMs = milliseconds;
        }
        public void Drain()
        {
            for (var slice = 0; slice < 1000 && Process.State is MetaProcessState.Runnable or MetaProcessState.Collecting; slice++) Process.RunSlice(512, _nowMs);
            Assert.That(Process.Fault, Is.Null); Assert.That(Process.State, Is.EqualTo(MetaProcessState.Idle));
        }
        public MetaValue Context(string locale) => Process.CreateRecord(App.ContextTypeId,
            [Process.CreateOpaque(App.UserRefTypeId, 1), Process.CreateOpaque(App.SessionRefTypeId, 2),
                Process.CreateString("main"), Process.CreateString("desktop"), Process.CreateString(locale)]);
        public JsonDocument View(string locale)
        {
            Assert.That(Process.TryStart(App.Views["main"].FunctionId, [Context(locale)]), Is.True); Drain();
            Assert.That(MuiDtoCodec.TryProject(Process, App.Artifact, App.Views["main"].StateTypeId, Process.LastResult!.Value,
                App.Contract.Views["main"].Fields, out var json), Is.True);
            return JsonDocument.Parse(json);
        }
        public void Action(string name, string input)
        {
            var action = App.Actions[name];
            var value = MuiDtoCodec.DecodeInput(Process, App.Artifact, action.InputTypeId, input, App.Contract.Actions[name].Inputs);
            Assert.That(Process.TryStart(action.FunctionId, [Context("en-US"), value]), Is.True); Drain();
        }
    }

    private sealed class Host : IMetaHost
    {
        public readonly List<string> Commands = [];
        public MetaValue Read(MetaProcess process, MetaHostRequest request) => throw new InvalidOperationException();
        public MetaHostResult BeginAwait(MetaProcess process, MetaHostRequest request)
        {
            var import = process.GetImport(request.ImportId); Commands.Add(import.Name);
            MetaValue result;
            if (import.Name == "meta.files.FileStore.readText") result = process.CreateString("");
            else if (import.Name == "meta.files.FileStore.writeTextAtomic") result = MetaValue.Unit;
            else if (import.Name == "meta.security.Alarm.setActive") result = process.CreateRecord(process.GetTypeId("meta.security.AlarmState"),
                [request.Arguments[0], Stamp(process)]);
            else if (import.Name == "meta.doors.Door.setOpen") result = process.CreateRecord(process.GetTypeId("meta.doors.DoorState"),
                [request.Arguments[0], process.CreateEnum(process.GetTypeId("meta.doors.DoorPhase"), request.Arguments[0].AsBool() ? 1 : 3), Stamp(process)]);
            else throw new InvalidOperationException("Unexpected stock app command.");
            return MetaHostResult.Completed(process.CreateResult(import.ReturnType!.Value, result));
        }
        public void Log(MetaProcess process, string text) { }
        public void Cancel(MetaProcess process, long operationId) { }
        public static MetaValue Stamp(MetaProcess process) => process.CreateRecord(process.GetTypeId("meta.system.SampleStamp"),
            [MetaValue.Int(1), MetaValue.Int(1), MetaValue.Int(60)]);
    }

    [TestCaseSource(nameof(LocalizedApps))]
    public void ShippedAppsAdmitTheirActualMarkupAndProduceARealLocalizedView(string id, string locale)
    {
        var app = new Harness(id); using var view = app.View(locale);
        var title = view.RootElement.GetProperty("title").GetString()!;
        Assert.That(title.Length, Is.GreaterThan(2));
        Assert.That(title.Any(value => value is >= 'А' and <= 'я'), Is.EqualTo(locale == "ru-RU"));
    }

    [Test]
    public void OperatorLimitsRejectInversionAndTheDoorAppDistinguishesAcceptedFromPhysicalOpen()
    {
        var training = new Harness("meta-tutorial-panel");
        training.Action("configure", "{\"low\":40,\"high\":30}");
        using (var rejected = training.View("en-US"))
        {
            Assert.That(rejected.RootElement.GetProperty("high").GetInt32(), Is.EqualTo(30));
            Assert.That(rejected.RootElement.GetProperty("low").GetInt32(), Is.EqualTo(25));
        }
        training.Action("configure", "{\"low\":20,\"high\":35}");
        using (var accepted = training.View("en-US")) Assert.That(accepted.RootElement.GetProperty("high").GetInt32(), Is.EqualTo(35));
        var control = new Harness("meta-stock-device-control"); control.Action("open", "{}");
        using var pending = control.View("en-US");
        Assert.That(pending.RootElement.GetProperty("status").GetString(), Is.EqualTo("Door transitioning"));
        Assert.That(control.Host.Commands, Is.EqualTo(new[] { "meta.doors.Door.setOpen" }));
        var state = control.Process.CreateRecord(control.Process.GetTypeId("meta.doors.DoorState"),
            [MetaValue.Bool(true), control.Process.CreateEnum(control.Process.GetTypeId("meta.doors.DoorPhase"), 2), Host.Stamp(control.Process)]);
        Assert.That(control.Process.TryEnqueueEvent(0, [state], 1) && control.Process.TryStartNextEvent(), Is.True); control.Drain();
        using var opened = control.View("en-US"); Assert.That(opened.RootElement.GetProperty("status").GetString(), Is.EqualTo("Open"));
        control.SetTime(240, 4000);
        using var stale = control.View("en-US");
        Assert.That(control.Process.NowMs, Is.EqualTo(4000));
        Assert.That(stale.RootElement.GetProperty("status").GetString(), Is.EqualTo("No fresh observation"));
    }
}
