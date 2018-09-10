﻿#define HDRP_ENABLED

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
#if HDRP_ENABLED
using UnityEngine.Experimental.Rendering.HDPipeline;
#endif

[ExecuteInEditMode]
#if HDRP_ENABLED
[HDRPCallback]
#endif
public partial class OcclusionProbes : MonoBehaviour
{
    public static OcclusionProbes Instance { get; private set; }

    [Header("Baked Results")]
    [Tooltip("Debug output in DebugMenu/Material/Material/Lit/SkyOcclusion")]
    public OcclusionProbeData m_Data;
    [Tooltip("Stores direct sky contribution. Baked with the button below or during lightmap bake. If not set, a probe generated from the current skybox will be used.")]
    public AmbientProbeData m_AmbientProbeData;

    static Vector4[] ms_AmbientProbeSC;
    static Texture3D ms_White;

    static class Uniforms
    {
        internal static readonly int _AmbientProbeSH = Shader.PropertyToID("_AmbientProbeSH");
        internal static readonly int _OcclusionProbes = Shader.PropertyToID("_OcclusionProbes");
        internal static readonly int _OcclusionProbesWorldToLocal = Shader.PropertyToID("_OcclusionProbesWorldToLocal");
        internal static readonly int _OcclusionProbesDetail = Shader.PropertyToID("_OcclusionProbesDetail");
        internal static readonly int _OcclusionProbesWorldToLocalDetail = Shader.PropertyToID("_OcclusionProbesWorldToLocalDetail");
        internal static readonly int _OcclusionProbesReflectionOcclusionAmount = Shader.PropertyToID("_OcclusionProbesReflectionOcclusionAmount");
    }

    void OnEnable()
    {
#if UNITY_EDITOR
        AddLightmapperCallbacks();
#endif

        if(Application.isPlaying)
            Debug.Assert(Instance == null);

        Instance = this;

#if !HDRP_ENABLED //legacy
        Camera.onPreRender += this.CameraPreRender;
#endif
    }

    void OnDisable()
    {
#if UNITY_EDITOR
        RemoveLightmapperCallbacks();
#endif

        if(Application.isPlaying)
            Debug.Assert(Instance == this);

        if(Instance == this) {
            Instance = null;
        }
    }

    void OnDestroy()
    {
        if(Application.isPlaying)
            Destroy(ms_White);
        else
            DestroyImmediate(ms_White);

        ms_White = null;
    }

#if HDRP_ENABLED
    [HDRPCallbackMethod]
	static void OcclusionProbesSetup()
    {
		HDRenderPipeline.OnPrepareCamera += SetupGPUData;
	}

    static public void SetupGPUData(ScriptableRenderContext renderContext, HDCamera hdCamera, CommandBuffer cmd)
    {
        if (Instance)
        {
            Instance.SetShaderUniforms(cmd, hdCamera.camera);
            return;
        }

        SetAmbientProbeShaderUniforms(cmd, null);
        SetDisabledOcclusionShaderUniforms(cmd);
    }
#else
    private void CameraPreRender(Camera camera)
    {
        if (Instance)
        {
            Instance.SetShaderUniforms(null, camera);
            return;
        }
        SetAmbientProbeShaderUniforms(null, null);
        SetDisabledOcclusionShaderUniforms(null);
    }
#endif

    // This function should soon make it into the LightProbes Unity api
    private static void GetShaderConstantsFromNormalizedSH(ref SphericalHarmonicsL2 ambientProbe, Vector4[] outCoefficients)
    {
        for (int channelIdx = 0; channelIdx < 3; ++channelIdx)
        {
            // Constant + Linear
            // In the shader we multiply the normal is not swizzled, so it's normal.xyz.
            // Swizzle the coefficients to be in { x, y, z, DC } order.
            outCoefficients[channelIdx].x = ambientProbe[channelIdx, 3];
            outCoefficients[channelIdx].y = ambientProbe[channelIdx, 1];
            outCoefficients[channelIdx].z = ambientProbe[channelIdx, 2];
            outCoefficients[channelIdx].w = ambientProbe[channelIdx, 0] - ambientProbe[channelIdx, 6];
            // Quadratic polynomials
            outCoefficients[channelIdx + 3].x = ambientProbe[channelIdx, 4];
            outCoefficients[channelIdx + 3].y = ambientProbe[channelIdx, 5];
            outCoefficients[channelIdx + 3].z = ambientProbe[channelIdx, 6] * 3.0f;
            outCoefficients[channelIdx + 3].w = ambientProbe[channelIdx, 7];
        }
        // Final quadratic polynomial
        outCoefficients[6].x = ambientProbe[0, 8];
        outCoefficients[6].y = ambientProbe[1, 8];
        outCoefficients[6].z = ambientProbe[2, 8];
        outCoefficients[6].w = 1.0f;
    }

    void SetShaderUniforms(CommandBuffer cmd, Camera camera)
    {
        SetAmbientProbeShaderUniforms(cmd, m_AmbientProbeData);

        if (!m_Data)
        {
            SetDisabledOcclusionShaderUniforms(cmd);
            return;
        }

#if HDRP_ENABLED
        // Sky occlusion
        cmd.SetGlobalTexture(Uniforms._OcclusionProbes, m_Data.occlusion);
        // TODO: no full matrix mul needed, just scale and offset the pos (don't really need to support rotation)
        cmd.SetGlobalMatrix(Uniforms._OcclusionProbesWorldToLocal, m_Data.worldToLocal);
#else
        Shader.SetGlobalTexture(Uniforms._OcclusionProbes, m_Data.occlusion);
        Shader.SetGlobalMatrix(Uniforms._OcclusionProbesWorldToLocal, m_Data.worldToLocal);
#endif


        // Detail sky occlusion - for the probe set the camera is in
        InitWhiteTexture();
        Texture3D occlusionDetail = ms_White;
        Matrix4x4 worldToLocalDetail = Matrix4x4.identity;
        worldToLocalDetail[1, 3] = 1000.0f; // move out of the way

        if (m_Data.occlusionDetail != null)
        {
            Vector3 cameraPos = camera.transform.position;
            int detailSetCount = m_Data.worldToLocalDetail.Length;
            
            for (int i = 0; i < detailSetCount; i++)
            {
                if (IsInside(cameraPos, m_Data.worldToLocalDetail[i]))
                {
                    occlusionDetail = m_Data.occlusionDetail[i];
                    worldToLocalDetail = m_Data.worldToLocalDetail[i];
                    break;
                }
            }
        }

#if HDRP_ENABLED
        cmd.SetGlobalTexture(Uniforms._OcclusionProbesDetail, occlusionDetail);
        cmd.SetGlobalMatrix(Uniforms._OcclusionProbesWorldToLocalDetail, worldToLocalDetail);
        cmd.SetGlobalFloat(Uniforms._OcclusionProbesReflectionOcclusionAmount, m_Data.reflectionOcclusionAmount);
#else
        Shader.SetGlobalTexture(Uniforms._OcclusionProbesDetail, occlusionDetail);
        Shader.SetGlobalMatrix(Uniforms._OcclusionProbesWorldToLocalDetail, worldToLocalDetail);
        Shader.SetGlobalFloat(Uniforms._OcclusionProbesReflectionOcclusionAmount, m_Data.reflectionOcclusionAmount);
#endif
    }

    static void SetAmbientProbeShaderUniforms(CommandBuffer cmd, AmbientProbeData ambientProbeData)
    {
        // Ambient probe, i.e. direct sky contribution
        if (ms_AmbientProbeSC == null || ms_AmbientProbeSC.Length != 7)
            ms_AmbientProbeSC = new Vector4[7];
        
        if (ambientProbeData != null)
        {
#if HDRP_ENABLED
            cmd.SetGlobalVectorArray(Uniforms._AmbientProbeSH, ambientProbeData.sh);
#else
            Shader.SetGlobalVectorArray(Uniforms._AmbientProbeSH, ambientProbeData.sh);
#endif
        }
        else
        {
            SphericalHarmonicsL2 ambientProbe =  RenderSettings.ambientProbe;
            // LightProbes.GetShaderConstantsFromNormalizedSH(ref ambientProbe, m_AmbientProbeSC);
            GetShaderConstantsFromNormalizedSH(ref ambientProbe, ms_AmbientProbeSC);
#if HDRP_ENABLED
            cmd.SetGlobalVectorArray(Uniforms._AmbientProbeSH, ms_AmbientProbeSC);
#else
            Shader.SetGlobalVectorArray(Uniforms._AmbientProbeSH, ms_AmbientProbeSC);
#endif
        }
    }

    static bool IsInside(Vector3 worldPos, Matrix4x4 worldToLocal)
    {
        var pos = worldToLocal.MultiplyPoint3x4(worldPos);
        return pos.x > 0 && pos.x < 1 && pos.y > 0 && pos.y < 1 && pos.z > 0 && pos.z < 1;
    }

    static void InitWhiteTexture()
    {
        if (ms_White != null)
            return;
        
        ms_White = new Texture3D(1, 1, 1, TextureFormat.Alpha8, false);
        ms_White.hideFlags = HideFlags.DontSave;
        ms_White.SetPixels32(new Color32[]{new Color32(255, 255, 255, 255)});
        ms_White.Apply();
    }

    static void SetDisabledOcclusionShaderUniforms(CommandBuffer cmd)
    {
        InitWhiteTexture();

        // To avoid extra variants in the shader, set up a white texture when probes are disabled.
#if HDRP_ENABLED
        cmd.SetGlobalTexture(Uniforms._OcclusionProbes, ms_White);
        cmd.SetGlobalMatrix(Uniforms._OcclusionProbesWorldToLocal, Matrix4x4.identity);
        cmd.SetGlobalTexture(Uniforms._OcclusionProbesDetail, ms_White);
        cmd.SetGlobalMatrix(Uniforms._OcclusionProbesWorldToLocalDetail, Matrix4x4.identity);
#else        
        Shader.SetGlobalTexture(Uniforms._OcclusionProbes, ms_White);
        Shader.SetGlobalMatrix(Uniforms._OcclusionProbesWorldToLocal, Matrix4x4.identity);
        Shader.SetGlobalTexture(Uniforms._OcclusionProbesDetail, ms_White);
        Shader.SetGlobalMatrix(Uniforms._OcclusionProbesWorldToLocalDetail, Matrix4x4.identity);
#endif
    }
}
