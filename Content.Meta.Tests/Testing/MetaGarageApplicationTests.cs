// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Language;
using Content.Meta.Mui;
using Content.Meta.Runtime;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Testing;

[TestFixture]
public sealed class MetaGarageApplicationTests
{
    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Resources", "Meta", "Garage"))) directory = directory.Parent;
            return directory!.FullName;
        }
    }

    private sealed class Harness
    {
        public readonly MuiVerifiedApplication App;
        public readonly MetaProcess Process;
        public readonly SessionHost Host = new();
        private readonly Dictionary<(ulong User, ulong Session), (MetaValue User, MetaValue Session)> _proofs = [];

        public Harness()
        {
            var folder = Path.Combine(Root, "Resources", "Meta", "Garage");
            var catalog = MetaApiCatalog.Default;
            var member = new MetaApiMember("meta.services.ServiceOrders", "cachedList", ["meta.app.UserRef", "meta.app.SessionRef"],
                "result<array<meta.services.Order>>", "Read", "services.read");
            Assert.That(catalog.Members.Single(x => x.QualifiedName == member.QualifiedName).ContractHash, Is.EqualTo(member.ContractHash));
            var request = new MetaCompilationRequest([new("main.meta", File.ReadAllText(Path.Combine(folder, "main.meta")))], "examples.garage");
            var compiler = new MetaCompiler(catalog);
            var result = compiler.Compile(request);
            Assert.That(result.Success, Is.True, string.Join(';', result.Diagnostics.Select(d => d.Code + ":" + string.Join(',', d.Arguments))));
            var artifact = MetaVerifier.Verify(result.Artifact!, catalog.CreateVerificationCatalog(result.Module!));
            var exports = compiler.VerifyExports(request, artifact);
            var documents = new Dictionary<string, string>
            {
                ["ui/customer.mui"] = File.ReadAllText(Path.Combine(folder, "customer.mui")),
                ["ui/staff.mui"] = File.ReadAllText(Path.Combine(folder, "staff.mui")),
            };
            var contract = File.ReadAllText(Path.Combine(folder, "app.json"));
            var style = File.ReadAllText(Path.Combine(folder, "garage.mstyle"));
            var digest = MuiVerifiedApplication.ComputePackageDigest(artifact.Digest, contract, documents, style);
            Assert.That(MuiVerifiedApplication.TryAdmit(artifact, exports, contract, documents, style, digest, out var app, out var error), Is.True, error);
            App = app!;
            Process = new MetaProcess(artifact, Host, new MetaProcessLimits { RetainForInvocations = true });
            for (var index = 0; index < artifact.GetLinks().Length; index++) Assert.That(Process.TryBindLink(index, (ulong) index + 1), Is.True);
            Process.SetClock(1, 60, 1000.0 / 60);
            Assert.That(Process.TryStartMain(), Is.True);
            Drain();
        }

        public void Drain()
        {
            for (var slice = 0; slice < 1000 && Process.State is MetaProcessState.Runnable or MetaProcessState.Collecting; slice++) Process.RunSlice(512, 1000);
            Assert.That(Process.Fault, Is.Null);
            Assert.That(Process.State, Is.AnyOf(MetaProcessState.Idle, MetaProcessState.Waiting));
        }

        private MetaValue Context(ulong user, ulong session, string view, string locale)
        {
            if (!_proofs.TryGetValue((user, session), out var proof))
            {
                proof = (Process.CreateOpaque(App.UserRefTypeId, user), Process.CreateOpaque(App.SessionRefTypeId, session));
                _proofs.Add((user, session), proof);
            }
            return Process.CreateRecord(App.ContextTypeId, [proof.User, proof.Session, Process.CreateString(view), Process.CreateString(view), Process.CreateString(locale)]);
        }

        public void Revoke(ulong user, ulong session)
        {
            var proof = _proofs[(user, session)];
            Assert.That(Process.TryReleaseOpaque(proof.User) && Process.TryReleaseOpaque(proof.Session), Is.True);
            _proofs.Remove((user, session));
            Host.Revoked.Add(session);
        }

        public JsonDocument View(ulong user, ulong session, string view = "customer", string locale = "en-US")
        {
            var context = Context(user, session, view, locale);
            Assert.That(Process.TryStart(App.Views[view].FunctionId, [context]), Is.True);
            Drain();
            Assert.That(Process.State, Is.EqualTo(MetaProcessState.Idle), "A view cannot suspend for a private list query.");
            Assert.That(MuiDtoCodec.TryProject(Process, App.Artifact, App.Views[view].StateTypeId, Process.LastResult!.Value,
                App.Contract.Views[view].Fields, out var json), Is.True);
            return JsonDocument.Parse(json);
        }

        public void Action(string name, ulong user, ulong session, string input = "{}")
        {
            var action = App.Actions[name];
            var context = Context(user, session, "customer", "en-US");
            var data = MuiDtoCodec.DecodeInput(Process, App.Artifact, action.InputTypeId, input, App.Contract.Actions[name].Inputs);
            Assert.That(Process.TryStart(action.FunctionId, [context, data]), Is.True);
            Drain();
        }

        public void AssertSuccess() => Assert.That(Process.GetVariantTag(Process.LastResult!.Value), Is.Zero);
    }

    private sealed class SessionHost : IMetaHost
    {
        private static readonly Dictionary<ulong, ulong> Users = new() { [11] = 1, [22] = 2 };
        public readonly HashSet<ulong> Warm = [];
        public readonly HashSet<ulong> Revoked = [];
        public readonly List<string> Calls = [];
        public bool FailRefresh;
        public int TelemetryReads;
        public int PaymentResultType;

        private bool Proof(MetaProcess process, MetaHostRequest request, out ulong session)
        {
            session = 0;
            return request.Arguments.Count >= 2 && process.TryGetOpaqueToken(request.Arguments[0], out var user) &&
                process.TryGetOpaqueToken(request.Arguments[1], out session) && Users.TryGetValue(session, out var expected) &&
                expected == user && !Revoked.Contains(session);
        }

        private static MetaValue Order(MetaProcess process, ulong session)
        {
            var alice = session == 11;
            return process.CreateRecord(process.GetTypeId("meta.services.Order"),
                [process.CreateString(alice ? "a1111111-1111-4111-8111-111111111111" : "b2222222-2222-4222-8222-222222222222"),
                    process.CreateString(alice ? "Alice brake inspection" : "Bob oil change"), process.CreateString(alice ? "alice" : "bob"),
                    process.CreateString("mechanic"), process.CreateEnum(process.GetTypeId("meta.services.OrderStatus"), 3), MetaValue.Int(4),
                    process.CreateString("invoice-private"), process.CreateString(new string('a', 64)), MetaValue.Int(125), MetaValue.Bool(false)]);
        }

        public MetaValue Read(MetaProcess process, MetaHostRequest request)
        {
            var import = process.GetImport(request.ImportId);
            if (import.Name != "meta.services.ServiceOrders.cachedList")
            {
                TelemetryReads++;
                return process.CreateErrorResult(import.ReturnType!.Value, "AccessDenied");
            }
            if (!Proof(process, request, out var session)) return process.CreateErrorResult(import.ReturnType!.Value, "PermissionRevoked");
            if (!Warm.Contains(session)) return process.CreateErrorResult(import.ReturnType!.Value, "StaleState");
            var rows = process.CreateArray(process.GetTypeId("array<meta.services.Order>"), [Order(process, session)]);
            return process.CreateResult(import.ReturnType!.Value, rows);
        }

        public MetaHostResult BeginAwait(MetaProcess process, MetaHostRequest request)
        {
            var import = process.GetImport(request.ImportId);
            Calls.Add(import.Name);
            var resultType = import.ReturnType!.Value;
            if (!Proof(process, request, out var session)) return MetaHostResult.Completed(process.CreateErrorResult(resultType, "PermissionRevoked"));
            switch (import.Name)
            {
                case "meta.services.ServiceOrders.listOrders":
                    if (FailRefresh) return MetaHostResult.Completed(process.CreateErrorResult(resultType, "IoFailure"));
                    Warm.Add(session);
                    return MetaHostResult.Completed(process.CreateResult(resultType,
                        process.CreateArray(process.GetTypeId("array<meta.services.Order>"), [Order(process, session)])));
                case "meta.services.ServiceOrders.create":
                    Assert.That(process.GetString(request.Arguments[3]), Is.EqualTo("create-order"));
                    return MetaHostResult.Completed(process.CreateResult(resultType, Order(process, session)));
                case "meta.services.ServiceOrders.get":
                    Assert.That(process.GetString(request.Arguments[2]), Is.EqualTo("ORD-a1111111"));
                    return MetaHostResult.Completed(process.CreateResult(resultType, Order(process, session)));
                case "meta.services.ServiceOrders.assign":
                    Assert.That(process.GetString(request.Arguments[2]), Is.EqualTo("a1111111-1111-4111-8111-111111111111"));
                    Assert.That(process.GetString(request.Arguments[3]), Is.EqualTo("self"));
                    Assert.That(request.Arguments[4].AsInt(), Is.EqualTo(4));
                    Assert.That(process.GetString(request.Arguments[5]), Is.EqualTo("take-order"));
                    return MetaHostResult.Completed(process.CreateResult(resultType, Order(process, session)));
                case "meta.services.ServiceOrders.transition":
                    Assert.That(process.GetString(request.Arguments[2]), Is.EqualTo("a1111111-1111-4111-8111-111111111111"));
                    Assert.That(request.Arguments[4].AsInt(), Is.EqualTo(4));
                    Assert.That(process.GetString(request.Arguments[5]), Is.EqualTo("begin-work"));
                    return MetaHostResult.Completed(process.CreateResult(resultType, Order(process, session)));
                case "meta.services.ServiceOrders.invoice":
                    Assert.That(process.GetString(request.Arguments[2]), Is.EqualTo("a1111111-1111-4111-8111-111111111111"));
                    Assert.That(request.Arguments[3].AsInt(), Is.EqualTo(125));
                    Assert.That(request.Arguments[5].AsInt(), Is.EqualTo(4));
                    Assert.That(process.GetString(request.Arguments[6]), Is.EqualTo("quote-order"));
                    return MetaHostResult.Completed(process.CreateResult(resultType, Order(process, session)));
                case "meta.services.ServiceOrders.requestPayment":
                    Assert.That(process.GetString(request.Arguments[2]), Is.EqualTo("ORD-a1111111"));
                    PaymentResultType = resultType;
                    return MetaHostResult.Awaiting(73);
                default: throw new InvalidOperationException("Unexpected guest command: " + import.Name);
            }
        }

        public void Log(MetaProcess process, string text) => Assert.Fail("The supplied app does not log private order data.");
        public void Cancel(MetaProcess process, long operationId) { }
    }

    [Test]
    public void OneSharedVmNeverCarriesCustomerRowsIntoAnotherOrRevokedSession()
    {
        var harness = new Harness();
        harness.Action("refresh", 1, 11); harness.AssertSuccess();
        using (var alice = harness.View(1, 11))
        {
            var row = alice.RootElement.GetProperty("orders")[0].GetProperty("summary").GetString();
            Assert.That(row, Does.StartWith("ORD-a1111111").And.Contain("Alice").And.Not.Contain("1111-4111"));
        }
        using (var initialBob = harness.View(2, 22)) Assert.That(initialBob.RootElement.GetProperty("orders").GetArrayLength(), Is.Zero);
        harness.Action("refresh", 2, 22); harness.AssertSuccess();
        using (var bob = harness.View(2, 22, locale: "ru-RU"))
        {
            Assert.That(bob.RootElement.GetProperty("orders")[0].GetProperty("summary").GetString(), Does.Contain("Bob").And.Not.Contain("Alice"));
            Assert.That(bob.RootElement.GetProperty("orders")[0].GetProperty("status").GetString(), Is.EqualTo("Ожидает оплаты"));
        }
        using (var forgedProof = harness.View(2, 11)) Assert.That(forgedProof.RootElement.GetProperty("orders").GetArrayLength(), Is.Zero);
        harness.Host.Revoked.Add(11);
        using (var revoked = harness.View(1, 11)) Assert.That(revoked.RootElement.GetProperty("orders").GetArrayLength(), Is.Zero);
        Assert.That(harness.Host.TelemetryReads, Is.Zero, "Customer views never query a workshop vehicle.");
        Assert.That(harness.Host.Calls, Is.All.EqualTo("meta.services.ServiceOrders.listOrders"));
        Assert.That(harness.App.Contract.Views["customer"].Actions, Does.Not.Contain("invoice").And.Not.Contain("take"));
        using var staff = harness.View(2, 22, "staff");
        Assert.That(harness.Host.TelemetryReads, Is.EqualTo(2));
        Assert.That(staff.RootElement.GetProperty("engine").GetString(), Does.Contain("No current permitted"));
    }

    [Test]
    public void ACommittedMutationRemainsSuccessfulWhenItsSubsequentPrivateRefreshFails()
    {
        var harness = new Harness();
        harness.Host.FailRefresh = true;
        harness.Action("create", 1, 11, "{\"subject\":\"Inspect the actual vehicle\"}");
        harness.AssertSuccess();
        Assert.That(harness.Host.Calls, Is.EqualTo(new[] { "meta.services.ServiceOrders.create", "meta.services.ServiceOrders.listOrders" }));
        using var view = harness.View(1, 11);
        Assert.That(view.RootElement.GetProperty("orders").GetArrayLength(), Is.Zero);
    }

    [TestCase("take", "assign")]
    [TestCase("begin", "transition")]
    [TestCase("invoice", "invoice")]
    public void AHumanBusinessReferenceResolvesBeforeTheRevisionBoundMutation(string action, string member)
    {
        var harness = new Harness();
        var input = action == "invoice" ? "{\"id\":\"ORD-a1111111\",\"amount\":125,\"description\":\"Physical inspection\"}" : "{\"id\":\"ORD-a1111111\"}";
        harness.Action(action, 1, 11, input); harness.AssertSuccess();
        Assert.That(harness.Host.Calls, Is.EqualTo(new[] { "meta.services.ServiceOrders.get", "meta.services.ServiceOrders." + member, "meta.services.ServiceOrders.listOrders" }));
    }

    [Test]
    public void PaymentActionAwaitsNativeConfirmationWithoutClaimingTheInvoiceWasPaid()
    {
        var harness = new Harness();
        harness.Action("refresh", 1, 11); harness.AssertSuccess();
        harness.Action("pay", 1, 11, "{\"id\":\"ORD-a1111111\"}");
        Assert.That(harness.Process.State, Is.EqualTo(MetaProcessState.Waiting));
        var receipt = harness.Process.CreateRecord(harness.Process.GetTypeId("meta.services.PaymentRequest"),
            [harness.Process.CreateString("native-confirmation"), MetaValue.Bool(true)]);
        Assert.That(harness.Process.TryCompleteHost(73, harness.Process.CreateResult(harness.Host.PaymentResultType, receipt)), Is.True);
        harness.Drain(); harness.AssertSuccess();
        using var view = harness.View(1, 11);
        Assert.That(view.RootElement.GetProperty("orders")[0].GetProperty("status").GetString(), Is.EqualTo("Awaiting payment"));
        Assert.That(view.RootElement.GetProperty("orders")[0].GetProperty("invoice").GetString(), Does.Not.Contain(" · paid"));
        Assert.That(harness.Host.Calls, Is.EqualTo(new[] { "meta.services.ServiceOrders.listOrders", "meta.services.ServiceOrders.requestPayment" }));
    }

    [Test]
    public void HiddenRowSelectionBelongsToTheLiveSessionAndMustStillBePermittedByItsPrivateCache()
    {
        var harness = new Harness();
        harness.Action("refresh", 1, 11); harness.AssertSuccess();
        harness.Action("refresh", 2, 22); harness.AssertSuccess();
        harness.Action("select", 1, 11, "{\"order\":\"a1111111-1111-4111-8111-111111111111\"}"); harness.AssertSuccess();
        using (var alice = harness.View(1, 11))
        {
            Assert.That(alice.RootElement.GetProperty("hasSelection").GetBoolean(), Is.True);
            Assert.That(alice.RootElement.GetProperty("selection").GetString(), Does.Contain("Alice").And.Not.Contain("1111-4111"));
        }
        using (var bob = harness.View(2, 22)) Assert.That(bob.RootElement.GetProperty("hasSelection").GetBoolean(), Is.False);
        harness.Action("select", 2, 22, "{\"order\":\"a1111111-1111-4111-8111-111111111111\"}");
        Assert.That(harness.Process.GetVariantTag(harness.Process.LastResult!.Value), Is.EqualTo(1), "A forged hidden row key is not permission.");
        harness.Action("select", 2, 22, "{\"order\":\"b2222222-2222-4222-8222-222222222222\"}"); harness.AssertSuccess();
        using (var bob = harness.View(2, 22)) Assert.That(bob.RootElement.GetProperty("selection").GetString(), Does.Contain("Bob").And.Not.Contain("Alice"));
        using (var alice = harness.View(1, 11)) Assert.That(alice.RootElement.GetProperty("selection").GetString(), Does.Contain("Alice"));

        harness.Revoke(1, 11);
        harness.Host.Revoked.Remove(11);
        harness.Action("refresh", 1, 11); harness.AssertSuccess();
        using (var reopened = harness.View(1, 11)) Assert.That(reopened.RootElement.GetProperty("hasSelection").GetBoolean(), Is.False,
            "Reusing a fake host token with a fresh opaque ref never restores a closed session's selection.");
        harness.Action("select", 1, 11, "{\"order\":\"a1111111-1111-4111-8111-111111111111\"}"); harness.AssertSuccess();
        harness.Host.Warm.Remove(11);
        using var stale = harness.View(1, 11);
        Assert.That(stale.RootElement.GetProperty("hasSelection").GetBoolean(), Is.False);
        Assert.That(stale.RootElement.GetProperty("selectedId").GetString(), Is.Empty);
    }
}
