using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

using BDArmory.Armor;
using BDArmory.Competition;
using BDArmory.Damage;
using BDArmory.Extensions;
using BDArmory.FX;
using BDArmory.Modules;
using BDArmory.Settings;
using BDArmory.Shaders;
using BDArmory.Targeting;
using BDArmory.UI;
using BDArmory.Utils;
using BDArmory.Weapons;

namespace BDArmory.Bullets
{
    public class PooledBullet : MonoBehaviour
    {
        #region Declarations

        public BulletInfo bullet;
        //public float leftPenetration; //Not used by anything? Was this to provide a upper cap to how far a bullet could pen?

        public enum PooledBulletTypes
        {
            Slug,
            Explosive,
            Shaped
        }
        public enum BulletFuzeTypes
        {
            None,
            Impact,
            Timed,
            Proximity,
            Flak,
            Delay,
            Penetrating
        }
        public enum BulletDragTypes
        {
            None,
            AnalyticEstimate,
            NumericalIntegration
        }

        //public PooledBulletTypes bulletType;
        public BulletFuzeTypes fuzeType;
        public PooledBulletTypes HEType;
        public BulletDragTypes dragType;

        public Vessel sourceVessel;
        public string sourceVesselName;
        public Part sourceWeapon;
        public string team;
        public Color lightColor = GUIUtils.ParseColor255("255, 235, 145, 255");
        public Color projectileColor;
        public string bulletTexturePath;
        public bool fadeColor;
        public Color startColor;
        Color currentColor;
        public bool bulletDrop = true;
        public float tracerStartWidth = 1;
        public float tracerEndWidth = 1;
        public float tracerLength = 0;
        public float tracerDeltaFactor = 1.35f;
        public float tracerLuminance = 1;
        public Vector3 currPosition;

        //explosive parameters
        public float radius = 30;
        public float tntMass = 0;
        public float blastPower = 8;
        public float blastHeat = -1;
        public float bulletDmgMult = 1;
        public string explModelPath;
        public string explSoundPath;

        //general params
        public bool incendiary;
        public float apBulletMod = 0;
        public bool nuclear = false;
        public string flashModelPath;
        public string shockModelPath;
        public string blastModelPath;
        public string plumeModelPath;
        public string debrisModelPath;
        public string blastSoundPath;
        //public bool homing = false;
        public bool beehive = false;
        public BulletInfo subMunitionType;
        public bool EMP = false;

        //gravitic parameters
        public float impulse = 0;
        public float massMod = 0;

        //mutator Param
        public bool stealResources;
        public float dmgMult = 1;

        Vector3 startPosition;
        public float detonationRange = 5f;
        public float defaultDetonationRange = 3500f;
        public float maxAirDetonationRange = 3500f;
        float randomWidthScale = 1;
        LineRenderer bulletTrail;
        public float timeToLiveUntil;
        Light lightFlash;
        bool wasInitiated;
        public Vector3 currentVelocity; // Current real velocity w/o offloading
        public float bulletMass;
        public float caliber = 1;
        public float bulletVelocity; //muzzle velocity
        public bool sabot = false;
        private float HERatio = 0.06f;
        public float ballisticCoefficient;
        float currentSpeed; // Current speed of the bullet, for drag purposes.
        public float timeElapsedSinceCurrentSpeedWasAdjusted; // Time since the current speed was adjusted, to allow tracking speed changes of the bullet in air and water.
        bool underwater = false;
        bool startsUnderwater = false;
        public static Shader bulletShader;
        public static bool shaderInitialized;
        private float impactSpeed;
        private float dragVelocityFactor;

        public bool hasPenetrated = false;
        public bool hasDetonated = false;
        public bool hasRicocheted = false;
        public bool fuzeTriggered = false;
        private Part CurrentPart = null;

        public bool isAPSprojectile = false;
        public PooledRocket tgtRocket = null;
        public PooledBullet tgtShell = null;

        public int penTicker = 0;

        public Rigidbody rb;

        Ray bulletRay;

        #endregion Declarations

        static RaycastHit[] hits;
        static RaycastHit[] reverseHits;
        static Collider[] overlapSphereColliders;
        static Collider[] proximityOverlapSphereColliders;
        static List<RaycastHit> allHits;
        static Dictionary<Vessel, float> rayLength;
        private Vector3[] linePositions = new Vector3[2];

        private List<Part> partsHit = new List<Part>();

        private double distanceTraveled = 0;
        private double distanceLastHit = double.PositiveInfinity;

        private float kDist = 1;

        public double DistanceTraveled { get { return distanceTraveled; } }

        void Awake()
        {
            if (hits == null) { hits = new RaycastHit[100]; }
            if (reverseHits == null) { reverseHits = new RaycastHit[100]; }
            if (overlapSphereColliders == null) { overlapSphereColliders = new Collider[1000]; }
            if (proximityOverlapSphereColliders == null) { proximityOverlapSphereColliders = new Collider[100]; }
            if (allHits == null) { allHits = new List<RaycastHit>(); }
            if (rayLength == null) { rayLength = new Dictionary<Vessel, float>(); }
        }

        void OnEnable()
        {
            startPosition = transform.position;
            currentSpeed = currentVelocity.magnitude; // this is the velocity used for drag estimations (only), use total velocity, not muzzle velocity

            if (HEType != PooledBulletTypes.Slug)
            {
                HERatio = Mathf.Clamp(tntMass / (bulletMass < tntMass ? tntMass * 1.25f : bulletMass), 0.01f, 0.95f);
            }
            else
            {
                HERatio = 0;
            }
            if (nuclear)
            {
                var nuke = sourceWeapon.FindModuleImplementing<BDModuleNuke>();
                if (nuke == null)
                {
                    flashModelPath = BDModuleNuke.defaultflashModelPath;
                    shockModelPath = BDModuleNuke.defaultShockModelPath;
                    blastModelPath = BDModuleNuke.defaultBlastModelPath;
                    plumeModelPath = BDModuleNuke.defaultPlumeModelPath;
                    debrisModelPath = BDModuleNuke.defaultDebrisModelPath;
                    blastSoundPath = BDModuleNuke.defaultBlastSoundPath;
                }
                else
                {
                    flashModelPath = nuke.flashModelPath;
                    shockModelPath = nuke.shockModelPath;
                    blastModelPath = nuke.blastModelPath;
                    plumeModelPath = nuke.plumeModelPath;
                    debrisModelPath = nuke.debrisModelPath;
                    blastSoundPath = nuke.blastSoundPath;
                }
            }
            distanceTraveled = 0; // Reset the distance travelled for the bullet (since it comes from a pool).

            if (!wasInitiated)
            {
                //projectileColor.a = projectileColor.a/2;
                //startColor.a = startColor.a/2;
            }
            startsUnderwater = FlightGlobals.getAltitudeAtPos(transform.position) < 0;
            underwater = startsUnderwater;

            projectileColor.a = Mathf.Clamp(projectileColor.a, 0.25f, 1f);
            startColor.a = Mathf.Clamp(startColor.a, 0.25f, 1f);
            currentColor = projectileColor;
            if (fadeColor)
            {
                currentColor = startColor;
            }

            if (lightFlash == null || !gameObject.GetComponent<Light>())
            {
                lightFlash = gameObject.AddOrGetComponent<Light>();
                lightFlash.type = LightType.Point;
                lightFlash.range = 8;
                lightFlash.intensity = 1;
                lightFlash.color = lightColor;
                lightFlash.enabled = true;
            }

            //tracer setup
            if (bulletTrail == null || !gameObject.GetComponent<LineRenderer>())
            {
                bulletTrail = gameObject.AddOrGetComponent<LineRenderer>();
            }

            if (!wasInitiated)
            {
                bulletTrail.positionCount = linePositions.Length;
            }
            // Note: call SetTracerPosition() after enabling the bullet and making adjustments to it's position.

            if (!shaderInitialized)
            {
                shaderInitialized = true;
                bulletShader = BDAShaderLoader.BulletShader;
            }

            if (!wasInitiated)
            {
                bulletTrail.material = new Material(bulletShader);
                randomWidthScale = UnityEngine.Random.Range(0.5f, 1f);
                gameObject.layer = 15;
            }

            bulletTrail.material.mainTexture = GameDatabase.Instance.GetTexture(bulletTexturePath, false);
            bulletTrail.material.SetColor("_TintColor", currentColor);
            bulletTrail.material.SetFloat("_Lum", tracerLuminance);

            tracerStartWidth *= 2f;
            tracerEndWidth *= 2f;

            //leftPenetration = 1;
            penTicker = 0;
            wasInitiated = true;
            StartCoroutine(FrameDelayedRoutine());

            // Log shots fired.
            if (this.sourceVessel)
            {
                sourceVesselName = sourceVessel.GetName(); // Set the source vessel name as the vessel might have changed its name or died by the time the bullet hits.
                BDACompetitionMode.Instance.Scores.RegisterShot(sourceVesselName);
            }
            else
            {
                sourceVesselName = null;
            }
            if (caliber >= BDArmorySettings.APS_THRESHOLD) //if (caliber > 60)
            {
                BDATargetManager.FiredBullets.Add(this);
            }
        }

        void OnDisable()
        {
            sourceVessel = null;
            sourceWeapon = null;
            CurrentPart = null;
            sabot = false;
            partsHit.Clear();
            if (caliber >= BDArmorySettings.APS_THRESHOLD)  //if (caliber > 60)
            {
                BDATargetManager.FiredBullets.Remove(this);
            }
            isAPSprojectile = false;
            tgtRocket = null;
            tgtShell = null;
        }

        void OnDestroy()
        {
            StopCoroutine(FrameDelayedRoutine());
        }

        IEnumerator FrameDelayedRoutine()
        {
            yield return new WaitForFixedUpdate();
            lightFlash.enabled = false;
        }

        void OnWillRenderObject()
        {
            if (!gameObject.activeInHierarchy)
            {
                return;
            }
            Camera currentCam = Camera.current;
            if (TargetingCamera.IsTGPCamera(currentCam))
            {
                UpdateWidth(currentCam, 4);
            }
            else
            {
                UpdateWidth(currentCam, 1);
            }
        }

        void FixedUpdate()
        {
            if (!gameObject.activeInHierarchy)
            {
                return;
            }

            //floating origin and velocity offloading corrections
            if (BDKrakensbane.IsActive)
            {
                transform.position -= BDKrakensbane.FloatingOriginOffsetNonKrakensbane;
                startPosition -= BDKrakensbane.FloatingOriginOffsetNonKrakensbane;
            }

            if (fadeColor)
            {
                FadeColor();
                bulletTrail.material.SetColor("_TintColor", currentColor * tracerLuminance);
            }
            SetTracerPosition();

            if (Time.time > timeToLiveUntil) //kill bullet when TTL ends
            {
                KillBullet();
                if (isAPSprojectile)
                {
                    if (HEType != PooledBulletTypes.Explosive && tntMass > 0)
                        ExplosionFx.CreateExplosion(currPosition, tntMass, explModelPath, explSoundPath, ExplosionSourceType.Bullet, caliber, null, sourceVesselName, null, default, -1, true);
                }
                return;
            }
            /*
            if (fuzeTriggered)
            {
                if (!hasDetonated)
                {
                    ExplosionFx.CreateExplosion(currPosition, tntMass, explModelPath, explSoundPath, ExplosionSourceType.Bullet, caliber, null, sourceVesselName, null, default, -1, false, bulletMass, -1, dmgMult);
                    hasDetonated = true;
                    KillBullet();
                    return;
                }
            }
            */
            if (CheckBulletCollisions(TimeWarp.fixedDeltaTime)) return;

            MoveBullet(Time.fixedDeltaTime);

            if (BDArmorySettings.BULLET_WATER_DRAG)
            {
                if (startsUnderwater && !underwater) // Bullets that start underwater can exit the water if fired close enough to the surface.
                {
                    startsUnderwater = false;
                }
                if (!startsUnderwater && underwater) // Bullets entering water from air either disintegrate or don't penetrate far enough to bother about. Except large caliber naval shells.
                {
                    if (caliber < 75f)
                    {
                        if (HEType != PooledBulletTypes.Slug)
                            ExplosionFx.CreateExplosion(currPosition, tntMass, explModelPath, explSoundPath, ExplosionSourceType.Bullet, caliber, null, sourceVesselName, null, default, -1, false, bulletMass, -1, dmgMult);
                        if (nuclear)
                            NukeFX.CreateExplosion(currPosition, ExplosionSourceType.Bullet, sourceVesselName, bullet.DisplayName, 0, tntMass * 200, tntMass, tntMass, EMP, blastSoundPath, flashModelPath, shockModelPath, blastModelPath, plumeModelPath, debrisModelPath, "", "");
                        hasDetonated = true;

                        KillBullet();
                        return;
                    }
                    else
                    {
                        if (HEType != PooledBulletTypes.Slug)
                        {
                            if (fuzeType == BulletFuzeTypes.Delay || fuzeType == BulletFuzeTypes.Penetrating)
                            {
                                fuzeTriggered = true;
                                StartCoroutine(DelayedDetonationRoutine());
                            }
                            else //if (fuzeType != BulletFuzeTypes.None)
                            {
                                if (HEType != PooledBulletTypes.Slug)
                                    ExplosionFx.CreateExplosion(currPosition, tntMass, explModelPath, explSoundPath, ExplosionSourceType.Bullet, caliber, null, sourceVesselName, null, default, -1, false, bulletMass, -1, dmgMult);
                                if (nuclear)
                                    NukeFX.CreateExplosion(currPosition, ExplosionSourceType.Bullet, sourceVesselName, bullet.DisplayName, 0, tntMass * 200, tntMass, tntMass, EMP, blastSoundPath, flashModelPath, shockModelPath, blastModelPath, plumeModelPath, debrisModelPath, "", "");
                                hasDetonated = true;
                                FXMonger.Splash(transform.position, caliber / 2);
                                KillBullet();
                                return;
                            }
                        }
                    }
                }
            }
            //////////////////////////////////////////////////
            //Flak Explosion (air detonation/proximity fuse)
            //////////////////////////////////////////////////

            if (ProximityAirDetonation((float)distanceTraveled))
            {
                //detonate
                if (HEType != PooledBulletTypes.Slug)
                    ExplosionFx.CreateExplosion(currPosition, tntMass, explModelPath, explSoundPath, ExplosionSourceType.Bullet, caliber, null, sourceVesselName, null, HEType == PooledBulletTypes.Explosive ? default : currentVelocity, -1, false, bulletMass, -1, dmgMult, HEType == PooledBulletTypes.Shaped ? "shapedcharge" : "standard", null, HEType == PooledBulletTypes.Shaped ? apBulletMod : 1f);
                if (nuclear)
                    NukeFX.CreateExplosion(currPosition, ExplosionSourceType.Bullet, sourceVesselName, bullet.DisplayName, 0, tntMass * 200, tntMass, tntMass, EMP, blastSoundPath, flashModelPath, shockModelPath, blastModelPath, plumeModelPath, debrisModelPath, "", "");
                if (beehive)
                    BeehiveDetonation();
                hasDetonated = true;

                KillBullet();

                return;
            }
        }

        /// <summary>
        /// Move the bullet for the period of time, tracking distance traveled and accounting for drag and gravity.
        /// This is now done using the second order symplectic leapfrog method.
        /// Note: water drag on bullets breaks the symplectic nature of the integrator (since it's modifying the Hamiltonian), which isn't accounted for during aiming.
        /// </summary>
        /// <param name="period">Period to consider, typically Time.fixedDeltaTime</param>
        public void MoveBullet(float period)
        {
            // Initial half-timestep velocity change (leapfrog integrator)
            LeapfrogVelocityHalfStep(0.5f * period);

            // Full-timestep position change (leapfrog integrator)
            transform.position += currentVelocity * period; //move bullet
            distanceTraveled += currentVelocity.magnitude * period; // calculate flight distance for achievement purposes

            if (!underwater && FlightGlobals.getAltitudeAtPos(transform.position) <= 0) // Check if the bullet is now underwater.
            {
                float hitAngle = Vector3.Angle(GetDragAdjustedVelocity(), -VectorUtils.GetUpDirection(transform.position));
                if (RicochetScenery(hitAngle))
                {
                    tracerStartWidth /= 2;
                    tracerEndWidth /= 2;

                    currentVelocity = Vector3.Reflect(currentVelocity, VectorUtils.GetUpDirection(transform.position));
                    currentVelocity = (hitAngle / 150) * currentVelocity * 0.65f;

                    Vector3 randomDirection = UnityEngine.Random.rotation * Vector3.one;

                    currentVelocity = Vector3.RotateTowards(currentVelocity, randomDirection,
                        UnityEngine.Random.Range(0f, 5f) * Mathf.Deg2Rad, 0);
                }
                else
                {
                    underwater = true;
                }
                FXMonger.Splash(transform.position, caliber / 2);
            }
            // Second half-timestep velocity change (leapfrog integrator) (should be identical code-wise to the initial half-step)
            LeapfrogVelocityHalfStep(0.5f * period);
        }

        private void LeapfrogVelocityHalfStep(float period)
        {
            timeElapsedSinceCurrentSpeedWasAdjusted += period; // Track flight time for drag purposes
            UpdateDragEstimate(); // Update the drag estimate, accounting for water/air environment changes. Note: changes due to bulletDrop aren't being applied to the drag.
            if (underwater)
            {
                currentVelocity *= dragVelocityFactor; // Note: If applied to aerial flight, this screws up targeting, because the weapon's aim code doesn't know how to account for drag. Only have it apply when underwater for now. Review later?
                currentSpeed = currentVelocity.magnitude;
                timeElapsedSinceCurrentSpeedWasAdjusted = 0;
            }
            if (bulletDrop)
                currentVelocity += period * FlightGlobals.getGeeForceAtPosition(transform.position); // FIXME Should this be adjusted for being underwater?
        }

        /// <summary>
        /// Get the current velocity, adjusted for drag if necessary.
        /// </summary>
        /// <returns></returns>
        Vector3 GetDragAdjustedVelocity()
        {
            if (timeElapsedSinceCurrentSpeedWasAdjusted > 0)
            {
                return currentVelocity * dragVelocityFactor;
            }
            return currentVelocity;
        }

        public bool CheckBulletCollisions(float period)
        {
            //reset our hit variables to default state
            hasPenetrated = true;
            hasDetonated = false;
            hasRicocheted = false;
            //penTicker = 0;
            currPosition = transform.position;
            allHits.Clear();
            rayLength.Clear();

            if (BDArmorySettings.VESSEL_RELATIVE_BULLET_CHECKS)
            {
                CheckBulletCollisionWithVessels(period);
                CheckBulletCollisionWithScenery(period);
                using (var hitsEnu = allHits.OrderBy(x => x.distance).GetEnumerator()) // Check all hits in order of distance.
                    while (hitsEnu.MoveNext()) if (BulletHitAnalysis(hitsEnu.Current, period)) return true;
                return false;
            }
            else
                return CheckBulletCollision(period);
        }

        /// <summary>
        /// This performs checks using the relative velocity to each vessel within the range of the movement of the bullet.
        /// This is particularly relevant at high velocities (e.g., in orbit) where the sideways velocity of co-moving objects causes a complete miss.
        /// </summary>
        /// <param name="period"></param>
        /// <returns></returns>
        public void CheckBulletCollisionWithVessels(float period)
        {
            if (!BDArmorySettings.VESSEL_RELATIVE_BULLET_CHECKS) return;
            List<Vessel> nearbyVessels = new List<Vessel>();

            const int layerMask = (int)(LayerMasks.Parts | LayerMasks.EVA | LayerMasks.Wheels);
            var overlapSphereColliderCount = Physics.OverlapSphereNonAlloc(transform.position, currentVelocity.magnitude * period * 2f, overlapSphereColliders, layerMask); // Overlapsphere of 2*period assuming that vessels are moving at similar speeds to the bullet.
            if (overlapSphereColliderCount == overlapSphereColliders.Length)
            {
                overlapSphereColliders = Physics.OverlapSphere(transform.position, currentVelocity.magnitude * period * 2f, layerMask);
                overlapSphereColliderCount = overlapSphereColliders.Length;
            }

            using (var hitsEnu = overlapSphereColliders.Take(overlapSphereColliderCount).GetEnumerator())
            {
                while (hitsEnu.MoveNext())
                {
                    if (hitsEnu.Current == null) continue;
                    try
                    {
                        Part partHit = hitsEnu.Current.GetComponentInParent<Part>();
                        if (partHit == null) continue;
                        if (partHit.vessel == sourceVessel) continue;
                        if (ProjectileUtils.IsIgnoredPart(partHit)) continue; // Ignore ignored parts.
                        if (partHit.vessel != null && !nearbyVessels.Contains(partHit.vessel)) nearbyVessels.Add(partHit.vessel);
                    }
                    catch (Exception e) // ignored
                    {
                        Debug.LogWarning("[BDArmory.PooledBullet]: Exception thrown in CheckBulletCollisionWithVessels: " + e.Message + "\n" + e.StackTrace);
                    }
                }
            }
            foreach (var vessel in nearbyVessels.OrderBy(v => (v.transform.position - transform.position).sqrMagnitude))
            {
                CheckBulletCollisionWithVessel(period, vessel); // FIXME Convert this to use RaycastCommand to do all the raycasts in parallel.
            }
        }

        public void CheckBulletCollisionWithVessel(float period, Vessel vessel)
        {
            var relativeVelocity = currentVelocity - (Vector3)vessel.Velocity();
            float dist = relativeVelocity.magnitude * period;
            bulletRay = new Ray(currPosition, relativeVelocity + 0.5f * period * FlightGlobals.getGeeForceAtPosition(transform.position));
            const int layerMask = (int)(LayerMasks.Parts | LayerMasks.EVA | LayerMasks.Wheels);

            var hitCount = Physics.RaycastNonAlloc(bulletRay, hits, dist, layerMask);
            if (hitCount == hits.Length) // If there's a whole bunch of stuff in the way (unlikely), then we need to increase the size of our hits buffer.
            {
                hits = Physics.RaycastAll(bulletRay, dist, layerMask);
                hitCount = hits.Length;
            }

            var reverseRay = new Ray(bulletRay.origin + dist * bulletRay.direction, -bulletRay.direction);
            var reverseHitCount = Physics.RaycastNonAlloc(reverseRay, reverseHits, dist, layerMask);
            if (reverseHitCount == reverseHits.Length)
            {
                reverseHits = Physics.RaycastAll(reverseRay, dist, layerMask);
                reverseHitCount = reverseHits.Length;
            }
            for (int i = 0; i < reverseHitCount; ++i)
            { reverseHits[i].distance = dist - reverseHits[i].distance; }

            if (hitCount + reverseHitCount > 0)
            {
                bool hitFound = false;
                Part hitPart;
                using (var hit = hits.Take(hitCount).AsEnumerable().GetEnumerator())
                    while (hit.MoveNext())
                    {
                        hitPart = hit.Current.collider.gameObject.GetComponentInParent<Part>();
                        if (hitPart == null) continue;
                        if (hitPart.vessel == vessel) allHits.Add(hit.Current);
                        if (!hitFound) hitFound = true;
                    }
                using (var hit = reverseHits.Take(reverseHitCount).AsEnumerable().GetEnumerator())
                    while (hit.MoveNext())
                    {
                        hitPart = hit.Current.collider.gameObject.GetComponentInParent<Part>();
                        if (hitPart == null) continue;
                        if (hitPart.vessel == vessel) allHits.Add(hit.Current);
                        if (!hitFound) hitFound = true;
                    }
                if (hitFound) rayLength[vessel] = dist;
            }
        }

        public void CheckBulletCollisionWithScenery(float period)
        {
            float dist = currentVelocity.magnitude * period;
            bulletRay = new Ray(currPosition, currentVelocity + 0.5f * period * FlightGlobals.getGeeForceAtPosition(transform.position));
            var layerMask = (int)(LayerMasks.Scenery);

            var hitCount = Physics.RaycastNonAlloc(bulletRay, hits, dist, layerMask);
            if (hitCount == hits.Length) // If there's a whole bunch of stuff in the way (unlikely), then we need to increase the size of our hits buffer.
            {
                hits = Physics.RaycastAll(bulletRay, dist, layerMask);
                hitCount = hits.Length;
            }
            allHits.AddRange(hits.Take(hitCount));

            var reverseRay = new Ray(bulletRay.origin + dist * bulletRay.direction, -bulletRay.direction);
            var reverseHitCount = Physics.RaycastNonAlloc(reverseRay, reverseHits, dist, layerMask);
            if (reverseHitCount == reverseHits.Length)
            {
                reverseHits = Physics.RaycastAll(reverseRay, dist, layerMask);
                reverseHitCount = reverseHits.Length;
            }
            for (int i = 0; i < reverseHitCount; ++i)
            { reverseHits[i].distance = dist - reverseHits[i].distance; }
            allHits.AddRange(reverseHits.Take(reverseHitCount));
        }

        /// <summary>
        /// Check for bullet collision in the upcoming period. 
        /// This also performs a raycast in reverse to detect collisions from rays starting within an object.
        /// </summary>
        /// <param name="period">Period to consider, typically Time.fixedDeltaTime</param>
        /// <returns>true if a collision is detected, false otherwise.</returns>
        public bool CheckBulletCollision(float period)
        {
            float dist = currentVelocity.magnitude * period;
            bulletRay = new Ray(currPosition, currentVelocity + 0.5f * period * FlightGlobals.getGeeForceAtPosition(transform.position));
            const int layerMask = (int)(LayerMasks.Parts | LayerMasks.EVA | LayerMasks.Wheels | LayerMasks.Scenery);
            var hitCount = Physics.RaycastNonAlloc(bulletRay, hits, dist, layerMask);
            if (hitCount == hits.Length) // If there's a whole bunch of stuff in the way (unlikely), then we need to increase the size of our hits buffer.
            {
                hits = Physics.RaycastAll(bulletRay, dist, layerMask);
                hitCount = hits.Length;
            }

            var reverseRay = new Ray(bulletRay.origin + dist * bulletRay.direction, -bulletRay.direction);
            var reverseHitCount = Physics.RaycastNonAlloc(reverseRay, reverseHits, dist, layerMask);
            if (reverseHitCount == reverseHits.Length)
            {
                reverseHits = Physics.RaycastAll(reverseRay, dist, layerMask);
                reverseHitCount = reverseHits.Length;
            }
            for (int i = 0; i < reverseHitCount; ++i)
            { reverseHits[i].distance = dist - reverseHits[i].distance; }

            if (hitCount + reverseHitCount > 0)
            {
                // Note: this should probably use something like the CollateHits function in ExplosionFX, but doesn't seem to be as performance critical here.
                var orderedHits = hits.Take(hitCount).Concat(reverseHits.Take(reverseHitCount)).OrderBy(x => x.distance);
                using (var hit = orderedHits.GetEnumerator())
                    while (hit.MoveNext()) if (BulletHitAnalysis(hit.Current, period)) return true;
            }
            return false;
        }

        /// <summary>
        /// Internals of the bullet collision hits loop in CheckBulletCollision so it can also be called from CheckBulletCollisionWithVessel.
        /// </summary>
        /// <param name="hit">The raycast hit.</param>
        /// <param name="vesselHit">Whether the hit is a vessel hit or not.</param>
        /// <param name="dist">The distance the bullet moved in the current reference frame.</param>
        /// <param name="period">The period the bullet moved for.</param>
        /// <returns>true if the bullet hits and dies, false otherwise.</returns>
        bool BulletHitAnalysis(RaycastHit hit, float period)
        {

            if (!hasPenetrated || hasRicocheted || hasDetonated)
            {
                return true;
            }
            Part hitPart;
            KerbalEVA hitEVA;
            try
            {
                hitPart = hit.collider.gameObject.GetComponentInParent<Part>();
                hitEVA = hit.collider.gameObject.GetComponentUpwards<KerbalEVA>();
            }
            catch (NullReferenceException e)
            {
                Debug.Log("[BDArmory.PooledBullet]:NullReferenceException for Ballistic Hit: " + e.Message);
                return true;
            }

            if (hitPart != null && ProjectileUtils.IsIgnoredPart(hitPart)) return false; // Ignore ignored parts.
            if (hitPart != null && hitPart == sourceWeapon) return false; // Ignore weapon that fired the bullet.
            if (hitPart != null && (hitPart == CurrentPart && ProjectileUtils.IsArmorPart(CurrentPart))) return false; //only have bullet hit armor panels once - no back armor to hit if penetration

            CurrentPart = hitPart;
            if (hitEVA != null)
            {
                hitPart = hitEVA.part;
                // relative velocity, separate from the below statement, because the hitpart might be assigned only above
                if (hitPart.rb != null)
                    impactSpeed = (GetDragAdjustedVelocity() - (hitPart.rb.velocity + BDKrakensbane.FrameVelocityV3f)).magnitude;
                else
                    impactSpeed = GetDragAdjustedVelocity().magnitude;
                distanceTraveled += hit.distance;
                if (dmgMult < 0)
                {
                    hitPart.AddInstagibDamage();
                }
                else
                {
                    ProjectileUtils.ApplyDamage(hitPart, hit, dmgMult, 1, caliber, bulletMass, impactSpeed, bulletDmgMult, distanceTraveled, HEType != PooledBulletTypes.Slug ? true : false, incendiary, hasRicocheted, sourceVessel, bullet.name, team, ExplosionSourceType.Bullet, true, true, true);
                }
                ExplosiveDetonation(hitPart, hit, bulletRay);
                ResourceUtils.StealResources(hitPart, sourceVessel, stealResources);
                KillBullet(); // Kerbals are too thick-headed for penetration...
                return true;
            }

            if (hitPart != null && hitPart.vessel == sourceVessel) return false;  //avoid autohit;

            Vector3 impactVelocity = GetDragAdjustedVelocity();
            if (hitPart != null && hitPart.rb != null)
            {
                // using relative velocity vector instead of just bullet velocity
                // since KSP vessels might move faster than bullets
                impactVelocity -= (hitPart.rb.velocity + BDKrakensbane.FrameVelocityV3f);
            }

            impactSpeed = impactVelocity.magnitude;

            float length = ((bulletMass * 1000.0f * 400.0f) / ((caliber * caliber *
                    Mathf.PI) * (sabot ? 19.0f : 11.34f)) + 1.0f) * 10.0f;

            // New system to wear down hypervelocity projectiles over distance
            // This is based on an equation that was derived for shaped charges. Now this isn't
            // exactly accurate in our case, or rather it's not accurate at all, but it's something
            // in the ballpark and it's also more-or-less going to give the proper behavior for
            // spaced armor at high velocities. This is sourced from https://www.diva-portal.org/smash/get/diva2:643824/FULLTEXT01.pdf
            // and once again, I must emphasize. This is for shaped charges, it's not for post-penetration
            // behavior of hypervelocity projectiles, but I'm going to hand-wave away the difference between
            // the plasma that flies out after a penetration and the armor-piercing jet of a shaped charge.
            if (impactSpeed > 2500 || !double.IsPositiveInfinity(distanceLastHit))
            {
                // This only applies to anything that will actually survive an impact so EMP, impulse and any HE rounds that explode right away are out
                if (!EMP || impulse != 0 || ((HERatio > 0) && (fuzeType != BulletFuzeTypes.Penetrating || fuzeType != BulletFuzeTypes.Delay)))
                {

                    // This is just because if distanceSinceHit < (7f * caliber * 10f) penetration will be worse, this behavior is true of 
                    // shaped charges due to the jet formation distance, however we're going to ignore it since it isn't true of a hypervelocity
                    // projectile that's just smashed through some armor.
                    //if ((distanceTraveled + hit.distance - distanceLastHit) * 1000f > (7f * caliber * 10f))

                    // Changed from the previous 7 * caliber * 10 maximum to just > caliber since that no longer exists.
                    if ((distanceTraveled + hit.distance - distanceLastHit) * 1000f > caliber)
                    {
                        // The formula is based on distance and the caliber of the shaped charge, now since in our case we are talking
                        // about projectiles rather than shaped charges we'll take the projectile diameter and call that the jet size.
                        // Shaped charge jets have a diameter generally around 5% of the shaped charge's caliber, however in our case
                        // this means these projectiles wouldn't bleed off that hard with distance, thus we'll say they're 10% of the
                        // shaped charge's caliber.
                        // Calculating this term once since division is expensive
                        //float kTerm = ((float)(distanceTraveled + hit.distance - distanceLastHit) * 1000f - 7f * 10f *caliber) / (14f * 10f * caliber);
                        // Modified this from the original formula, commented out above to remove the standoff distance required for jet formation.
                        // Just makes more sense to me not to have it in there.
                        float kTerm = ((float)(distanceTraveled + hit.distance - distanceLastHit) * 1000f) / (14f * 10f * caliber);
                        kDist = 1f / (kDist * (1f + kTerm * kTerm)); // Then using it in the formula
                        
                        // If the projectile gets too small things go wonky with the formulas for penetration
                        // they'll still work honestly, but I'd rather avoid those situations
                        /*if ((kDist * length) < 1.2f * caliber)
                        {
                            float massFactor = (1.2f * caliber / length);
                            bulletMass = bulletMass * massFactor;
                            length = (length - 10) * massFactor + 10;
                        }
                        else
                        {
                            bulletMass = bulletMass * kDist;
                            length = (length - 10) * kDist + 10;
                        }*/

                        // Deprecated above since the penetration formula was modified to
                        // deal with such cases
                        bulletMass = bulletMass * kDist;
                        length = (length - 10) * kDist + 10;

                        if (BDArmorySettings.DEBUG_WEAPONS)
                        {
                            Debug.Log("[BDArmory.PooledBullet] kDist: " + kDist + ". Distance Since Last Hit: " + (distanceTraveled + hit.distance - distanceLastHit) + " m.");
                        }
                    }

                    if (double.IsPositiveInfinity(distanceLastHit))
                    {
                        // Add the distance since this hit so the next part that gets hit has the proper distance
                        distanceLastHit = distanceTraveled + hit.distance;
                    }
                }
            }

            float hitAngle = Vector3.Angle(impactVelocity, -hit.normal);
            float dist = hitPart != null && hitPart.vessel != null && rayLength.ContainsKey(hitPart.vessel) ? rayLength[hitPart.vessel] : currentVelocity.magnitude * period;

            if (ProjectileUtils.CheckGroundHit(hitPart, hit, caliber))
            {
                if (!BDArmorySettings.PAINTBALL_MODE) ProjectileUtils.CheckBuildingHit(hit, bulletMass, impactVelocity, bulletDmgMult);
                if (!RicochetScenery(hitAngle))
                {
                    ExplosiveDetonation(hitPart, hit, bulletRay);
                    KillBullet();
                    distanceTraveled += hit.distance;
                    return true;
                }
                else
                {
                    if (fuzeType == BulletFuzeTypes.Impact)
                    {
                        ExplosiveDetonation(hitPart, hit, bulletRay);
                    }
                    DoRicochet(hitPart, hit, hitAngle, hit.distance / dist, period);
                    return true;
                }
            }
            if (hitPart == null) return false; // Hits below here are part hits.

            //Standard Pipeline Hitpoints, Armor and Explosives
            //impactSpeed = impactVelocity.magnitude; //Moved up for the projectile weardown calculation
            if (massMod != 0)
            {
                var ME = hitPart.FindModuleImplementing<ModuleMassAdjust>();
                if (ME == null)
                {
                    ME = (ModuleMassAdjust)hitPart.AddModule("ModuleMassAdjust");
                }
                ME.massMod += massMod;
                ME.duration += BDArmorySettings.WEAPON_FX_DURATION;
            }
            if (EMP)
            {
                var emp = hitPart.vessel.rootPart.FindModuleImplementing<ModuleDrainEC>();
                if (emp == null)
                {
                    emp = (ModuleDrainEC)hitPart.vessel.rootPart.AddModule("ModuleDrainEC");
                }
                emp.incomingDamage += (caliber * Mathf.Clamp(bulletMass - tntMass, 0.1f, 101)); //soft EMP caps at 100; can always add a EMP amount value to bulletcfg later, but this should work for now
                emp.softEMP = true;
            }
            if (impulse != 0 && hitPart.rb != null)
            {
                distanceTraveled += hit.distance;
                if (!BDArmorySettings.PAINTBALL_MODE)
                { hitPart.rb.AddForceAtPosition(impactVelocity.normalized * impulse, hit.point, ForceMode.Acceleration); }
                ProjectileUtils.ApplyScore(hitPart, sourceVessel.GetName(), distanceTraveled, 0, bullet.name, ExplosionSourceType.Bullet, true);
                if (BDArmorySettings.BULLET_HITS)
                {
                    BulletHitFX.CreateBulletHit(hitPart, hit.point, hit, hit.normal, false, caliber, 0, team);
                }
                KillBullet();
                return true; //impulse rounds shouldn't penetrate/do damage
            }
            float anglemultiplier = (float)Math.Cos(Math.PI * hitAngle / 180.0);
            //calculate armor thickness
            float thickness = ProjectileUtils.CalculateThickness(hitPart, anglemultiplier);
            //calculate armor strength
            float penetration = 0;
            float penetrationFactor = 0;
            //float length = 0; //Moved up for the new bullet wear over distance system
            var Armor = hitPart.FindModuleImplementing<HitpointTracker>();
            if (Armor != null)
            {
                float Ductility = Armor.Ductility;
                float hardness = Armor.Hardness;
                float Strength = Armor.Strength;
                float safeTemp = Armor.SafeUseTemp;
                float Density = Armor.Density;

                float vFactor = Armor.vFactor;
                float muParam1;
                float muParam2;
                float muParam3;

                if (hitPart.skinTemperature > safeTemp) //has the armor started melting/denaturing/whatever?
                {
                    //vFactor *= 1/(1.25f*0.75f-0.25f*0.75f*0.75f);
                    vFactor *= 1.25490196078f; // Uses the above equation but just calculated out.
                    // The equation 1/(1.25*x-0.25*x^2) approximates the effect of changing yield strength
                    // by a factor of x
                    if (hitPart.skinTemperature > safeTemp * 1.5f)
                    {
                        vFactor *= 1.77777777778f; // Same as used above, but here with x = 0.5. Maybe this should be
                        // some kind of a curve?
                    }
                }

                int armorType = (int)Armor.ArmorTypeNum;
                if (BDArmorySettings.DEBUG_ARMOR)
                {
                    Debug.Log("[BDArmory.PooledBullet]: ArmorVars found: Strength : " + Strength + "; Ductility: " + Ductility + "; Hardness: " + hardness + "; MaxTemp: " + safeTemp + "; Density: " + Density + "; thickness: " + thickness);
                }

                //calculate bullet deformation
                float newCaliber = caliber;
                //length = ((bulletMass * 1000.0f * 400.0f) / ((caliber * caliber *
                //    Mathf.PI) * (sabot ? 19.0f : 11.34f)) + 1.0f) * 10.0f; //Moved up for the purposes of the new bullet wear over distance system

                /*
                if (Ductility > 0.05)
                {
                */

                if (!sabot)
                {
                    // Moved the bulletEnergy and armorStrength calculations here because
                    // they are no longer needed for CalculatePenetration. This should
                    // improve performance somewhat for sabot rounds, which is a good
                    // thing since that new model requires the use of Mathf.Log and
                    // Mathf.Exp.
                    float bulletEnergy = ProjectileUtils.CalculateProjectileEnergy(bulletMass, impactSpeed);
                    float armorStrength = ProjectileUtils.CalculateArmorStrength(caliber, thickness, Ductility, Strength, Density, safeTemp, hitPart);
                    newCaliber = ProjectileUtils.CalculateDeformation(armorStrength, bulletEnergy, caliber, impactSpeed, hardness, Density, HERatio, apBulletMod, sabot);

                    // Also set the params to the non-sabot ones
                    muParam1 = Armor.muParam1;
                    muParam2 = Armor.muParam2;
                    muParam3 = Armor.muParam3;
                }
                else
                {
                    // If it's a sabot just set the params to the sabot ones
                    muParam1 = Armor.muParam1S;
                    muParam2 = Armor.muParam2S;
                    muParam3 = Armor.muParam3S;
                }
                //penetration = ProjectileUtils.CalculatePenetration(caliber, newCaliber, bulletMass, impactSpeed, Ductility, Density, Strength, thickness, apBulletMod, sabot);
                penetration = ProjectileUtils.CalculatePenetration(caliber, impactSpeed, bulletMass, apBulletMod, Strength, vFactor, muParam1, muParam2, muParam3, sabot, length);

                if (BDArmorySettings.DEBUG_WEAPONS)
                {
                    Debug.Log("[BDArmory.PooledBullet] Penetration: " + penetration + "mm. impactSpeed: " + impactSpeed + "m/s. bulletMass = " + bulletMass + "kg. Caliber: " + caliber + "mm. Length: " + length + "mm. Sabot: " + sabot);
                }

                /*
                }
                else
                {
                    float bulletEnergy = ProjectileUtils.CalculateProjectileEnergy(bulletMass, impactSpeed);
                    float armorStrength = ProjectileUtils.CalculateArmorStrength(caliber, thickness, Ductility, Strength, Density, safeTemp, hitPart);
                    newCaliber = ProjectileUtils.CalculateDeformation(armorStrength, bulletEnergy, caliber, impactSpeed, hardness, Density, HERatio, apBulletMod, sabot);
                    penetration = ProjectileUtils.CalculateCeramicPenetration(caliber, newCaliber, bulletMass, impactSpeed, Ductility, Density, Strength, thickness, apBulletMod, sabot);
                }
                */

                caliber = newCaliber; //update bullet with new caliber post-deformation(if any)
                penetrationFactor = ProjectileUtils.CalculateArmorPenetration(hitPart, penetration, thickness);
                //Reactive Armor calcs
                //Round has managed to punch through front plate of RA, triggering RA
                //if NXRA, will activate on anything that can pen front plate

                var RA = hitPart.FindModuleImplementing<ModuleReactiveArmor>();
                if (RA != null)
                {
                    if (penetrationFactor > 1)
                    {
                        float thicknessModifier = RA.armorModifier;
                        if (BDArmorySettings.DEBUG_ARMOR) Debug.Log("[BDArmory.PooledBullet]: Beginning Reactive Armor Hit; NXRA: " + RA.NXRA + "; thickness Mod: " + RA.armorModifier);
                        if (RA.NXRA) //non-explosive RA, always active
                        {
                            thickness *= thicknessModifier;
                        }
                        else
                        {
                            if (sabot)
                            {
                                if (hitAngle < 80) //ERA isn't going to do much against near-perpendicular hits
                                {
                                    caliber = BDAMath.Sqrt((caliber * (((bulletMass * 1000) / ((caliber * caliber * Mathf.PI / 400) * 19)) + 1) * 4) / Mathf.PI); //increase caliber to sim sabot hitting perpendicualr instead of point-first
                                    bulletMass /= 2; //sunder sabot
                                                     //RA isn't going to stop sabot, but underlying part's armor will (probably)
                                    if (BDArmorySettings.DEBUG_ARMOR) Debug.Log("[BDArmory.PooledBullet]: Sabot caliber and mass now: " + caliber + ", " + bulletMass);
                                    RA.UpdateSectionScales();
                                }
                            }
                            else //standard rounds
                            {
                                if (caliber >= RA.sensitivity) //big enough round to trigger RA
                                {
                                    thickness *= thicknessModifier;
                                    if (fuzeType == BulletFuzeTypes.Delay || fuzeType == BulletFuzeTypes.Penetrating || fuzeType == BulletFuzeTypes.None) //non-explosive impact
                                    {
                                        RA.UpdateSectionScales(); //detonate RA section
                                                                  //explosive impacts handled in ExplosionFX
                                                                  //if explosive and contact fuze, kill bullet?
                                    }
                                }
                            }
                        }
                    }
                    penetrationFactor = ProjectileUtils.CalculateArmorPenetration(hitPart, penetration, thickness); //RA stop round?
                }
                else ProjectileUtils.CalculateArmorDamage(hitPart, penetrationFactor, caliber, hardness, Ductility, Density, impactSpeed, sourceVesselName, ExplosionSourceType.Bullet, armorType);
            }
            else
            {
                Debug.Log("[PooledBUllet].ArmorVars not found; hitPart null");
            }
            //determine what happens to bullet
            //pen < 1: bullet stopped by armor
            //pen > 1 && <2: bullet makes it into part, but can't punch through other side
            //pen > 2: bullet goes stragiht through part and out other side
            if (penetrationFactor < 1) //stopped by armor
            {
                if (RicochetOnPart(hitPart, hit, hitAngle, impactSpeed, hit.distance / dist, period))
                {
                    bool viableBullet = ProjectileUtils.CalculateBulletStatus(bulletMass, caliber, sabot);
                    if (!viableBullet)
                    {
                        distanceTraveled += hit.distance;
                        KillBullet();
                        return true;
                    }
                    else
                    {
                        //rounds w/ contact fuzes are going to detoante anyway
                        if (fuzeType == BulletFuzeTypes.Impact || fuzeType == BulletFuzeTypes.Timed)
                        {
                            ExplosiveDetonation(hitPart, hit, bulletRay);
                            ProjectileUtils.CalculateShrapnelDamage(hitPart, hit, caliber, tntMass, 0, sourceVesselName, ExplosionSourceType.Bullet, bulletMass, penetrationFactor); //calc daamge from bullet exploding 
                        }
                        if (fuzeType == BulletFuzeTypes.Delay)
                        {
                            fuzeTriggered = true;
                        }
                        hasRicocheted = true;
                    }
                }
                if (!hasRicocheted) // explosive bullets that get stopped by armor will explode
                {
                    if (hitPart.rb != null && hitPart.rb.mass > 0)
                    {
                        float forceAverageMagnitude = impactSpeed * impactSpeed * (1f / hit.distance) * bulletMass;

                        float accelerationMagnitude = forceAverageMagnitude / (hitPart.vessel.GetTotalMass() * 1000);

                        hitPart.rb.AddForceAtPosition(impactVelocity.normalized * accelerationMagnitude, hit.point, ForceMode.Acceleration);

                        if (BDArmorySettings.DEBUG_WEAPONS)
                            Debug.Log("[BDArmory.PooledBullet]: Force Applied " + Math.Round(accelerationMagnitude, 2) + "| Vessel mass in kgs=" + hitPart.vessel.GetTotalMass() * 1000 + "| bullet effective mass =" + (bulletMass - tntMass));
                    }
                    distanceTraveled += hit.distance;
                    hasPenetrated = false;
                    if (dmgMult < 0)
                    {
                        hitPart.AddInstagibDamage();
                    }
                    if (fuzeTriggered)
                    {
                        //Debug.Log("[BDArmory.PooledBullet]: Active Delay Fuze failed to penetrate, detonating");
                        fuzeTriggered = false;
                        StopCoroutine(DelayedDetonationRoutine());
                    }
                    ExplosiveDetonation(hitPart, hit, bulletRay);
                    ProjectileUtils.CalculateShrapnelDamage(hitPart, hit, caliber, tntMass, 0, sourceVesselName, ExplosionSourceType.Bullet, bulletMass, penetrationFactor); //calc damage from bullet exploding 
                    ProjectileUtils.ApplyScore(hitPart, sourceVesselName, distanceTraveled, 0, bullet.name, ExplosionSourceType.Bullet, penTicker > 0 ? false : true);
                    hasDetonated = true;
                    KillBullet();
                    return true;
                }
            }
            else //penetration >= 1
            {
                // Old Post Pen Behavior
                //currentVelocity = currentVelocity * (1 - (float)Math.Sqrt(thickness / penetration));
                //impactVelocity = impactVelocity * (1 - (float)Math.Sqrt(thickness / penetration));

                // New Post Pen Behavior, this is quite game-ified and not really based heavily on
                // actual proper equations, however it does try to get the same kind of behavior as
                // would be found IRL. Primarily, this means high velocity impacts will be mostly
                // eroding the projectile rather than slowing it down (all studies of this behavior
                // show residual velocity only decreases slightly during penetration while the
                // projectile is getting eroded, then starts decreasing rapidly as the projectile
                // approaches a L/D ratio of 1. Erosion is drastically increased at 2500 m/s + in
                // order to try and replicate the projectile basically vaporizing at high velocities.
                // Note that the velocity thresholds are really mostly arbitrary and that the vaporizing
                // behavior isn't that accurate since the projectile would remain semi-coherent immediately
                // after penetration and would disperse over time, hence the spacing in stuff like
                // whipple shields but that behavior is fairly complex and I'm already in way over my head.

                // Calculating this ratio once since we're going to need it a bunch
                float adjustedPenRatio = (1 - BDAMath.Sqrt(thickness / penetration));

                // If impact is at high speed
                if (impactSpeed > 1200f)
                {
                    // If the projectile is still above a L/D ratio of 1.1 (should be 1 but I want to
                    // avoid the edge case in the pen formula where L/D = 1)
                    if (length / caliber > 1.1f)
                    {
                        // Then we set the mass ratio to the default for impacts under 2500 m/s
                        // we take off 5% by default to decrease penetration efficiency through
                        // multiple plates a little more
                        float massRatio = 0.975f * adjustedPenRatio;

                        if (impactSpeed > 2500f)
                        {
                            // If impact speed is really high then spaced armor wil work exceptionally
                            // well, with increasing results as the velocity of the projectile goes
                            // higher. This is mostly to make whipple shields viable and desireable
                            // in railgun combat. Ideally we'd be modelling the projectile vaporizing
                            // and then losing coherence as it travels through empty space between the
                            // outer plate and the inner plate but I'm not quite sure how that behavior
                            // would look like. Best way to probably do that is to decrease projectile
                            // lifespan and to add a lastImpact timestamp and do some kind of decrease
                            // in mass as a function of the time between impacts.
                            //massRatio = 2375f / impactSpeed * adjustedPenRatio;

                            // Adjusted the above formula so only up to 50% of the mass could be lost
                            // immediately upon penetration (to being vaporized). At this point this
                            // stuff I've accepted is going to be purely gameified. If anybody has
                            // an expertise in hypervelocity impact mechanics they're welcome to
                            // change all this stuff I'm doing for hypervelocity stuff.
                            massRatio = (0.45f + 0.5f * (2500f / impactSpeed)) * adjustedPenRatio;
                        }
                        
                        

                        // We cap the minimum L/D to be 1.2 to avoid that edge case in the pen formula
                        if ((massRatio * (length-10f) + 10f) < (1.1f * caliber))
                        {
                            float ratio;

                            if (caliber < 10f)
                            {
                                ratio = (1.1f * caliber / length);
                            }
                            else
                            {
                                ratio = (1.1f * caliber - 10f) / (length - 10f);
                            }

                            bulletMass *= ratio;

                            adjustedPenRatio /= ratio;

                            // In the case we are reaching that cap we decrease the velocity by
                            // the adjustedPenRatio minus the portion that went into erosion
                            currentVelocity = currentVelocity * adjustedPenRatio;
                            impactVelocity = impactVelocity * adjustedPenRatio;
                        }
                        else
                        {
                            bulletMass = bulletMass * massRatio;

                            // If we don't, I.E. the round isn't completely eroded, we decrease
                            // the velocity by a max of 5%, proportional to the adjustedPenRatio
                            currentVelocity = currentVelocity * (0.95f + 0.05f * adjustedPenRatio);
                            impactVelocity = impactVelocity * (0.95f + 0.05f * adjustedPenRatio);
                        }
                    }
                    else
                    {
                        // If the projectile has already been eroded away we just decrease the
                        // velocity by the adjustedPenRatio
                        currentVelocity = currentVelocity * adjustedPenRatio;
                        impactVelocity = impactVelocity * adjustedPenRatio;
                    }
                }
                else
                {
                    // Low velocity impacts behave the same as before
                    currentVelocity = currentVelocity * adjustedPenRatio;
                    impactVelocity = impactVelocity * adjustedPenRatio;
                }

                currentSpeed = currentVelocity.magnitude;
                timeElapsedSinceCurrentSpeedWasAdjusted = 0;

                float bulletDragArea = Mathf.PI * (caliber * caliber / 4f); //if bullet not killed by impact, possbily deformed from impact; grab new ballistic coeff for drag
                ballisticCoefficient = bulletMass / ((bulletDragArea / 1000000f) * 0.295f); // mm^2 to m^2
                hasRicocheted = false; // bullet inside part

                //fully penetrated continue ballistic damage
                hasPenetrated = true;
                bool viableBullet = ProjectileUtils.CalculateBulletStatus(bulletMass, caliber, sabot);

                ResourceUtils.StealResources(hitPart, sourceVessel, stealResources);
                //ProjectileUtils.CheckPartForExplosion(hitPart);

                if (dmgMult < 0)
                {
                    hitPart.AddInstagibDamage();
                    ProjectileUtils.ApplyScore(hitPart, sourceVessel.GetName(), distanceTraveled, 0, bullet.name, ExplosionSourceType.Bullet, true);
                }
                else
                {
                    float cockpitPen = (float)(16f * impactVelocity.magnitude * BDAMath.Sqrt(bulletMass / 1000) / BDAMath.Sqrt(caliber) * apBulletMod); //assuming a 20mm steel armor plate for cockpit armor
                    ProjectileUtils.ApplyDamage(hitPart, hit, dmgMult, penetrationFactor, caliber, bulletMass, currentVelocity.magnitude, viableBullet ? bulletDmgMult : bulletDmgMult / 2, distanceTraveled, HEType != PooledBulletTypes.Slug ? true : false, incendiary, hasRicocheted, sourceVessel, bullet.name, team, ExplosionSourceType.Bullet, penTicker > 0 ? false : true, partsHit.Contains(hitPart) ? false : true, (cockpitPen > Mathf.Max(20 / anglemultiplier, 1)) ? true : false);
                    //need to add a check for if the bullet has already struck the part, since it doesn't make sense for some battledamage to apply on the second hit from the bullet exiting the part - wings/ctrl srfs, pilot kills, subsystem damage
                }

                //Delay and Penetrating Fuze bullets that penetrate should explode shortly after
                //if penetration is very great, they will have moved on                            
                //if (explosive && penetrationFactor < 3 || !viableBullet)
                if (HEType != PooledBulletTypes.Slug)
                {
                    if (fuzeType == BulletFuzeTypes.Delay)
                    {
                        //transform.position += (currentVelocity * period) / 3; //when using post-penetration currentVelocity, this yields distances pretty close to distance a Delay fuze would travel before detonation
                        //commented out, since this could cause explosions to phase through armor/parts between hit point and detonation point
                        //distanceTraveled += hit.distance;
                        if (!fuzeTriggered)
                        {
                            if (BDArmorySettings.DEBUG_WEAPONS) Debug.Log("[BDArmory.PooledBullet]: Delay Fuze Tripped at t: " + Time.time);
                            fuzeTriggered = true;
                            StartCoroutine(DelayedDetonationRoutine());
                        }
                    }
                    else if (fuzeType == BulletFuzeTypes.Penetrating) //should look into having this be a set depth. For now, assume fancy inertial/electrical mechanism for detecting armor thickness based on time spent passing through
                    {
                        if (penetrationFactor < 1.5f)
                        {
                            if (!fuzeTriggered)
                            {
                                if (BDArmorySettings.DEBUG_WEAPONS) Debug.Log("[BDArmory.PooledBullet]: Penetrating Fuze Tripped at t: " + Time.time);
                                fuzeTriggered = true;
                                StartCoroutine(DelayedDetonationRoutine());
                            }
                        }
                    }
                    else //impact by impact, Timed, Prox and Flak, if for whatever reason those last two have 0 proxi range
                    {
                        //if (BDArmorySettings.DEBUG_WEAPONS) Debug.Log("[BDArmory.PooledBullet]: impact Fuze detonation");
                        ExplosiveDetonation(hitPart, hit, bulletRay, true);
                        ProjectileUtils.CalculateShrapnelDamage(hitPart, hit, caliber, tntMass, 0, sourceVesselName, ExplosionSourceType.Bullet, bulletMass, penetrationFactor); //calc daamge from bullet exploding 
                        hasDetonated = true;
                        KillBullet();
                        distanceTraveled += hit.distance;
                        return true;
                    }
                    if (!viableBullet)
                    {
                        if (BDArmorySettings.DEBUG_WEAPONS) Debug.Log("[BDArmory.PooledBullet]: !viable bullet, removing");
                        ExplosiveDetonation(hitPart, hit, bulletRay, true);
                        ProjectileUtils.CalculateShrapnelDamage(hitPart, hit, caliber, tntMass, 0, sourceVesselName, ExplosionSourceType.Bullet, bulletMass, penetrationFactor); //calc daamge from bullet exploding
                        hasDetonated = true;
                        KillBullet();
                        distanceTraveled += hit.distance;
                        return true;
                    }
                }
                penTicker += 1;
            }
            if (!partsHit.Contains(hitPart)) partsHit.Add(hitPart);
            //bullet should not go any further if moving too slowly after hit
            //smaller caliber rounds would be too deformed to do any further damage
            if (currentVelocity.magnitude <= 100 && hasPenetrated)
            {
                if (BDArmorySettings.DEBUG_WEAPONS)
                {
                    Debug.Log("[BDArmory.PooledBullet]: Bullet Velocity too low, stopping");
                }
                if (!fuzeTriggered) KillBullet();
                distanceTraveled += hit.distance;
                return true;
            }
            return false;
        }

        IEnumerator DelayedDetonationRoutine()
        {
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            fuzeTriggered = false;
            if (!hasDetonated)
            {
                if (HEType != PooledBulletTypes.Slug)
                    ExplosionFx.CreateExplosion(currPosition, tntMass, explModelPath, explSoundPath, ExplosionSourceType.Bullet, caliber, null, sourceVesselName, null, HEType == PooledBulletTypes.Explosive ? default : currentVelocity, -1, false, bulletMass, -1, dmgMult, HEType == PooledBulletTypes.Shaped ? "shapedcharge" : "standard", CurrentPart, HEType == PooledBulletTypes.Shaped ? apBulletMod : 1f);
                if (nuclear)
                    NukeFX.CreateExplosion(currPosition, ExplosionSourceType.Bullet, sourceVesselName, bullet.DisplayName, 0, tntMass * 200, tntMass, tntMass, EMP, blastSoundPath, flashModelPath, shockModelPath, blastModelPath, plumeModelPath, debrisModelPath, "", "");
                hasDetonated = true;

                if (tntMass > 1)
                {
                    if ((FlightGlobals.getAltitudeAtPos(transform.position) <= 0) && (FlightGlobals.getAltitudeAtPos(transform.position) > -detonationRange))
                    {
                        double latitudeAtPos = FlightGlobals.currentMainBody.GetLatitude(transform.position);
                        double longitudeAtPos = FlightGlobals.currentMainBody.GetLongitude(transform.position);
                        FXMonger.Splash(FlightGlobals.currentMainBody.GetWorldSurfacePosition(latitudeAtPos, longitudeAtPos, 0), tntMass * 20);
                    }
                }
                if (BDArmorySettings.DEBUG_WEAPONS) Debug.Log("[BDArmory.PooledBullet]: Delayed Detonation at: " + Time.time);
                KillBullet();
            }
        }
        public void BeehiveDetonation()
        {
            if (subMunitionType == null)
            {
                Debug.Log("[BDArmory.PooledBullet] Beehive round not configured with subMunitionType!");
                return;
            }
            string fuze = subMunitionType.fuzeType;
            fuze.ToLower();
            BulletFuzeTypes sFuze;
            switch (fuze)
            {
                case "timed":
                    sFuze = PooledBullet.BulletFuzeTypes.Timed;
                    break;
                case "proximity":
                    sFuze = PooledBullet.BulletFuzeTypes.Proximity;
                    break;
                case "flak":
                    sFuze = PooledBullet.BulletFuzeTypes.Flak;
                    break;
                case "delay":
                    sFuze = PooledBullet.BulletFuzeTypes.Delay;
                    break;
                case "penetrating":
                    sFuze = PooledBullet.BulletFuzeTypes.Penetrating;
                    break;
                case "impact":
                    sFuze = PooledBullet.BulletFuzeTypes.Impact;
                    break;
                case "none":
                    sFuze = PooledBullet.BulletFuzeTypes.None;
                    break;
                default:
                    sFuze = PooledBullet.BulletFuzeTypes.Impact;
                    break;
            }
            for (int s = 0; s < subMunitionType.subProjectileCount; s++)
            {
                GameObject Bullet = ModuleWeapon.bulletPool.GetPooledObject();
                PooledBullet pBullet = Bullet.GetComponent<PooledBullet>();
                pBullet.transform.position = currPosition;

                pBullet.caliber = subMunitionType.caliber;
                pBullet.bulletVelocity = subMunitionType.bulletVelocity;
                pBullet.bulletMass = subMunitionType.bulletMass;
                pBullet.incendiary = subMunitionType.incendiary;
                pBullet.apBulletMod = subMunitionType.apBulletMod;
                pBullet.bulletDmgMult = bulletDmgMult;
                pBullet.ballisticCoefficient = subMunitionType.bulletMass / (((Mathf.PI * 0.25f * subMunitionType.caliber * subMunitionType.caliber) / 1000000f) * 0.295f);
                pBullet.timeElapsedSinceCurrentSpeedWasAdjusted = 0;
                pBullet.timeToLiveUntil = 2000 / bulletVelocity * 1.1f + Time.time;
                Vector3 firedVelocity = VectorUtils.GaussianDirectionDeviation(currentVelocity.normalized, subMunitionType.subProjectileDispersion > 0 ? subMunitionType.subProjectileDispersion : (subMunitionType.subProjectileCount / BDAMath.Sqrt((currentVelocity * dragVelocityFactor).magnitude / 100))) * subMunitionType.bulletVelocity; //more subprojectiles = wider spread, higher base velocity = tighter spread
                pBullet.currentVelocity = GetDragAdjustedVelocity() + firedVelocity; // currentVelocity is already the real velocity w/o offloading
                pBullet.sourceWeapon = sourceWeapon;
                pBullet.sourceVessel = sourceVessel;
                pBullet.team = team;
                pBullet.bulletTexturePath = bulletTexturePath;
                pBullet.projectileColor = GUIUtils.ParseColor255(subMunitionType.projectileColor);
                pBullet.startColor = GUIUtils.ParseColor255(subMunitionType.startColor);
                pBullet.fadeColor = subMunitionType.fadeColor;
                pBullet.tracerStartWidth = subMunitionType.caliber / 300;
                pBullet.tracerEndWidth = subMunitionType.caliber / 750;
                pBullet.tracerLength = tracerLength;
                pBullet.tracerDeltaFactor = tracerDeltaFactor;
                pBullet.tracerLuminance = tracerLuminance;
                pBullet.bulletDrop = bulletDrop;

                if (subMunitionType.tntMass > 0 || subMunitionType.beehive)
                {
                    pBullet.explModelPath = explModelPath;
                    pBullet.explSoundPath = explSoundPath;
                    pBullet.tntMass = subMunitionType.tntMass;
                    string HEtype = subMunitionType.explosive;
                    HEtype.ToLower();
                    switch (HEtype)
                    {
                        case "standard":
                            pBullet.HEType = PooledBullet.PooledBulletTypes.Explosive;
                            break;
                        //legacy support for older configs that are still explosive = true
                        case "true":
                            pBullet.HEType = PooledBullet.PooledBulletTypes.Explosive;
                            break;
                        case "shaped":
                            pBullet.HEType = PooledBullet.PooledBulletTypes.Shaped;
                            break;
                    }
                    pBullet.detonationRange = detonationRange;
                    pBullet.maxAirDetonationRange = maxAirDetonationRange;
                    pBullet.defaultDetonationRange = defaultDetonationRange;
                    pBullet.fuzeType = sFuze;
                }
                else
                {
                    pBullet.fuzeType = PooledBullet.BulletFuzeTypes.None;
                    pBullet.sabot = (((((subMunitionType.bulletMass * 1000) / ((subMunitionType.caliber * subMunitionType.caliber * Mathf.PI / 400) * 19) + 1) * 10) > subMunitionType.caliber * 4)) ? true : false;
                    pBullet.HEType = PooledBullet.PooledBulletTypes.Slug;
                }
                pBullet.EMP = subMunitionType.EMP;
                pBullet.nuclear = subMunitionType.nuclear;
                pBullet.beehive = subMunitionType.beehive;
                pBullet.subMunitionType = BulletInfo.bullets[subMunitionType.subMunitionType];
                //pBullet.homing = BulletInfo.homing;
                pBullet.impulse = subMunitionType.impulse;
                pBullet.massMod = subMunitionType.massMod;
                switch (subMunitionType.bulletDragTypeName)
                {
                    case "None":
                        pBullet.dragType = PooledBullet.BulletDragTypes.None;
                        break;
                    case "AnalyticEstimate":
                        pBullet.dragType = PooledBullet.BulletDragTypes.AnalyticEstimate;
                        break;
                    case "NumericalIntegration":
                        pBullet.dragType = PooledBullet.BulletDragTypes.NumericalIntegration;
                        break;
                    default:
                        pBullet.dragType = PooledBullet.BulletDragTypes.AnalyticEstimate;
                        break;
                }
                pBullet.bullet = BulletInfo.bullets[subMunitionType.name];
                pBullet.stealResources = stealResources;
                pBullet.dmgMult = dmgMult;
                pBullet.isAPSprojectile = isAPSprojectile;
                pBullet.tgtShell = tgtShell;
                pBullet.tgtRocket = tgtRocket;
                pBullet.gameObject.SetActive(true);
            }
        }
        private bool ProximityAirDetonation(float distanceFromStart)
        {
            bool detonate = false;

            if (isAPSprojectile && (tgtShell != null || tgtRocket != null))
            {
                if (Vector3.Distance(transform.position, tgtShell != null ? tgtShell.transform.position : tgtRocket.transform.position) < detonationRange / 2)
                {
                    if (BDArmorySettings.DEBUG_WEAPONS)
                        Debug.Log("[BDArmory.PooledBullet]: bullet proximity to APS target | Distance overlap = " + detonationRange + "| tgt name = " + tgtShell != null ? tgtShell.name : tgtRocket.name);
                    return detonate = true;
                }
            }

            if (distanceTraveled <= detonationRange * 2.5f && (fuzeType == BulletFuzeTypes.Proximity || fuzeType == BulletFuzeTypes.Timed)) return false; //bullet not past arming distance

            if (!(((HEType != PooledBulletTypes.Slug || nuclear) && tntMass > 0) || beehive)) return false;


            if (fuzeType == BulletFuzeTypes.Timed || fuzeType == BulletFuzeTypes.Flak)
            {
                if (distanceFromStart > (beehive ? maxAirDetonationRange - 100 : maxAirDetonationRange) || distanceFromStart > (beehive ? defaultDetonationRange - 100 : defaultDetonationRange))
                {
                    return detonate = true;
                }
            }
            if (fuzeType == BulletFuzeTypes.Proximity || fuzeType == BulletFuzeTypes.Flak)
            {
                var layerMask = (int)(LayerMasks.Parts | LayerMasks.Scenery | LayerMasks.Unknown19 | LayerMasks.Wheels);
                var overlapSphereColliderCount = Physics.OverlapSphereNonAlloc(transform.position, detonationRange, proximityOverlapSphereColliders, layerMask);
                if (overlapSphereColliderCount == proximityOverlapSphereColliders.Length)
                {
                    proximityOverlapSphereColliders = Physics.OverlapSphere(transform.position, detonationRange, layerMask);
                    overlapSphereColliderCount = proximityOverlapSphereColliders.Length;
                }
                using (var hitsEnu = proximityOverlapSphereColliders.Take(overlapSphereColliderCount).GetEnumerator())
                {
                    while (hitsEnu.MoveNext())
                    {
                        if (hitsEnu.Current == null) continue;
                        try
                        {
                            Part partHit = hitsEnu.Current.GetComponentInParent<Part>();
                            if (partHit == null || partHit.vessel == null) continue;
                            if (partHit.vessel == sourceVessel) continue;
                            if (ProjectileUtils.IsIgnoredPart(partHit)) continue; // Ignore ignored parts.


                            if (BDArmorySettings.DEBUG_WEAPONS)
                                Debug.Log("[BDArmory.PooledBullet]: Bullet proximity sphere hit | Distance overlap = " + detonationRange + "| Part name = " + partHit.name);

                            return detonate = true;
                        }
                        catch (Exception e)
                        {
                            // ignored
                            Debug.LogWarning("[BDArmory.PooledBullet]: Exception thrown in ProximityAirDetonation: " + e.Message + "\n" + e.StackTrace);
                        }
                    }
                }
            }
            return detonate;
        }

        private void UpdateDragEstimate()
        {
            switch (dragType)
            {
                case BulletDragTypes.None: // Don't do anything else
                    return;

                case BulletDragTypes.AnalyticEstimate:
                    CalculateDragAnalyticEstimate(currentSpeed, timeElapsedSinceCurrentSpeedWasAdjusted);
                    break;

                case BulletDragTypes.NumericalIntegration: // Numerical Integration is currently Broken
                    CalculateDragNumericalIntegration();
                    break;
            }
        }

        private void CalculateDragNumericalIntegration()
        {
            Vector3 dragAcc = currentVelocity * currentVelocity.magnitude *
                              (float)
                              FlightGlobals.getAtmDensity(FlightGlobals.getStaticPressure(transform.position),
                                  FlightGlobals.getExternalTemperature(transform.position));
            dragAcc *= 0.5f;
            dragAcc /= ballisticCoefficient;

            currentVelocity -= dragAcc * TimeWarp.deltaTime;
            //numerical integration; using Euler is silly, but let's go with it anyway
        }

        private void CalculateDragAnalyticEstimate(float initialSpeed, float timeElapsed)
        {
            float atmDensity;
            if (underwater)
                atmDensity = 1030f; // Sea water (3% salt) has a density of 1030kg/m^3 at 4°C at sea level. https://en.wikipedia.org/wiki/Density#Various_materials
            else
                atmDensity = (float)FlightGlobals.getAtmDensity(FlightGlobals.getStaticPressure(currPosition), FlightGlobals.getExternalTemperature(currPosition));

            dragVelocityFactor = 2f * ballisticCoefficient / (timeElapsed * initialSpeed * atmDensity + 2f * ballisticCoefficient);

            // Force Drag = 1/2 atmdensity*velocity^2 * drag coeff * area
            // Derivation:
            //   F = 1/2 * ρ * v^2 * Cd * A
            //   Cb = m / (Cd * A)
            //   dv/dt = F / m = -1/2 * ρ v^2 m / Cb  (minus due to direction being opposite velocity)
            //     => ∫ 1/v^2 dv = -1/2 * ∫ ρ/Cb dt
            //     => -1/v = -1/2*t*ρ/Cb + a
            //     => v(t) = 2*Cb / (t*ρ + 2*Cb*a)
            //   v(0) = v0 => a = 1/v0
            //     => v(t) = 2*Cb*v0 / (t*v0*ρ + 2*Cb)
            //     => drag factor at time t is 2*Cb / (t*v0*ρ + 2*Cb)

        }

        private bool ExplosiveDetonation(Part hitPart, RaycastHit hit, Ray ray, bool penetratingHit = false)
        {
            ///////////////////////////////////////////////////////////////////////
            // High Explosive Detonation
            ///////////////////////////////////////////////////////////////////////
            if (fuzeType == BulletFuzeTypes.None) return false;
            if (hitPart == null || hitPart.vessel != sourceVessel)
            {
                //if bullet hits and is HE, detonate and kill bullet
                if ((HEType != PooledBulletTypes.Slug || nuclear) && tntMass > 0)
                {
                    if (BDArmorySettings.DEBUG_WEAPONS)
                    {
                        Debug.Log("[BDArmory.PooledBullet]: Detonation Triggered | penetration: " + hasPenetrated + " penTick: " + penTicker + " airDet: " + (fuzeType == BulletFuzeTypes.Timed || fuzeType == BulletFuzeTypes.Flak));
                    }
                    if (fuzeType == BulletFuzeTypes.Timed || fuzeType == BulletFuzeTypes.Flak)
                    {
                        if (HEType != PooledBulletTypes.Slug)
                            ExplosionFx.CreateExplosion(hit.point, GetExplosivePower(), explModelPath, explSoundPath, ExplosionSourceType.Bullet, caliber, null, sourceVesselName, null, HEType == PooledBulletTypes.Explosive ? default : ray.direction, -1, false, bulletMass, -1, dmgMult, HEType == PooledBulletTypes.Shaped ? "shapedcharge" : "standard", null, HEType == PooledBulletTypes.Shaped ? apBulletMod : 1f);
                        if (nuclear)
                            NukeFX.CreateExplosion(currPosition, ExplosionSourceType.Bullet, sourceVesselName, bullet.DisplayName, 0, tntMass * 200, tntMass, tntMass, EMP, blastSoundPath, flashModelPath, shockModelPath, blastModelPath, plumeModelPath, debrisModelPath, "", "");
                    }
                    else
                    {
                        if (HEType != PooledBulletTypes.Slug)
                            ExplosionFx.CreateExplosion(hit.point - (ray.direction * 0.1f), GetExplosivePower(), explModelPath, explSoundPath, ExplosionSourceType.Bullet, caliber, null, sourceVesselName, null, HEType == PooledBulletTypes.Explosive ? default : ray.direction, -1, false, bulletMass, -1, dmgMult, HEType == PooledBulletTypes.Shaped ? "shapedcharge" : "standard", penetratingHit ? hitPart : null, HEType == PooledBulletTypes.Shaped ? apBulletMod : 1f);
                        if (nuclear)
                            NukeFX.CreateExplosion(currPosition, ExplosionSourceType.Bullet, sourceVesselName, bullet.DisplayName, 0, tntMass * 200, tntMass, tntMass, EMP, blastSoundPath, flashModelPath, shockModelPath, blastModelPath, plumeModelPath, debrisModelPath, "", "");
                    }
                    KillBullet();
                    hasDetonated = true;
                    return true;
                }
            }
            return false;
        }

        public void UpdateWidth(Camera c, float resizeFactor)
        {
            if (c == null)
            {
                return;
            }
            if (bulletTrail == null)
            {
                return;
            }
            if (!gameObject.activeInHierarchy)
            {
                return;
            }

            float fov = c.fieldOfView;
            float factor = (fov / 60) * resizeFactor *
                           Mathf.Clamp(Vector3.Distance(transform.position, c.transform.position), 0, 3000) / 50;
            bulletTrail.startWidth = tracerStartWidth * factor * randomWidthScale;
            bulletTrail.endWidth = tracerEndWidth * factor * randomWidthScale;
        }

        public void KillBullet()
        {
            gameObject.SetActive(false);
        }

        public void SetTracerPosition()
        {
            // visual tracer velocity is relative to the observer (which uses srf_vel when below 100km (f*&king KSP!), not orb_vel)
            if (tracerLength == 0)
            {
                linePositions[0] = transform.position + ((currentVelocity - FlightGlobals.ActiveVessel.Velocity()) * tracerDeltaFactor * 0.45f * Time.fixedDeltaTime);
            }
            else
            {
                linePositions[0] = transform.position + ((currentVelocity - FlightGlobals.ActiveVessel.Velocity()).normalized * tracerLength);
            }
            linePositions[1] = transform.position;
            bulletTrail.SetPositions(linePositions);
        }

        void FadeColor()
        {
            Vector4 endColorV = new Vector4(projectileColor.r, projectileColor.g, projectileColor.b, projectileColor.a);
            float delta = TimeWarp.deltaTime;
            Vector4 finalColorV = Vector4.MoveTowards(currentColor, endColorV, delta);
            currentColor = new Color(finalColorV.x, finalColorV.y, finalColorV.z, Mathf.Clamp(finalColorV.w, 0.25f, 1f));
        }

        bool RicochetOnPart(Part p, RaycastHit hit, float angleFromNormal, float impactVel, float fractionOfDistance, float period)
        {
            float hitTolerance = p.crashTolerance;
            //15 degrees should virtually guarantee a ricochet, but 75 degrees should nearly always be fine
            float chance = (((angleFromNormal - 5) / 75) * (hitTolerance / 150)) * 100 / Mathf.Clamp01(impactVel / 600);
            float random = UnityEngine.Random.Range(0f, 100f);
            if (BDArmorySettings.DEBUG_WEAPONS) Debug.Log("[BDArmory.PooledBullet]: Ricochet chance: " + chance);
            if (random < chance)
            {
                DoRicochet(p, hit, angleFromNormal, fractionOfDistance, period);
                return true;
            }
            else
            {
                return false;
            }
        }

        bool RicochetScenery(float hitAngle)
        {
            float reflectRandom = UnityEngine.Random.Range(-75f, 90f);
            if (reflectRandom > 90 - hitAngle && caliber <= 30f)
            {
                return true;
            }

            return false;
        }

        public void DoRicochet(Part p, RaycastHit hit, float hitAngle, float fractionOfDistance, float period)
        {
            //ricochet
            if (BDArmorySettings.BULLET_HITS)
            {
                BulletHitFX.CreateBulletHit(p, hit.point, hit, hit.normal, true, caliber, 0, null);
            }

            tracerStartWidth /= 2;
            tracerEndWidth /= 2;

            MoveBullet(fractionOfDistance * period);
            transform.position = hit.point; // Error in position is around 1e-3.
            currentVelocity = Vector3.Reflect(currentVelocity, hit.normal);
            currentVelocity = (hitAngle / 150) * currentVelocity * 0.65f;

            Vector3 randomDirection = UnityEngine.Random.rotation * Vector3.one;

            currentVelocity = Vector3.RotateTowards(currentVelocity, randomDirection,
                UnityEngine.Random.Range(0f, 5f) * Mathf.Deg2Rad, 0);
            MoveBullet((1f - fractionOfDistance) * period);
        }

        private float GetExplosivePower()
        {
            return tntMass > 0 ? tntMass : blastPower;
        }
    }
}
