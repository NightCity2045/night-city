// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Language;
using NUnit.Framework;

namespace Content.Meta.Tests.Language;

[TestFixture]
public sealed class MetaDoorSafetyTests
{
    [Test]
    public void TheCatalogBeforeDoorSafetyStaysTrustedAndLacksTheNewMember()
    {
        var old = MetaApiCatalog.ResolveTrustedCatalog(MetaApiCatalog.PreDoorSafetyCatalogHash);
        Assert.That(old.Hash, Is.EqualTo(MetaApiCatalog.PreDoorSafetyCatalogHash));
        Assert.That(MetaApiCatalog.Default.Hash, Is.Not.EqualTo(MetaApiCatalog.PreDoorSafetyCatalogHash));
        Assert.That(old.Members.Any(member => member.QualifiedName == "meta.doors.Door.setSafety"), Is.False);
        var current = MetaApiCatalog.Default.Members.Single(member => member.QualifiedName == "meta.doors.Door.setSafety");
        Assert.That(current.Scope, Is.EqualTo("door.safety"));
    }

    [Test]
    public void TheCatalogBeforeDoorOccupancyStaysTrustedAndLacksTheEvent()
    {
        var old = MetaApiCatalog.ResolveTrustedCatalog(MetaApiCatalog.PreDoorOccupancyCatalogHash);
        Assert.That(old.Hash, Is.EqualTo(MetaApiCatalog.PreDoorOccupancyCatalogHash));
        Assert.That(old.Members.Any(member => member.QualifiedName == "meta.doors.Door.occupancyChanged"), Is.False);
        Assert.That(MetaApiCatalog.Default.Members.Any(member => member.QualifiedName == "meta.doors.Door.occupancyChanged"), Is.True);
    }

    [Test]
    public void AProgramMayTurnTheInterlockOffAndDriveTheDoorFromCameraMotion()
    {
        const string source = """
            module trap.door;
            import meta.system;
            import meta.doors;

            link door: Door = "trap-door";

            async fn main() -> void {
                match (await door.setSafety(false)) {
                    ok(state) => { }
                    err(problem) => { system.log(problem.message); }
                }
            }

            async watch door.occupancyChanged(doorway) {
                if (doorway.occupied) {
                    match (await door.setOpen(false)) {
                        ok(state) => { }
                        err(problem) => { system.log(problem.message); }
                    }
                }
            }

            async watch door.stateChanged(state) {
                if (state.phase == DoorPhase.Closed) {
                    match (await door.setOpen(true)) {
                        ok(opened) => { }
                        err(problem) => { system.log(problem.message); }
                    }
                }
            }
            """;
        var compiled = new MetaCompiler().Compile(new([new("main.meta", source)], "trap.door"));
        Assert.That(compiled.Success, Is.True, string.Join(';', compiled.Diagnostics.Select(d => d.Code)));
    }
}
