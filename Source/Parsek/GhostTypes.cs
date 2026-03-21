using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    internal class JettisonGhostInfo
    {
        public uint partPersistentId;
        public List<Transform> jettisonTransforms;
    }

    internal class ParachuteGhostInfo
    {
        public uint partPersistentId;
        public Transform canopyTransform;
        public Transform capTransform;
        public Vector3 deployedCanopyScale;
        public Vector3 deployedCanopyPos;
        public Quaternion deployedCanopyRot;
        public Vector3 semiDeployedCanopyScale;
        public Vector3 semiDeployedCanopyPos;
        public Quaternion semiDeployedCanopyRot;
        public bool semiDeployedSampled;
    }

    internal class EngineGhostInfo
    {
        public uint partPersistentId;
        public int moduleIndex;
        public List<ParticleSystem> particleSystems = new List<ParticleSystem>();
        public FloatCurve emissionCurve;
        public FloatCurve speedCurve;
    }

    internal struct DeployableTransformState
    {
        public Transform t;
        public Vector3 stowedPos;
        public Quaternion stowedRot;
        public Vector3 stowedScale;
        public Vector3 deployedPos;
        public Quaternion deployedRot;
        public Vector3 deployedScale;
    }

    internal class DeployableGhostInfo
    {
        public uint partPersistentId;
        public List<DeployableTransformState> transforms;
    }

    internal struct HeatTransformState
    {
        public Transform t;
        public Vector3 coldPos, mediumPos, hotPos;
        public Quaternion coldRot, mediumRot, hotRot;
        public Vector3 coldScale, mediumScale, hotScale;
    }

    internal struct HeatMaterialState
    {
        public Material material;
        public string colorProperty;
        public Color coldColor;
        public Color mediumColor;
        public Color hotColor;
        public string emissiveProperty;
        public Color coldEmission;
        public Color mediumEmission;
        public Color hotEmission;
    }

    internal class HeatGhostInfo
    {
        public uint partPersistentId;
        public List<HeatTransformState> transforms;
        public List<HeatMaterialState> materialStates;
    }

    internal class LightGhostInfo
    {
        public uint partPersistentId;
        public List<Light> lights;
    }

    internal struct ColorChangerMaterialState
    {
        public Material material;
        public Color offColor;
        public Color onColor;
    }

    internal class ColorChangerGhostInfo
    {
        public uint partPersistentId;
        public string shaderProperty;      // "_EmissiveColor" or "_BurnColor"
        public bool isCabinLight;          // true = Pattern A (toggle), false = Pattern B (reentry)
        // Pattern B: highest char fraction reached (permanent, never decreases).
        // NOTE: Rewind past reentry won't reset this value — a reset mechanism
        // will be needed when rewind/scrub support is added.
        public float peakCharIntensity;
        public List<ColorChangerMaterialState> materials;
    }

    internal class RcsGhostInfo
    {
        public uint partPersistentId;
        public int moduleIndex;
        public List<ParticleSystem> particleSystems = new List<ParticleSystem>();
        public FloatCurve emissionCurve;
        public FloatCurve speedCurve;
        public float emissionScale = 1f;
        public float speedScale = 1f;
    }

    internal enum RoboticVisualMode
    {
        Rotational,
        Linear,
        RotorRpm
    }

    internal class RoboticGhostInfo
    {
        public uint partPersistentId;
        public int moduleIndex;
        public string moduleName;
        public Transform servoTransform;
        public Vector3 axisLocal = Vector3.up;
        public Vector3 stowedPos;
        public Quaternion stowedRot;
        public RoboticVisualMode visualMode;
        public float currentValue;
        public bool active;
        public double lastUpdateUT = double.NaN;
    }

    internal struct FxModelDefinition
    {
        public string transformName;
        public string modelName;
        public Vector3 localOffset;
        public Quaternion localRotation;
        public Vector3 localScale;
    }

    internal class FairingGhostInfo
    {
        public uint partPersistentId;
        public GameObject fairingMeshObject;
        public List<GameObject> internalStructureObjects; // truss/cap clones, initially hidden
        public bool showInternalOnJettison;               // from ModuleStructuralNodeToggle.showMesh
    }

    internal struct FireShellMesh
    {
        public Mesh mesh;
        public Transform transform;
    }

    internal class ReentryFxInfo
    {
        public ParticleSystem fireParticles;
        public Mesh combinedEmissionMesh; // combined ghost meshes for surface emission, needs Destroy
        public Texture2D generatedTexture; // runtime soft-circle, needs Destroy on cleanup
        public List<FireShellMesh> fireShellMeshes; // mesh+transform pairs for DrawMesh flame overlay
        public Material fireShellMaterial; // additive material for flame shell passes
        public List<HeatMaterialState> glowMaterials = new List<HeatMaterialState>();
        public List<Material> allClonedMaterials = new List<Material>();
        public float lastIntensity;
        public float vesselLength;
    }

    internal enum VariantPropertyType { Texture, Color, Float, Skip }

    internal struct VariantTextureRule
    {
        public string materialName;
        public string shaderName;
        public string transformName;
        public List<(string key, string value)> properties;
    }

    /// <summary>
    /// Bundles all output from BuildTimelineGhostFromSnapshot: the root GameObject
    /// plus per-module-type ghost info lists. Replaces the previous 10 out-parameters.
    /// Null list fields mean that module type had no matching parts in the snapshot.
    /// </summary>
    internal class GhostBuildResult
    {
        public GameObject root;
        public List<ParachuteGhostInfo> parachuteInfos;
        public List<JettisonGhostInfo> jettisonInfos;
        public List<EngineGhostInfo> engineInfos;
        public List<DeployableGhostInfo> deployableInfos;
        public List<HeatGhostInfo> heatInfos;
        public List<LightGhostInfo> lightInfos;
        public List<FairingGhostInfo> fairingInfos;
        public List<RcsGhostInfo> rcsInfos;
        public List<RoboticGhostInfo> roboticInfos;
        public List<ColorChangerGhostInfo> colorChangerInfos;
    }
}
