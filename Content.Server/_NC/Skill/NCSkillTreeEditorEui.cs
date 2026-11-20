// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.IO;
using System.Linq;
using System.Numerics;
using Content.Server.Administration;
using Content.Server.Administration.Managers;
using Content.Server.EUI;
using Content.Shared._NC.Skill;
using Content.Shared.Administration;
using Content.Shared.Eui;
using Robust.Shared.Console;
using Robust.Shared.ContentPack;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Content.Server._NC.Skill;

public sealed partial class NCSkillTreeEditorEui : BaseEui
{
    [Dependency] private IAdminManager _admins = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IResourceManager _resources = default!;

    private const int MaxDraftBytes = 512 * 1024;
    private static readonly ResPath UserDirectory = new("/skill_tree_editor");

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);
        if (msg is not NCSkillEditorMessage request)
            return;

        if (_admins.GetAdminData(Player)?.HasFlag(AdminFlags.Debug) != true)
        {
            Close();
            return;
        }

        if (string.IsNullOrEmpty(request.Tree) || request.Tree.Length > 100 ||
            !request.Tree.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_') ||
            !_prototypes.HasIndex<NCSkillTreePrototype>(request.Tree))
        {
            Reply(request.Tree, "nc-skill-editor-invalid");
            return;
        }

        var file = request.Tree + ".yml";
        try
        {
            if (request.Nodes == null)
                Load(request.Tree, file);
            else
                Save(request.Tree, file, request.Nodes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException)
        {
            Reply(request.Tree, "nc-skill-editor-io-error");
        }
    }

    private void Load(string tree, string file)
    {
        var path = UserDirectory / file;
        if (_resources.UserData.Exists(path))
        {
            using var stream = _resources.UserData.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(stream);
            Parse(tree, reader, stream.Length);
            return;
        }

        if (FindRepositoryDirectory() is { } directory && File.Exists(Path.Combine(directory, file)))
        {
            using var stream = File.OpenRead(Path.Combine(directory, file));
            using var reader = new StreamReader(stream);
            Parse(tree, reader, stream.Length);
            return;
        }

        Reply(tree, "nc-skill-editor-no-draft");
    }

    private void Parse(string tree, TextReader reader, long length)
    {
        if (length > MaxDraftBytes)
        {
            Reply(tree, "nc-skill-editor-invalid");
            return;
        }

        var draft = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build().Deserialize<Draft>(reader);
        var nodes = draft?.Skills?.Select(skill => skill?.ToNode()).ToArray();
        if (draft?.Version != 1 || draft.Tree != tree || nodes == null || nodes.Any(node => node == null))
        {
            Reply(tree, "nc-skill-editor-invalid");
            return;
        }

        var validNodes = nodes.OfType<NCSkillNode>().ToArray();
        if (!IsValid(tree, validNodes))
        {
            Reply(tree, "nc-skill-editor-invalid");
            return;
        }

        SendMessage(new NCSkillEditorResult(tree, validNodes, "nc-skill-editor-loaded"));
    }

    private void Save(string tree, string file, NCSkillNode[] nodes)
    {
        if (!IsValid(tree, nodes))
        {
            Reply(tree, "nc-skill-editor-invalid");
            return;
        }

        var draft = new Draft
        {
            Tree = tree,
            Skills = nodes.OrderBy(node => node.Id, StringComparer.Ordinal).Select(node => new DraftSkill
            {
                Id = node.Id,
                X = node.Position.X,
                Y = node.Position.Y,
                Prerequisites = node.Prerequisites,
                AnyPrerequisites = node.AnyPrerequisites,
            }).ToArray(),
        };
        var yaml = new SerializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build().Serialize(draft);
        if (System.Text.Encoding.UTF8.GetByteCount(yaml) > MaxDraftBytes)
        {
            Reply(tree, "nc-skill-editor-invalid");
            return;
        }

        _resources.UserData.CreateDir(UserDirectory);
        if (_resources.UserData.RootDir is { } root)
            WriteDraft(Path.Combine(root, UserDirectory.ToString().TrimStart('/'), file), yaml);
        else
            _resources.UserData.WriteAllText(UserDirectory / file, yaml);

        if (FindRepositoryDirectory() is { } directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                WriteDraft(Path.Combine(directory, file), yaml);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Reply(tree, "nc-skill-editor-repo-error");
                return;
            }
        }

        Reply(tree, "nc-skill-editor-saved");
    }

    private bool IsValid(string tree, NCSkillNode[] nodes)
    {
        if (nodes.Length > 1000)
            return false;

        var edited = new Dictionary<string, NCSkillNode>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node == null || string.IsNullOrEmpty(node.Id) ||
                !_prototypes.TryIndex<NCSkillPrototype>(node.Id, out var skill) || skill.Tree.Id != tree ||
                !float.IsFinite(node.Position.X) || !float.IsFinite(node.Position.Y) ||
                MathF.Abs(node.Position.X) > 100000 || MathF.Abs(node.Position.Y) > 100000 ||
                node.Prerequisites == null || node.AnyPrerequisites == null ||
                node.Prerequisites.Length > 1000 || node.AnyPrerequisites.Length > 1000 ||
                node.AnyPrerequisites.Any(group => group is not { Length: > 0 and <= 1000 }) ||
                !edited.TryAdd(node.Id, node))
            {
                return false;
            }
        }

        var graph = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var skill in _prototypes.EnumeratePrototypes<NCSkillPrototype>())
        {
            var dependencies = edited.TryGetValue(skill.ID, out var node)
                ? node.Prerequisites.Concat(node.AnyPrerequisites.SelectMany(group => group))
                : skill.Prerequisites.Concat(skill.AnyPrerequisites.SelectMany(group => group)).Select(id => id.Id);

            var ids = dependencies.ToArray();
            if (ids.Any(id => string.IsNullOrEmpty(id) || !_prototypes.HasIndex<NCSkillPrototype>(id)))
                return false;

            graph.Add(skill.ID, ids);
        }

        var remaining = graph.ToDictionary(pair => pair.Key, pair => pair.Value.Length, StringComparer.Ordinal);
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (id, requirements) in graph)
        {
            foreach (var prerequisite in requirements)
            {
                if (!dependents.TryGetValue(prerequisite, out var dependent))
                    dependents.Add(prerequisite, dependent = []);

                dependent.Add(id);
            }
        }

        var resolved = new Queue<string>(remaining.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var visited = 0;
        while (resolved.TryDequeue(out var id))
        {
            visited++;
            if (dependents.TryGetValue(id, out var next))
            {
                foreach (var other in next)
                {
                    if (--remaining[other] == 0)
                        resolved.Enqueue(other);
                }
            }
        }

        return visited == graph.Count;
    }

    private void Reply(string tree, LocId status) => SendMessage(new NCSkillEditorResult(tree, null, status));

    private static void WriteDraft(string path, string yaml)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, yaml);
        File.Move(temporary, path, overwrite: true);
    }

    private static string? FindRepositoryDirectory()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var current = new DirectoryInfo(start); current != null; current = current.Parent)
            {
                if (File.Exists(Path.Combine(current.FullName, "SpaceStation14.slnx")) &&
                    Directory.Exists(Path.Combine(current.FullName, "Resources", "Prototypes")))
                {
                    return Path.Combine(current.FullName, "Resources", "SkillTreeEditor");
                }
            }
        }

        return null;
    }

    public sealed class Draft
    {
        public int Version { get; set; } = 1;
        public string Tree { get; set; } = "";
        public DraftSkill[] Skills { get; set; } = [];
    }

    public sealed class DraftSkill
    {
        public string Id { get; set; } = "";
        public float X { get; set; }
        public float Y { get; set; }
        public string[] Prerequisites { get; set; } = [];
        public string[][] AnyPrerequisites { get; set; } = [];

        public NCSkillNode ToNode() => new(Id, new Vector2(X, Y), Prerequisites, AnyPrerequisites);
    }
}

[AdminCommand(AdminFlags.Debug)]
public sealed partial class NCSkillTreeEditorCommand : IConsoleCommand
{
    [Dependency] private EuiManager _eui = default!;

    public string Command => "skilltreeeditor";
    public string Description => Loc.GetString("nc-skill-editor-command");
    public string Help => Command;

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player is { } player)
            _eui.OpenEui(new NCSkillTreeEditorEui(), player);
    }
}
