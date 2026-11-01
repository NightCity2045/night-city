// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Robust.UnitTesting.Pool;

namespace Content.IntegrationTests.Tests._NC.Meta;

internal static class NCMetaTestScenePrototypes
{
    [TestPrototypes]
    public const string Prototypes = """
        # SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
        
        - type: ncMetaDemoScene
          id: NCMetaDoorScene
          name: nc-meta-scene-door
          objects:
          - key: host
            prototype: NCMetaTerminal
            offset: 0, 0
          - key: door
            prototype: NCMetaDoor
            offset: 2, 0
          - key: panel
            prototype: NCMetaDoorPanel
            offset: 1, 1
          - key: sensor
            prototype: NCMetaTemperatureSensor
            offset: -1, 0
          - key: heater
            prototype: SpaceHeater
            offset: -2, 0
          - key: wrench
            prototype: Wrench
            offset: -2, -1
          - key: alarm
            prototype: NCMetaAlarm
            offset: 0, 2
          - key: camera
            prototype: NCMetaCamera
            offset: -1, 1
          - key: tool
            prototype: NCMetaCommissioningTool
            offset: -1, -1
          - key: manual
            prototype: NCMetaProgrammingManual
            offset: 1, -1
          - key: vendor
            prototype: NCMetaDevelopmentVendor
            offset: -3, 3
          cables:
          - from: host
            to: door
          - from: host
            to: panel
          - from: host
            to: sensor
          - from: host
            to: alarm
          - from: host
            to: camera
        
        - type: ncMetaDemoScene
          id: NCMetaDeviceScene
          name: nc-meta-scene-garage
          objects:
          - key: host
            prototype: NCMetaTerminal
            offset: 0, 0
          - key: vehicle
            prototype: NCMetaSupraEcu
            offset: 3, 0
          - key: lift
            prototype: NCVehicleWorkshopLift
            offset: 3, 3
          - key: scanner
            prototype: NCVehicleScanner
            offset: 0, -1
          - key: tool
            prototype: NCMetaCommissioningTool
            offset: -1, -1
          - key: wrench
            prototype: Wrench
            offset: -1, -2
          - key: screwdriver
            prototype: Screwdriver
            offset: 1, -2
          cables:
          - from: host
            to: vehicle
        
        - type: ncMetaDemoScene
          id: NCMetaNetScene
          name: nc-meta-scene-net
          objects:
          - key: host
            prototype: NCMetaDemoNetAccessPoint
            offset: 0, 0
          - key: deck
            prototype: NCMetaCyberdeck
            offset: 0, -1
          - key: data
            prototype: NCMetaDemoNetData
            offset: 2, 0
          - key: control
            prototype: NCMetaDemoNetControl
            offset: -2, 0
          - key: firewall
            prototype: NCMetaDemoNetFirewall
            offset: 0, 2
          - key: blackIce
            prototype: NCMetaDemoNetBlackIce
            offset: 2, 2
          - key: door
            prototype: NCMetaDemoNetworkDoor
            offset: -2, 2
          cables:
          - from: host
            to: data
          - from: host
            to: control
          - from: host
            to: firewall
          - from: data
            to: blackIce
          - from: control
            to: door
        
        - type: ncMetaDemoScene
          id: NCMetaPatrolDeliveryScene
          name: nc-meta-scene-patrol
          objects:
          - key: host
            prototype: NCMetaTerminal
            offset: 0, 0
          - key: drone
            prototype: NCMetaCargoDrone
            offset: 2, 0
          - key: waypointA
            prototype: NCMetaWaypointA
            offset: 3, 2
          - key: waypointB
            prototype: NCMetaWaypointB
            offset: -3, 2
          - key: loading
            prototype: NCMetaLoadingWaypoint
            offset: 3, -2
          - key: tool
            prototype: NCMetaCommissioningTool
            offset: 0, -1
          - key: cargo
            prototype: SheetSteel1
            offset: 2, 1
          - key: storage
            prototype: CrateGeneric
            offset: 3, 3
          cables:
          - from: host
            to: drone
        
        - type: ncMetaDemoScene
          id: NCMetaConveyorScene
          name: nc-meta-scene-conveyor
          objects:
          - key: host
            prototype: NCMetaTerminal
            offset: 0, 0
          - key: conveyor
            prototype: NCMetaConveyor
            offset: 2, 0
          - key: sensor
            prototype: NCMetaLoadCell
            offset: 2, 1
          - key: fabricator
            prototype: NCMetaFabricator
            offset: -2, 0
          - key: materials
            prototype: SheetSteel10
            offset: -2, -1
          - key: load
            prototype: SheetSteel1
            offset: 2, 0
          - key: tool
            prototype: NCMetaCommissioningTool
            offset: 0, -1
          cables:
          - from: host
            to: conveyor
          - from: host
            to: sensor
          - from: host
            to: fabricator
        
        - type: ncMetaDemoScene
          id: NCMetaBedsideLabScene
          name: nc-meta-scene-clinical
          objects:
          - key: host
            prototype: NCMetaTerminal
            offset: 0, 0
          - key: bedside
            prototype: NCMetaClinicalController
            offset: -1, 0
          - key: scanner
            prototype: NCMetaBodyScanner
            offset: 0, 1
          - key: table
            prototype: OperatingTable
            offset: 1, 2
          - key: patient
            prototype: MobHuman
            offset: 0, 3
          - key: grinder
            prototype: NCMetaLaboratoryGrinder
            offset: 2, 0
          - key: chemMaster
            prototype: NCMetaLaboratoryChemMaster
            offset: -2, 0
          - key: beaker
            prototype: LargeBeaker
            offset: 2, -1
          - key: ingredient
            prototype: FoodCarrot
            offset: 3, 0
          - key: tool
            prototype: NCMetaCommissioningTool
            offset: 0, -1
          - key: configurator
            prototype: NetworkConfigurator
            offset: -1, -1
          cables:
          - from: host
            to: bedside
          - from: host
            to: scanner
          - from: host
            to: grinder
          - from: host
            to: chemMaster
        
        - type: entity
          parent: NCMetaDoor
          id: NCMetaDemoNetworkDoor
          suffix: local NET training
          components:
          - type: NCMetaDevice
            allowServiceControl: true
          - type: NCMetaPort
            networkId: meta-demo
        
        - type: entity
          parent: NCMetaWaypointA
          id: NCMetaLoadingWaypoint
          components:
          - type: NCMetaWaypoint
            name: loading
        
        
        - type: entity
          parent: NCMetaNetworkPort
          id: NCMetaDemoNetAccessPoint
          name: local NET access point
          suffix: training
          description: A local architecture entrance for a physical cable or a nearby unobstructed radio link.
          components:
          - type: NCMetaPort
            networkId: meta-demo
            wireless: true
            range: 8
          - type: NCMetaNetAccessPoint
            architectureId: meta-demo
            entryNodes: [entry]
          - type: NCMetaNetNode
            architectureId: meta-demo
            nodeId: entry
            label: nc-meta-net-entry-node
            kind: Defense
            defense: NCMetaNetUnprotected
            neighbors: [data, control, firewall, black-ice]
        
        
        - type: entity
          parent: NCMetaNetworkPort
          id: NCMetaDemoNetData
          name: local NET data node
          suffix: training
          description: A data service exposing one configured virtual file after an authorized NET action.
          components:
          - type: NCMetaNetNode
            architectureId: meta-demo
            nodeId: data
            label: nc-meta-net-data-node
            kind: Data
            dataPath: public/status.txt
            defense: NCMetaNetUnprotected
        
        
        - type: entity
          parent: NCMetaNetworkPort
          id: NCMetaDemoNetControl
          name: local NET control node
          suffix: training
          description: A scoped control service for one physically connected door that permits service control.
          components:
          - type: NCMetaNetNode
            architectureId: meta-demo
            nodeId: control
            label: nc-meta-net-control-node
            kind: Control
            defense: NCMetaNetUnprotected
        
        
        - type: entity
          parent: NCMetaNetworkPort
          id: NCMetaDemoNetFirewall
          name: local NET firewall
          suffix: training
          description: A protected node for native tool and defense resolution.
          components:
          - type: NCMetaNetNode
            architectureId: meta-demo
            nodeId: firewall
            label: nc-meta-net-firewall-node
            kind: Defense
            defense: NCMetaNetSecured
        
        
        - type: entity
          parent: NCMetaDemoNetFirewall
          id: NCMetaDemoNetBlackIce
          name: local NET Black ICE
          suffix: training
          description: A dangerous native defense. Its threat timer continues while META software is paused or stopped.
          components:
          - type: NCMetaNetNode
            architectureId: meta-demo
            nodeId: black-ice
            label: nc-meta-net-black-ice-node
            kind: Defense
            defense: NCMetaNetBlackIce
        """;
}
