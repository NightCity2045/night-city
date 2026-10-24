// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text;
using Content.Meta.Persistence;
using NUnit.Framework;

namespace Content.Meta.Tests.Persistence;

[TestFixture]
public sealed class MetaStateTransformerTests
{
    [Test]
    public void RenameAndDefaultsPreserveOriginalDataAndDoNotOverwriteCustomerSettings()
    {
        var bytes = Encoding.UTF8.GetBytes("Привет 👩‍💻");
        MetaStateTransformFile[] source = [new("legacy.txt", bytes, 3), new("language.txt", "ru-RU"u8.ToArray(), 5)];
        MetaStateTransformStep[] steps =
        [
            new(MetaStateTransformKind.Rename, "legacy.txt", "settings/message.txt"),
            new(MetaStateTransformKind.SetDefault, "language.txt", Text: "en-US"),
            new(MetaStateTransformKind.SetDefault, "new.txt", Text: "Новая настройка"),
        ];
        Assert.That(MetaStateTransformer.TryTransform(source, steps, 4096, out var result), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(source[0].Path, Is.EqualTo("legacy.txt"));
            Assert.That(source[0].Revision, Is.EqualTo(3));
            Assert.That(source[0].Bytes, Is.EqualTo(bytes));
            Assert.That(result.Single(file => file.Path == "settings/message.txt").Revision, Is.EqualTo(4));
            Assert.That(result.Single(file => file.Path == "settings/message.txt").Bytes, Is.EqualTo(bytes));
            Assert.That(Encoding.UTF8.GetString(result.Single(file => file.Path == "language.txt").Bytes), Is.EqualTo("ru-RU"));
        });
    }

    [Test]
    public void CollisionAndInvalidUnicodeOrPathsFailWithoutPublishingPartialData()
    {
        MetaStateTransformFile[] source = [new("a.txt", [1], 1), new("b.txt", [2], 1)];
        foreach (var step in new[]
                 {
                     new MetaStateTransformStep(MetaStateTransformKind.Rename, "a.txt", "b.txt"),
                     new MetaStateTransformStep(MetaStateTransformKind.Rename, "a.txt", "../escape.txt"),
                     new MetaStateTransformStep(MetaStateTransformKind.SetDefault, "new.txt", Text: "\ud800"),
                     new MetaStateTransformStep((MetaStateTransformKind) 255, "a.txt"),
                 })
        {
            Assert.That(MetaStateTransformer.TryTransform(source, [step], 4096, out var result), Is.False);
            Assert.That(result, Is.Empty);
            Assert.That(source.Select(file => file.Path), Is.EqualTo(new[] { "a.txt", "b.txt" }));
        }
    }

    [Test]
    public void ATrustedDeleteCanShrinkOldDataIntoTheNewCapacityWithoutChangingTheSource()
    {
        var obsolete = Encoding.UTF8.GetBytes("old retained settings");
        MetaStateTransformFile[] source =
            [new("obsolete.txt", obsolete, 4), new("keep.txt", [7], 2)];
        MetaStateTransformStep[] steps =
        [
            new(MetaStateTransformKind.Delete, "obsolete.txt"),
            new(MetaStateTransformKind.SetDefault, "new.txt", Text: "x"),
        ];

        Assert.That(MetaStateTransformer.TryTransform(source, steps, 2, out var result), Is.True,
            "The target capacity applies to transformed data, independently of the bounded old input.");
        Assert.Multiple(() =>
        {
            Assert.That(result.Select(file => file.Path), Is.EqualTo(new[] { "keep.txt", "new.txt" }));
            Assert.That(result.Sum(file => file.Bytes.Length), Is.EqualTo(2));
            Assert.That(source[0].Path, Is.EqualTo("obsolete.txt"));
            Assert.That(source[0].Bytes, Is.EqualTo(obsolete));
            Assert.That(source[0].Revision, Is.EqualTo(4));
            Assert.That(source[1].Revision, Is.EqualTo(2));
        });
        Assert.That(MetaStateTransformer.TryTransform(source, steps, 1, out var overCapacity), Is.False);
        Assert.That(overCapacity, Is.Empty, "A transformed output which still does not fit must not be published.");
    }

    [Test]
    public void QuotasApplyBeforeAStageCanBeReturned()
    {
        Assert.That(MetaStateTransformer.TryTransform([], [new(MetaStateTransformKind.SetDefault, "x", Text: "ё")],
            1, out _), Is.False, "Capacity counts UTF-8 bytes.");
        Assert.That(MetaStateTransformer.TryTransform([], Enumerable.Repeat(
            new MetaStateTransformStep(MetaStateTransformKind.Delete, "x"), MetaStateTransformer.MaximumSteps + 1).ToArray(),
            4096, out _), Is.False);
        Assert.That(MetaStateTransformer.TryTransform([new("x", new byte[32769], 1)], [], 65536, out _), Is.False);
        Assert.That(MetaStateTransformer.TryTransform([new("x", [1], long.MaxValue)],
            [new(MetaStateTransformKind.Rename, "x", "y")], 4096, out _), Is.False);
    }
}
