// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Persistence;
using NUnit.Framework;

namespace Content.Meta.Tests;

[TestFixture]
public sealed class MetaBlobReferencesTests
{
    [Test]
    public void NativeImageDependenciesRetainBytesWithoutTreatingContainerHashesAsBlobs()
    {
        var image = new string('a', 64);
        var resource = new string('b', 64);
        var dependency = new string('c', 64);
        var container = new string('d', 64);
        var payload = $$"""{"Active":{"Digest":"{{image}}","Exports":{"ArtifactDigest":"{{container}}"},"Sources":[{"Digest":"{{resource}}"}],"Dependencies":[{"BlobDigest":"{{dependency}}","ArtifactDigest":"{{container}}"}]},"Recovery":{"Digest":"{{image}}"},"ContractHash":"{{container}}"}""";
        Assert.That(MetaBlobReferences.Extract("meta.installation", payload), Is.EqualTo(new[] { image, resource, dependency }));
    }

    [Test]
    public void CommercialReleaseAndPinnedLicenseAreBlobRoots()
    {
        var image = new string('a', 64);
        var container = new string('b', 64);
        Assert.That(MetaBlobReferences.Extract("meta.software.product",
            $$"""{"Releases":[{"ArtifactDigest":"{{image}}","ContainerDigest":"{{container}}"}]}"""), Is.EqualTo(new[] { image }));
        Assert.That(MetaBlobReferences.Extract("meta.software.license",
            $$"""{"PinnedArtifactDigest":"{{image}}","Activations":[{"ReleaseDigest":"{{image}}"}]}"""), Is.EqualTo(new[] { image }));
        Assert.That(MetaBlobReferences.Extract("meta.net.journal", "not a blob schema"), Is.Empty);
    }

    [Test]
    public void FrozenContractFixturesAndDeliveredCustomerClaimsRetainTheirRawBytes()
    {
        var fixture = new string('a', 64);
        var image = new string('b', 64);
        var source = new string('c', 64);
        var semantic = new string('d', 64);
        Assert.That(MetaBlobReferences.Extract("meta.development-contract", System.Text.Json.JsonSerializer.Serialize(new
        {
            Terms = new { Fixtures = new[] { new { Digest = fixture, Bytes = 40 } } },
            Delivery = new { ContainerDigest = semantic, ContentHash = semantic },
        })), Is.EqualTo(new[] { fixture }));
        Assert.That(MetaBlobReferences.Extract("meta.development.delivery-claim", System.Text.Json.JsonSerializer.Serialize(new
        {
            BlobDigest = image, ContainerDigest = semantic,
            Release = new { Digest = image, Sources = new[] { new { Digest = source } },
                Dependencies = new[] { new { BlobDigest = fixture, ArtifactDigest = semantic } } },
        })),
            Is.EqualTo(new[] { fixture, image, source }));
        Assert.That(MetaBlobReferences.Extract("meta.development.acceptance", "hash-only-receipt"), Is.Empty);
    }

    [Test]
    public void MalformedAndAmbiguousRootMetadataFailsClosed()
    {
        var digest = new string('a', 64);
        Assert.Throws<InvalidDataException>(() => MetaBlobReferences.Extract("meta.volume", "{\"Digest\":\"bad\"}"));
        Assert.Throws<InvalidDataException>(() => MetaBlobReferences.Extract("meta.volume",
            $$"""{"Digest":"{{digest}}","Digest":"{{digest}}"}"""));
        Assert.Throws<InvalidDataException>(() => MetaBlobReferences.Extract("meta.project", new string(' ', MetaBlobReferences.MaximumPayloadBytes + 1)));
        Assert.Catch<System.Text.Json.JsonException>(() => MetaBlobReferences.Extract("meta.project", new string('[', 33) + new string(']', 33)));
    }
}
