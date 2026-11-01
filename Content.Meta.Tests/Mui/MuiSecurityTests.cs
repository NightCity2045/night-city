// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Immutable;
using Content.Meta.Mui;
using NUnit.Framework;

namespace Content.Meta.Tests.Mui;

[TestFixture]
public sealed class MuiSecurityTests
{
    private static readonly ImmutableDictionary<string, MuiField> Fields = new Dictionary<string, MuiField>
    {
        ["name"] = new(MuiValueKind.String, MaxLength: 32),
        ["enabled"] = new(MuiValueKind.Boolean),
        ["rows"] = new(MuiValueKind.Array, MaxLength: 64),
        ["optional"] = new(MuiValueKind.String, false),
    }.ToImmutableDictionary();
    private static readonly MuiViewContract View = new("view", "ui/main.mui", Fields, ["submit"]);
    private static readonly MuiAppContract Contract = new("1.0",
        new Dictionary<string, MuiViewContract> { ["main"] = View }.ToImmutableDictionary(),
        new Dictionary<string, MuiActionContract>
        {
            ["submit"] = new("submit", new Dictionary<string, MuiField> { ["name"] = new(MuiValueKind.String, MaxLength: 32) }.ToImmutableDictionary(), []),
        }.ToImmutableDictionary(), ImmutableHashSet<string>.Empty);

    [Test]
    public void ValidDocumentBindsOnlyDeclaredFieldsAndActions()
    {
        const string source = "<Page id='root'><Column id='layout'><Text id='name' text='{bind name}'/><Button id='submit' action='submit' enabled='{bind enabled}' text='Save'/></Column></Page>";
        Assert.That(MuiParser.TryParse(source, View, Contract, out var result, out var diagnostic), Is.True, diagnostic?.Code);
        Assert.That(result!.NodeCount, Is.EqualTo(4));
    }

    [Test]
    public void ComponentsExpandWithInstanceIdsAndGlobalBudgets()
    {
        var contract = Contract with { Resources = ImmutableHashSet.Create("components/card.mui") };
        var resources = new Dictionary<string, string> { ["components/card.mui"] = "<Page id='card'><Text id='label' text='{bind name}'/></Page>" };
        const string source = "<Page id='root'><Component id='first' resource='components/card.mui'/><Component id='second' resource='components/card.mui'/></Page>";
        Assert.That(MuiParser.TryParse(source, View, contract, resources, out var parsed, out _), Is.True);
        Assert.That(parsed!.Root.Children[0].Children[0].Children[0].Id, Is.EqualTo("first_label"));
        Assert.That(parsed.Root.Children[1].Children[0].Children[0].Id, Is.EqualTo("second_label"));
        resources["components/card.mui"] = "<Page id='card'><Component id='loop' resource='components/card.mui'/></Page>";
        Assert.That(MuiParser.TryParse(source, View, contract, resources, out _, out _), Is.False);
    }

    [Test]
    public void ExpandedIdCollisionAndUnknownAssetAliasAreRejected()
    {
        var contract = Contract with { Resources = ImmutableHashSet.Create("components/card.mui", "images/not-native") };
        var resources = new Dictionary<string, string> { ["components/card.mui"] = "<Page id='card'><Text id='label'/></Page>" };
        Assert.That(MuiParser.TryParse("<Page id='root'><Text id='first_label'/><Component id='first' resource='components/card.mui'/></Page>",
            View, contract, resources, out _, out _), Is.False);
        Assert.That(MuiParser.TryParse("<Page id='root'><Image id='image' resource='images/not-native'/></Page>", View, contract, out _, out _), Is.False);
        Assert.That(MuiStyleSheet.TryParse("{\"version\":\"1.0\",\"rules\":[{\"selector\":\"#root\",\"properties\":{\"font\":\"images/not-native\"}}]}",
            contract, out _, out _), Is.False);
    }

    [TestCase("<!DOCTYPE Page [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><Page id='root'><Text id='text'>&x;</Text></Page>")]
    [TestCase("<Page id='root' xmlns:x='clr-namespace:System'><x:Object id='evil'/></Page>")]
    [TestCase("<Page id='root'><Text id='x' text='{Binding name}'/></Page>")]
    [TestCase("<Page id='root'><Text id='x' text='{bind name.ToString()}'/></Page>")]
    [TestCase("<Page id='root'><Button id='x' action='admin' /></Page>")]
    [TestCase("<Page id='root'><Button id='x' onClick='evil' /></Page>")]
    [TestCase("<Page id='root'><Text id='root' /></Page>")]
    [TestCase("<Page id='root'><Text id='x' visible='{bind name}' /></Page>")]
    [TestCase("<Page id='root'><Image id='x' resource='https://example.com/a.png' /></Page>")]
    [TestCase("<?evil execute?><Page id='root'/>")]
    public void HostileDocumentIsRejected(string source)
    {
        Assert.That(MuiParser.TryParse(source, View, Contract, out var result, out _), Is.False);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void DepthAndActiveControlBudgetsAreEnforced()
    {
        var deep = string.Concat(Enumerable.Range(0, 33).Select(i => $"<Column id='n{i}'>"));
        deep = "<Page id='root'>" + deep + string.Concat(Enumerable.Repeat("</Column>", 33)) + "</Page>";
        Assert.That(MuiParser.TryParse(deep, View, Contract, out _, out _), Is.False);
        var wide = "<Page id='root'>" + string.Concat(Enumerable.Range(0, 513).Select(i => $"<Text id='n{i}'/>")) + "</Page>";
        Assert.That(MuiParser.TryParse(wide, View, Contract, out _, out _), Is.False);
    }

    [Test]
    public void PatchGapRequestsResyncWithoutMutation()
    {
        using var state = new MuiSessionState(Guid.NewGuid(), Fields);
        Assert.That(state.TryApplySnapshot(3, "{\"name\":\"Alice\",\"enabled\":true,\"rows\":[]}"), Is.EqualTo(MuiUpdateResult.Applied));
        Assert.That(state.TryApplyPatch(2, 4, "{\"set\":{\"name\":\"Bob\"}}"), Is.EqualTo(MuiUpdateResult.Gap));
        Assert.That(state.Read("name")!.GetValue<string>(), Is.EqualTo("Alice"));
        Assert.That(state.TryApplyPatch(3, 4, "{\"set\":{\"name\":\"Bob\"},\"remove\":[\"enabled\"]}"), Is.EqualTo(MuiUpdateResult.Invalid));
        Assert.That(state.Revision, Is.EqualTo(3));
        Assert.That(state.Read("name")!.GetValue<string>(), Is.EqualTo("Alice"));
        Assert.That(state.TryApplyPatch(3, 4, "{\"set\":{\"name\":\"Bob\"}}"), Is.EqualTo(MuiUpdateResult.Applied));
        Assert.That(state.TryApplyPatch(3, 4, "{\"set\":{\"name\":\"Eve\"}}"), Is.EqualTo(MuiUpdateResult.Duplicate));
        Assert.That(state.Read("name")!.GetValue<string>(), Is.EqualTo("Bob"));
        Assert.That(state.TryApplySnapshot(4, "{\"name\":\"Mallory\",\"enabled\":true,\"rows\":[]}"), Is.EqualTo(MuiUpdateResult.Invalid));
        Assert.That(state.Read("name")!.GetValue<string>(), Is.EqualTo("Bob"));
    }

    [TestCase("{\"name\":17,\"enabled\":true,\"rows\":[]}")]
    [TestCase("{\"name\":\"Alice\",\"name\":\"Bob\",\"enabled\":true,\"rows\":[]}")]
    [TestCase("{\"name\":\"Alice\",\"enabled\":true,\"rows\":[],\"secret\":1}")]
    public void InvalidViewDataIsRejected(string json) => Assert.That(MuiDataValidator.TryValidate(json, Fields, MuiLimits.InitialStateBytes, out _), Is.False);

    [Test]
    public void DisposeReleasesSessionProjection()
    {
        var state = new MuiSessionState(Guid.NewGuid(), Fields);
        state.TryApplySnapshot(0, "{\"name\":\"Alice\",\"enabled\":true,\"rows\":[]}");
        state.Dispose();
        Assert.That(state.Read("name"), Is.Null);
        Assert.That(state.TryApplyPatch(0, 1, "{}"), Is.EqualTo(MuiUpdateResult.Closed));
    }

    [TestCase("{\"rules\":[{\"selector\":\"System.Button\",\"properties\":{}}]}")]
    [TestCase("{\"rules\":[{\"selector\":\"#root\",\"properties\":{\"script\":\"evil\"}}]}")]
    [TestCase("{\"rules\":[{\"selector\":\"#root\",\"properties\":{\"image\":\"../secrets\"}}]}")]
    [TestCase("{\"tokens\":{\"a\":\"$b\",\"b\":\"$a\"},\"rules\":[{\"selector\":\"#root\",\"properties\":{\"color\":\"$a\"}}]}")]
    public void UnsafeStyleIsRejected(string style) => Assert.That(MuiStyleSheet.TryParse(style, Contract, out _, out _), Is.False);

    [Test]
    public void DeterministicMalformedInputCorpusDoesNotThrow()
    {
        var random = new Random(0x4D5549);
        var alphabet = "<>/'\"{}[]=!&;:abc123 \n";
        for (var i = 0; i < 500; i++)
        {
            var text = new string(Enumerable.Range(0, random.Next(0, 512)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            Assert.DoesNotThrow(() => MuiParser.TryParse(text, View, Contract, out _, out _));
            Assert.DoesNotThrow(() => MuiStyleSheet.TryParse(text, Contract, out _, out _));
            Assert.DoesNotThrow(() => MuiContractParser.TryParse(text, out _, out _));
            Assert.DoesNotThrow(() => MuiDataValidator.TryValidate(text, Fields, MuiLimits.InitialStateBytes, out _));
        }
    }

    [Test]
    public void AppContractChecksPublicEntrypointAbi()
    {
        const string json = """
            {"version":"1.0","views":{"main":{"function":"view","document":"ui/main.mui","fields":{"name":{"type":"string"}},"actions":["submit"]}},
             "actions":{"submit":{"function":"submit","inputs":{"name":{"type":"string","maxLength":32}},"scopes":["app.write"]}},"resources":[]}
            """;
        Assert.That(MuiContractParser.TryParse(json, out var contract, out _), Is.True);
        Assert.That(contract!.TryValidateExports([
            new("view", true, ["meta.app.AppContext"], "ViewState"),
            new("submit", true, ["meta.app.AppContext", "ActionInput"], "result<void>"),
        ], out _), Is.True);
        Assert.That(contract.TryValidateExports([
            new("view", false, ["meta.app.AppContext"], "ViewState"),
            new("submit", true, ["meta.app.AppContext", "ActionInput"], "result<void>"),
        ], out _), Is.False);
    }

    [TestCase("{\"version\":\"1.0\",\"views\":{},\"actions\":{},\"clrType\":\"System.Object\"}")]
    [TestCase("{\"version\":\"1.0\",\"views\":{},\"actions\":{},\"resources\":[\"../secret\"]}")]
    public void UnsafeAppContractIsRejected(string json) => Assert.That(MuiContractParser.TryParse(json, out _, out _), Is.False);

    [Test]
    public void StylesResolveTokensAndBoundedEffects()
    {
        const string json = """
            {"version":"1.0","tokens":{"accent":"#0088FF"},"rules":[
              {"selector":"#root","minWidth":320,"maxWidth":1024,"properties":{"color":"$accent","gradient":"#001122,#224466","animation":"pulse:1000:0.5:1"}}]}
            """;
        Assert.That(MuiStyleSheet.TryParse(json, Contract, out var style, out _), Is.True);
        Assert.That(style!.Rules[0].Properties["color"], Is.EqualTo("#0088FF"));
        Assert.That(MuiStyleSheet.TryParse(json.Replace("pulse:1000", "pulse:0", StringComparison.Ordinal), Contract, out _, out _), Is.False);
    }

    [Test]
    public void TableRowActionRequiresDeclaredStringInputAndHiddenExplicitKey()
    {
        const string table = "<Page id='root'><Table id='rows' items='{bind rows}' rowAction='submit' rowKey='id' rowField='name' columns='summary status'/></Page>";
        Assert.That(MuiParser.TryParse(table, View, Contract, out _, out _), Is.True);
        Assert.That(MuiParser.TryParse(table.Replace("rowField='name'", "rowField='unknown'", StringComparison.Ordinal), View, Contract, out _, out _), Is.False);
        Assert.That(MuiParser.TryParse(table.Replace("rowKey='id'", "", StringComparison.Ordinal), View, Contract, out _, out _), Is.False);
        Assert.That(MuiParser.TryParse(table.Replace("summary status", "id summary", StringComparison.Ordinal), View, Contract, out _, out _), Is.False);
        Assert.That(MuiParser.TryParse("<Page id='root'><Grid id='grid' columns='2'/></Page>", View, Contract, out _, out _), Is.True);
    }

    [TestCase("Chart")]
    [TestCase("Map")]
    [TestCase("Schematic")]
    public void AllPlotKindsShareTheEightPlotBudget(string kind)
    {
        var body = string.Concat(Enumerable.Range(0, 8).Select(i => $"<{kind} id='p{i}' items='{{bind rows}}'/>"));
        Assert.That(MuiParser.TryParse("<Page id='root'>" + body + "</Page>", View, Contract, out _, out _), Is.True);
        Assert.That(MuiParser.TryParse("<Page id='root'>" + body + $"<{kind} id='extra'/></Page>", View, Contract, out _, out _), Is.False);
        Assert.That(MuiParser.TryParse("<Page id='root'><Chart id='plot' min='1' max='1'/></Page>", View, Contract, out _, out _), Is.False);
    }

    [Test]
    public void StyleVersionFontSizeAndExpandedAnimationBudgetAreValidated()
    {
        Assert.That(MuiStyleSheet.TryParse("{\"rules\":[]}", Contract, out _, out _), Is.False);
        const string style = "{\"version\":\"1.0\",\"rules\":[{\"selector\":\".pulse\",\"properties\":{\"fontSize\":\"13\",\"animation\":\"pulse:1000:0.5:1\"}}]}";
        Assert.That(MuiStyleSheet.TryParse(style, Contract, out var parsed, out _), Is.True);
        Assert.That(MuiStyleSheet.TryParse(style.Replace("\"13\"", "\"49\"", StringComparison.Ordinal), Contract, out _, out _), Is.False);
        var nodes = string.Concat(Enumerable.Range(0, 16).Select(i => $"<Text id='n{i}' class='pulse'/>"));
        Assert.That(MuiParser.TryParse("<Page id='root'>" + nodes + "</Page>", View, Contract, out var document, out _), Is.True);
        Assert.That(parsed!.FitsBudget(document!), Is.True);
        Assert.That(MuiParser.TryParse("<Page id='root'>" + nodes + "<Text id='extra' class='pulse'/></Page>", View, Contract, out document, out _), Is.True);
        Assert.That(parsed.FitsBudget(document!), Is.False);
    }
}
