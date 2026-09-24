// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.IO;
using System.Text.Json;
using AstraGraph.Editor.Protocol;
using Content.Shared._NC.AstraGraph;
using Content.Shared._NC.AstraGraph.Bindings;
using Content.Shared.Administration;
using NUnit.Framework;

namespace Content.Tests.Shared._NC.AstraGraph;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class AstraGraphHostTests
{
    [Test]
    public void AdminFlag_UsesTheFreeBitAstraGraphExpects()
    {
        Assert.That((uint)AdminFlags.AstraGraph, Is.EqualTo(1u << 24));
        Assert.That(AdminFlagsHelper.NameToFlag("ASTRAGRAPH"), Is.EqualTo(AdminFlags.AstraGraph));
    }

    [Test]
    public void RankMap_DoesNotGrantPublishToAPlainAdmin()
    {
        Assert.That(AstraAdminRankMap.Map("Game Admin", isHost: false), Is.EqualTo(AstraAdminRankMap.Developer));
        Assert.That(AstraAdminRankMap.Map(null, isHost: false), Is.EqualTo(AstraAdminRankMap.Developer));
        Assert.That(AstraAdminRankMap.Map("Publisher", isHost: false), Is.EqualTo(AstraAdminRankMap.Publisher));
        Assert.That(AstraAdminRankMap.Map("Head Admin", isHost: false), Is.EqualTo(AstraAdminRankMap.Maintainer));
        Assert.That(AstraAdminRankMap.Map("Developer", isHost: true), Is.EqualTo(AstraAdminRankMap.Maintainer));
    }

    [Test]
    public void BindingFilter_KeepsOnlyNightCityBindings()
    {
        Assert.That(AstraContentBindings.IsIndexedType(typeof(AstraContentBindings)), Is.True);
        Assert.That(AstraContentBindings.IsIndexedType(typeof(AdminFlags)), Is.False);
        Assert.That(AstraContentBindings.IsIndexedType(typeof(string)), Is.False);
    }

    [Test]
    public void AuthoringCodec_RoundtripsPing()
    {
        var bytes = AstraAuthoringCodec.Serialize(new PingMsg());
        var message = AstraAuthoringCodec.Deserialize("ping", bytes);
        Assert.That(message, Is.InstanceOf<PingMsg>());
    }

    [Test]
    public void HtmlDocumentPath_UsesTheDocumentName()
    {
        Assert.That(AstraHtmlDocumentPaths.FileName("Vehicle Maintenance"), Is.EqualTo("Vehicle Maintenance"));
        Assert.That(AstraHtmlDocumentPaths.FileName("../secret"), Is.Not.EqualTo("../secret"));
        Assert.That(AstraHtmlDocumentPaths.ResourcePath("Panel"), Is.EqualTo("/AstraGraph/Ui/Panel.json"));
    }

    [Test]
    public void AuthoringCodec_RoundtripsUiPreview()
    {
        var request = new UiPreviewRequestMsg
        {
            SessionId = "studio",
            Document = new UiDocumentDto(
                "ui-1",
                "Panel",
                400,
                300,
                new UiNodeDto("root", "Window", "Panel", "Panel", []))
        };

        var bytes = AstraAuthoringCodec.Serialize(request);
        var message = AstraAuthoringCodec.Deserialize("ui.preview.request", bytes);

        Assert.That(message, Is.InstanceOf<UiPreviewRequestMsg>());
        var preview = (UiPreviewRequestMsg)message!;
        Assert.That(preview.Document?.Name, Is.EqualTo("Panel"));
        Assert.That(preview.Document?.Root.Id, Is.EqualTo("root"));
    }

    [Test]
    public void GitCommit_FollowsSubmoduleGitdir()
    {
        var root = Path.Combine(Path.GetTempPath(), "astra-git-" + Guid.NewGuid().ToString("N"));
        var gitDir = Path.Combine(root, "modules", "RobustToolbox");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "81e82b01d6cc5957015a8c9f3baf5e573b0a5217\n");
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: modules/RobustToolbox\n");

        Assert.That(AstraConsumerLayout.TryReadGitCommit(root, out var commit), Is.True);
        Assert.That(commit, Is.EqualTo("81e82b01d6cc5957015a8c9f3baf5e573b0a5217"));
    }

    [Test]
    public void Locate_FindsAstraGraphBesideRobustToolbox()
    {
        var root = Path.Combine(Path.GetTempPath(), "astra-root-" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "bin", "Content.Server");
        Directory.CreateDirectory(Path.Combine(root, "AstraGraph"));
        Directory.CreateDirectory(Path.Combine(root, "RobustToolbox"));
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(root, "AstraGraph", "Compatibility.json"), "{}");

        Assert.That(AstraConsumerLayout.TryLocate(new[] { nested }, out var found), Is.True);
        Assert.That(found, Is.EqualTo(Path.GetFullPath(root)));
    }

    [Test]
    public void Checkout_RobustCommitMatchesCompatibilityManifest()
    {
        Assert.That(AstraConsumerLayout.TryLocate(out var root), Is.True, "Night City checkout was not found.");
        Assert.That(AstraConsumerLayout.TryReadGitCommit(Path.Combine(root, "RobustToolbox"), out var commit), Is.True);

        var manifestPath = Path.Combine(root, "AstraGraph", "Compatibility.json");
        var manifest = JsonSerializer.Deserialize<ManifestProbe>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.That(manifest, Is.Not.Null);
        Assert.That(commit, Is.EqualTo(manifest!.TestedRobustCommit));
    }

    private sealed class ManifestProbe
    {
        public string TestedRobustCommit { get; set; } = string.Empty;
    }
}
