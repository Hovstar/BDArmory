using UnityEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using BDArmory.Control;
using BDArmory.Extensions;
using BDArmory.Settings;
using BDArmory.Weapons;

namespace BDArmory.Competition.VesselSpawning
{
    /// <summary>
    /// A static class for doing the actual spawning of a vessel from a craft file into KSP.
    /// This is mostly taken from VesselSpawner.
    /// For proper vessel placement and orientation (including accounting for control point orientation), the vessel should be immediately set as not landed,
    /// then wait a couple of fixed updates for the root part's transform to be assigned and assign this as the vessel's reference transform, then the proper
    /// position and orientation of the vessel can be finally assigned.
    /// Note: KSP sometimes packs and unpacks vessels between frames (possibly due to external seats), which can reset positions and rotations and reset things!
    /// </summary>
    public static class VesselSpawner
    {
        public static string spawnProbeLocation
        {
            get
            {
                if (_spawnProbeLocation != null) return _spawnProbeLocation;
                _spawnProbeLocation = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "BDArmory", "craft", "SpawnProbe.craft"); // SpaceDock location
                if (!File.Exists(_spawnProbeLocation)) _spawnProbeLocation = Path.Combine(KSPUtil.ApplicationRootPath, "Ships", "SPH", "SpawnProbe.craft"); // CKAN location
                if (!File.Exists(_spawnProbeLocation))
                {
                    _spawnProbeLocation = null;
                    var message = "SpawnProbe.craft is missing. Your installation is likely corrupt.";
                    ScreenMessages.PostScreenMessage(message, 10);
                    Debug.LogError("[BDArmory.SpawnUtils]: " + message);
                }
                return _spawnProbeLocation;
            }
        }
        private static string _spawnProbeLocation = null;

        /// <summary>
        /// Spawn a spawn-probe at the camera's coordinates (plus offset).
        /// </summary>
        /// <returns>The spawn probe on success, else null.</returns>
        public static Vessel SpawnSpawnProbe(Vector3 offset = default)
        {
            // Spawn in the SpawnProbe at the camera position.
            var dummyVar = EditorFacility.None;
            Vector3d dummySpawnCoords;
            FlightGlobals.currentMainBody.GetLatLonAlt(FlightCamera.fetch.transform.position + offset, out dummySpawnCoords.x, out dummySpawnCoords.y, out dummySpawnCoords.z);
            if (spawnProbeLocation == null) return null;
            Vessel spawnProbe = VesselSpawner.SpawnVesselFromCraftFile(spawnProbeLocation, dummySpawnCoords, 0f, 0f, 0f, out dummyVar);
            return spawnProbe;
        }

        /// <summary>
        /// Spawn a craft at the given coordinates with the given orientation.
        /// Note: This does not take into account control point orientation, which only exists once the reference transform for the vessel is loaded (not the protovessel here). See the class description for details.
        /// </summary>
        /// <param name="craftURL"></param>
        /// <param name="gpsCoords"></param>
        /// <param name="heading"></param>
        /// <param name="pitch"></param>
        /// <param name="roll"></param>
        /// <param name="shipFacility">out parameter containing the vessel's EditorFacility (VAB/SPH)</param>
        /// <param name="crewData"></param>
        /// <returns></returns>
        public static Vessel SpawnVesselFromCraftFile(string craftURL, Vector3d gpsCoords, float heading, float pitch, float roll, out EditorFacility shipFacility, List<ProtoCrewMember> crewData = null)
        {
            VesselData newData = new VesselData();

            newData.craftURL = craftURL;
            newData.latitude = gpsCoords.x;
            newData.longitude = gpsCoords.y;
            newData.altitude = gpsCoords.z;

            newData.body = FlightGlobals.currentMainBody;
            newData.heading = heading;
            newData.pitch = pitch;
            newData.roll = roll;
            newData.orbiting = false;
            newData.flagURL = HighLogic.CurrentGame.flagURL;
            newData.owned = true;
            newData.vesselType = VesselType.Ship;

            newData.crew = new List<CrewData>();

            return SpawnVessel(newData, out shipFacility, crewData);
        }

        static Vessel SpawnVessel(VesselData vesselData, out EditorFacility shipFacility, List<ProtoCrewMember> crewData = null)
        {
            shipFacility = EditorFacility.None;
            //Set additional info for landed vessels
            bool landed = false;
            if (!vesselData.orbiting)
            {
                landed = true;
                if (vesselData.altitude == null || vesselData.altitude < 0)
                {
                    vesselData.altitude = 35;
                }

                Vector3d pos = vesselData.body.GetRelSurfacePosition(vesselData.latitude, vesselData.longitude, vesselData.altitude.Value);

                vesselData.orbit = new Orbit(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, vesselData.body);
                vesselData.orbit.UpdateFromStateVectors(pos, vesselData.body.getRFrmVel(pos), vesselData.body, Planetarium.GetUniversalTime());
            }
            else
            {
                vesselData.orbit.referenceBody = vesselData.body;
            }

            ConfigNode[] partNodes;
            ShipConstruct shipConstruct = null;
            if (!string.IsNullOrEmpty(vesselData.craftURL))
            {
                var craftNode = ConfigNode.Load(vesselData.craftURL);
                shipConstruct = new ShipConstruct();
                if (!shipConstruct.LoadShip(craftNode))
                {
                    Debug.LogError("[BDArmory.VesselSpawner]: Ship file error!");
                    return null;
                }

                // Set the name
                if (string.IsNullOrEmpty(vesselData.name))
                {
                    vesselData.name = shipConstruct.shipName;
                }

                // Sort the parts into top-down tree order.
                shipConstruct.parts = SortPartTree(shipConstruct.parts);

                // Set some parameters that need to be at the part level
                uint missionID = (uint)Guid.NewGuid().GetHashCode();
                uint launchID = HighLogic.CurrentGame.launchID++;
                foreach (Part p in shipConstruct.parts)
                {
                    p.flightID = ShipConstruction.GetUniqueFlightID(HighLogic.CurrentGame.flightState);
                    p.missionID = missionID;
                    p.launchID = launchID;
                    p.flagURL = vesselData.flagURL ?? HighLogic.CurrentGame.flagURL;

                    // Had some issues with this being set to -1 for some ships - can't figure out
                    // why.  End result is the vessel exploding, so let's just set it to a positive
                    // value.
                    p.temperature = 1.0;
                }

                // Add crew
                List<Part> crewParts;
                ModuleWeapon crewedWeapon;
                switch (BDArmorySettings.VESSEL_SPAWN_FILL_SEATS)
                {
                    case 0: // Minimal plus crewable weapons.
                        {
                            crewParts = shipConstruct.parts.FindAll(p => p.protoModuleCrew.Count < p.CrewCapacity && (crewedWeapon = p.FindModuleImplementing<ModuleWeapon>()) && crewedWeapon.crewserved).ToList(); // Crewed weapons.
                            var part = shipConstruct.parts.Find(p => p.protoModuleCrew.Count < p.CrewCapacity && !p.FindModuleImplementing<ModuleWeapon>() && (p.FindModuleImplementing<ModuleCommand>() || p.FindModuleImplementing<KerbalSeat>())); // A non-weapon crewed command part.
                            if (part) crewParts.Add(part);
                            break;
                        }
                    case 1: // All cockpits or the first combat seat if no cockpits are found, plus crewable weapons.
                        {
                            crewParts = shipConstruct.parts.FindAll(p => p.protoModuleCrew.Count < p.CrewCapacity && p.FindModuleImplementing<ModuleCommand>()).ToList(); // Crewable cockpits.
                            if (crewParts.Count() == 0)
                            {
                                var part = shipConstruct.parts.Find(p => p.protoModuleCrew.Count < p.CrewCapacity && p.FindModuleImplementing<KerbalSeat>() && p.FindModuleImplementing<MissileFire>()); // The first combat seat if no cockpits were found.
                                if (part) crewParts.Add(part);
                            }
                            crewParts.AddRange(shipConstruct.parts.FindAll(p => p.protoModuleCrew.Count < p.CrewCapacity && ((crewedWeapon = p.FindModuleImplementing<ModuleWeapon>()) && crewedWeapon.crewserved))); // Crewable weapons.
                            break;
                        }
                    case 2: // All crewable control points plus crewable weapons.
                        {
                            crewParts = shipConstruct.parts.FindAll(p => p.protoModuleCrew.Count < p.CrewCapacity && (p.FindModuleImplementing<ModuleCommand>() || p.FindModuleImplementing<KerbalSeat>() || ((crewedWeapon = p.FindModuleImplementing<ModuleWeapon>()) && crewedWeapon.crewserved))).ToList();
                            break;
                        }
                    case 3: // All crewable parts.
                        {
                            crewParts = shipConstruct.parts.FindAll(p => p.protoModuleCrew.Count < p.CrewCapacity).ToList();
                            break;
                        }
                    default:
                        throw new IndexOutOfRangeException("Invalid Fill Seats value");
                }
                if (BDArmorySettings.RUNWAY_PROJECT && BDArmorySettings.RUNWAY_PROJECT_ROUND == 42) // Fly the Unfriendly Skies
                { crewParts = shipConstruct.parts.FindAll(p => p.protoModuleCrew.Count < p.CrewCapacity).ToList(); }
                foreach (var part in crewParts)
                {
                    int crewToAdd = (BDArmorySettings.VESSEL_SPAWN_FILL_SEATS > 0 || (BDArmorySettings.RUNWAY_PROJECT && BDArmorySettings.RUNWAY_PROJECT_ROUND == 42)) ? part.CrewCapacity - part.protoModuleCrew.Count : 1;
                    for (int crewCount = 0; crewCount < crewToAdd; ++crewCount)
                    {
                        // Create the ProtoCrewMember
                        ProtoCrewMember crewMember = HighLogic.CurrentGame.CrewRoster.GetNextOrNewKerbal(ProtoCrewMember.KerbalType.Crew);
                        KerbalRoster.SetExperienceTrait(crewMember, KerbalRoster.pilotTrait); // Make the kerbal a pilot (so they can use SAS properly).
                        KerbalRoster.SetExperienceLevel(crewMember, KerbalRoster.GetExperienceMaxLevel()); // Make them experienced.
                        crewMember.isBadass = true; // Make them bad-ass (likes nearby explosions).

                        // Add them to the part
                        part.AddCrewmemberAt(crewMember, part.protoModuleCrew.Count);
                    }
                }

                // Create a dummy ProtoVessel, we will use this to dump the parts to a config node.
                // We can't use the config nodes from the .craft file, because they are in a
                // slightly different format than those required for a ProtoVessel (seriously
                // Squad?!?).
                ConfigNode empty = new ConfigNode();
                ProtoVessel dummyProto = new ProtoVessel(empty, null);
                Vessel dummyVessel = new Vessel();
                dummyVessel.parts = shipConstruct.Parts;
                dummyProto.vesselRef = dummyVessel;

                // Create the ProtoPartSnapshot objects and then initialize them
                foreach (Part p in shipConstruct.parts)
                {
                    dummyVessel.loaded = false;
                    p.vessel = dummyVessel;

                    dummyProto.protoPartSnapshots.Add(new ProtoPartSnapshot(p, dummyProto, true));
                    UnityEngine.Object.Destroy(p.gameObject); // Destroy the prefab.
                }
                foreach (ProtoPartSnapshot p in dummyProto.protoPartSnapshots)
                {
                    p.storePartRefs();
                }

                // Create the ship's parts
                List<ConfigNode> partNodesL = new List<ConfigNode>();
                foreach (ProtoPartSnapshot snapShot in dummyProto.protoPartSnapshots)
                {
                    ConfigNode node = new ConfigNode("PART");
                    snapShot.Save(node);
                    partNodesL.Add(node);
                }
                partNodes = partNodesL.ToArray();
            }
            else
            {
                // Create crew member array
                ProtoCrewMember[] crewArray = new ProtoCrewMember[vesselData.crew.Count];
                int i = 0;
                foreach (CrewData cd in vesselData.crew)
                {
                    // Create the ProtoCrewMember
                    ProtoCrewMember crewMember = HighLogic.CurrentGame.CrewRoster.GetNextOrNewKerbal(ProtoCrewMember.KerbalType.Crew);
                    if (cd.name != null)
                    {
                        crewMember.KerbalRef.name = cd.name;
                    }
                    KerbalRoster.SetExperienceTrait(crewMember, KerbalRoster.pilotTrait); // Make the kerbal a pilot (so they can use SAS properly).
                    KerbalRoster.SetExperienceLevel(crewMember, KerbalRoster.GetExperienceMaxLevel()); // Make them experienced.
                    crewMember.isBadass = true; // Make them bad-ass (likes nearby explosions).

                    crewArray[i++] = crewMember;
                }

                // Create part nodes
                uint flightId = ShipConstruction.GetUniqueFlightID(HighLogic.CurrentGame.flightState);
                partNodes = new ConfigNode[1];
                partNodes[0] = ProtoVessel.CreatePartNode(vesselData.craftPart.name, flightId, crewArray);

                // Default the size class
                //sizeClass = UntrackedObjectClass.A;

                // Set the name
                if (string.IsNullOrEmpty(vesselData.name))
                {
                    vesselData.name = vesselData.craftPart.name;
                }
            }

            // Create additional nodes
            ConfigNode[] additionalNodes = new ConfigNode[0];

            // Create the config node representation of the ProtoVessel
            int rootPartIndex = 0;
            ConfigNode protoVesselNode = ProtoVessel.CreateVesselNode(vesselData.name, vesselData.vesselType, vesselData.orbit, rootPartIndex, partNodes, additionalNodes);

            // Additional settings for a landed vessel
            if (!vesselData.orbiting)
            {
                bool splashed = false;// = landed && terrainHeight < 0.001;

                // Create the config node representation of the ProtoVessel
                // Note - flying is experimental, and so far doesn't work
                protoVesselNode.SetValue("sit", (splashed ? Vessel.Situations.SPLASHED : landed ?
                    Vessel.Situations.LANDED : Vessel.Situations.FLYING).ToString());
                protoVesselNode.SetValue("landed", (landed && !splashed).ToString());
                protoVesselNode.SetValue("splashed", splashed.ToString());
                protoVesselNode.SetValue("lat", vesselData.latitude.ToString());
                protoVesselNode.SetValue("lon", vesselData.longitude.ToString());
                protoVesselNode.SetValue("alt", vesselData.altitude.ToString());
                protoVesselNode.SetValue("landedAt", vesselData.body.name);

                // Figure out the additional height to subtract
                float lowest = float.MaxValue;
                if (shipConstruct != null)
                {
                    foreach (Part p in shipConstruct.parts)
                    {
                        foreach (Collider collider in p.GetComponentsInChildren<Collider>())
                        {
                            if (collider.gameObject.layer != 21 && collider.enabled)
                            {
                                lowest = Mathf.Min(lowest, collider.bounds.min.y);
                            }
                        }
                    }
                }
                else
                {
                    foreach (Collider collider in vesselData.craftPart.partPrefab.GetComponentsInChildren<Collider>())
                    {
                        if (collider.gameObject.layer != 21 && collider.enabled)
                        {
                            lowest = Mathf.Min(lowest, collider.bounds.min.y);
                        }
                    }
                }

                if (lowest == float.MaxValue)
                {
                    lowest = 0;
                }

                // Figure out the surface height and rotation
                Quaternion normal = Quaternion.LookRotation((Vector3)vesselData.body.GetRelSurfaceNVector(vesselData.latitude, vesselData.longitude));
                Quaternion rotation = Quaternion.identity;
                float heading = vesselData.heading;
                if (shipConstruct == null)
                {
                    // Debug.Log("[BDArmory.VesselSpawner]: initial rotation override: null");
                    rotation = Quaternion.FromToRotation(Vector3.up, Vector3.back); //FIXME add a check if spawning in null-atmo to have craft spawn horizontal, not nose-down
                }
                else if (shipConstruct.shipFacility == EditorFacility.SPH)
                {
                    //Debug.Log("[BDArmory.VesselSpawner]: initial rotation override: SPH");
                    rotation = Quaternion.FromToRotation(Vector3.forward, Vector3.back); // Orient the SPH vessel upright, facing (vessel.up) south.
                    rotation = Quaternion.AngleAxis(180f, Vector3.back) * rotation; // Face north (heading 0°).
                }
                else
                {
                    // Debug.Log("[BDArmory.VesselSpawner]: initial rotation override: VAB");
                    rotation = Quaternion.FromToRotation(Vector3.up, Vector3.forward); // Orient the SPH vessel upright, facing (-vessel.forward) south.
                    rotation = Quaternion.AngleAxis(180f, Vector3.back) * rotation; // Face north (heading 0°).
                }

                rotation = Quaternion.AngleAxis(vesselData.roll, Vector3.down) * rotation; // Note: roll direction is inverted.
                rotation = Quaternion.AngleAxis(vesselData.pitch, Vector3.right) * rotation;
                rotation = Quaternion.AngleAxis(heading, Vector3.forward) * rotation;

                // Set the height and rotation
                if (landed || splashed)
                {
                    float hgt = (shipConstruct != null ? shipConstruct.parts[0] : vesselData.craftPart.partPrefab).localRoot.attPos0.y - lowest;
                    hgt += vesselData.height + 35;
                    protoVesselNode.SetValue("hgt", hgt.ToString(), true);
                }
                protoVesselNode.SetValue("rot", KSPUtil.WriteQuaternion(normal * rotation), true);

                // Set the normal vector relative to the surface
                Vector3 nrm = (rotation * Vector3.forward);
                protoVesselNode.SetValue("nrm", nrm.x + "," + nrm.y + "," + nrm.z, true);
                protoVesselNode.SetValue("prst", false.ToString(), true);
            }

            // Add vessel to the game
            ProtoVessel protoVessel = HighLogic.CurrentGame.AddVessel(protoVesselNode);

            // Set the vessel size (FIXME various other vessel fields appear to not be set, e.g. CoM)
            protoVessel.vesselRef.vesselSize = shipConstruct.shipSize;
            shipFacility = shipConstruct.shipFacility;
            switch (shipFacility)
            {
                case EditorFacility.SPH:
                    protoVessel.vesselRef.vesselType = VesselType.Plane;
                    break;
                case EditorFacility.VAB:
                    protoVessel.vesselRef.vesselType = VesselType.Ship;
                    break;
                default:
                    break;
            }

            // Store the id for later use
            vesselData.id = protoVessel.vesselRef.id;
            // StartCoroutine(PlaceSpawnedVessel(protoVessel.vesselRef));

            return protoVessel.vesselRef;
        }

        static List<Part> SortPartTree(List<Part> parts)
        {
            List<Part> Parts = parts.Where(p => p.parent == null).ToList(); // There can be only one.
            while (Parts.Count() < parts.Count())
            {
                var partsToAdd = parts.Where(p => !Parts.Contains(p) && Parts.Contains(p.parent));
                if (partsToAdd.Count() == 0)
                {
                    Debug.Log($"[BDArmory.VesselSpawner]: Part count mismatch when sorting the part-tree: {Parts.Count()} vs {parts.Count()}");
                    break;
                }
                Parts.AddRange(partsToAdd);
            }
            return Parts;
        }

        internal class CrewData
        {
            public string name = null;
            public ProtoCrewMember.Gender? gender = null;
            public bool addToRoster = true;

            public CrewData() { }
            public CrewData(CrewData cd)
            {
                name = cd.name;
                gender = cd.gender;
                addToRoster = cd.addToRoster;
            }
        }

        internal class VesselData
        {
            public string name = null;
            public Guid? id = null;
            public string craftURL = null;
            public AvailablePart craftPart = null;
            public string flagURL = null;
            public VesselType vesselType = VesselType.Ship;
            public CelestialBody body = null;
            public Orbit orbit = null;
            public double latitude = 0.0;
            public double longitude = 0.0;
            public double? altitude = null;
            public float height = 0.0f;
            public bool orbiting = false;
            public bool owned = false;
            public List<CrewData> crew = new List<CrewData>();
            public PQSCity pqsCity = null;
            public Vector3d pqsOffset = Vector3d.zero;
            public float heading = 0f;
            public float pitch = 0f;
            public float roll = 0f;
        }
    }
}