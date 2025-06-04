using Seb.Helpers;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using Seb.Fluid.Simulation;

namespace Seb.Fluid.Rendering
{
	public class FluidRenderTest : MonoBehaviour
	{
		public Vector3 extinctionCoefficients;
		public float extinctionMultiplier;
		public float depthParticleSize;
		public float refractionMultiplier;
		public Vector3 testParams;

		[Header("Smoothing Settings")] public BlurType smoothType;
		public BilateralSmooth2D.BilateralFilterSettings bilateralSettings;
		public GaussSmooth.GaussianBlurSettings gaussSmoothSettings;

		public EnvironmentSettings environmentSettings;

		[Header("Debug Settings")] public DisplayMode displayMode;
		public float depthDisplayScale;
		[Header("References")] public Shader renderA;
		public Shader depthShader;
		public Shader smoothThickPrepareShader;
		public FluidSim sim;

		DisplayMode displayModeOld;
		Mesh quadMesh;
		Material matDepth;
		Material matComposite;
		Material smoothPrepareMat;
		ComputeBuffer argsBuffer;

		// Render textures
		RenderTexture compRt;
		RenderTexture depthRt;
		// Command buffers
		CommandBuffer cmd;

		// Smoothing types
		Bilateral1D bilateral1D = new();
		BilateralSmooth2D bilateral2D = new();
		GaussSmooth gaussSmooth = new();

		void Update()
		{
			Init();
			RenderCamSetup();
			BuildCommands();
			UpdateSettings();

			HandleDebugDisplayInput();
		}

		void BuildCommands()
		{
			// ---- Render commands ----
			cmd.Clear();

			// -- Render particles to Depth texture --
			cmd.SetRenderTarget(depthRt);
			cmd.ClearRenderTarget(true, true, Color.white * 10000000, 1);
			cmd.DrawMeshInstancedIndirect(quadMesh, 0, matDepth, 0, argsBuffer);

			// ---- Pack thickness and depth into compRt (depth, thick, thick, depth) ----
			cmd.Blit(null, compRt, smoothPrepareMat);

			// -- Apply smoothing to RG channels of compRt, using A channel as depth source --
			// After smoothing, it will contain (thickness_smooth, thickness, depth)
			ApplyActiveSmoothingType(cmd, compRt, compRt, compRt.descriptor, new Vector3(1, 1, 0));

			// -- Composite final image and draw to screen --
			cmd.Blit(null, BuiltinRenderTextureType.CameraTarget, matComposite);
		}

		void Init()
		{
			if (!quadMesh) quadMesh = QuadGenerator.GenerateQuadMesh();
			ComputeHelper.CreateArgsBuffer(ref argsBuffer, quadMesh, sim.positionBuffer.count);

			InitTextures();
			InitMaterials();

			void InitMaterials()
			{
				if (!matDepth) matDepth = new Material(depthShader);
				if (!smoothPrepareMat) smoothPrepareMat = new Material(smoothThickPrepareShader);
				if (!matComposite) matComposite = new Material(renderA);
			}

			void InitTextures()
			{
				// Display size
				int width = Screen.width;
				int height = Screen.height;

				GraphicsFormat fmtRGBA = GraphicsFormat.R32G32B32A32_SFloat;
				GraphicsFormat fmtR = GraphicsFormat.R32_SFloat;
				ComputeHelper.CreateRenderTexture(ref depthRt, width, height, FilterMode.Bilinear, fmtR, depthMode: DepthMode.Depth16);
				ComputeHelper.CreateRenderTexture(ref compRt, width, height, FilterMode.Bilinear, fmtRGBA, depthMode: DepthMode.None);
			}
		}


		void ApplyActiveSmoothingType(CommandBuffer cmd, RenderTargetIdentifier src, RenderTargetIdentifier target, RenderTextureDescriptor desc, Vector3 smoothMask)
		{
			if (smoothType == BlurType.Bilateral1D)
			{
				bilateral1D.Smooth(cmd, src, target, desc, bilateralSettings, smoothMask);
			}
			else if (smoothType == BlurType.Bilateral2D)
			{
				bilateral2D.Smooth(cmd, src, target, desc, bilateralSettings, smoothMask);
			}
			else if (smoothType == BlurType.Gaussian)
			{
				gaussSmooth.Smooth(cmd, src, target, desc, gaussSmoothSettings, smoothMask);
			}
		}

		float FrameBoundsOrtho(Vector3 boundsSize, Matrix4x4 worldToView)
		{
			Vector3 halfSize = boundsSize * 0.5f;
			float maxX = 0;
			float maxY = 0;

			for (int i = 0; i < 8; i++)
			{
				Vector3 corner = new Vector3(
					(i & 1) == 0 ? -halfSize.x : halfSize.x,
					(i & 2) == 0 ? -halfSize.y : halfSize.y,
					(i & 4) == 0 ? -halfSize.z : halfSize.z
				);

				Vector3 viewCorner = worldToView.MultiplyPoint(corner);
				maxX = Mathf.Max(maxX, Mathf.Abs(viewCorner.x));
				maxY = Mathf.Max(maxY, Mathf.Abs(viewCorner.y));
			}

			float aspect = Screen.height / (float)Screen.width;
			float targetOrtho = Mathf.Max(maxY, maxX * aspect);
			return targetOrtho;
		}

		void RenderCamSetup()
		{
			if (cmd == null)
			{
				cmd = new();
				cmd.name = "Fluid Render Commands";
			}

			Camera.main.RemoveAllCommandBuffers();
			Camera.main.AddCommandBuffer(CameraEvent.AfterEverything, cmd);
			Camera.main.depthTextureMode = DepthTextureMode.Depth;
		}

		void UpdateSettings()
		{
			// ---- Smooth prepare ----
			smoothPrepareMat.SetTexture("Depth", depthRt);
			
			// ---- Depth ----
			matDepth.SetBuffer("Positions", sim.positionBuffer);
			matDepth.SetFloat("scale", depthParticleSize);

			// ---- Composite mat settings ----
			matComposite.SetInt("debugDisplayMode", (int)displayMode);
			matComposite.SetTexture("Comp", compRt);
			
			matComposite.SetVector("testParams", testParams);
			matComposite.SetVector("extinctionCoefficients", extinctionCoefficients * extinctionMultiplier);
			matComposite.SetVector("boundsSize", sim.Scale);
			matComposite.SetFloat("refractionMultiplier", refractionMultiplier);

			matComposite.SetFloat("depthDisplayScale", depthDisplayScale);
			
			// Environment
			Vector3 floorSize = new Vector3(30, 0.05f, 30);
			float floorHeight = -sim.Scale.y / 2 + sim.transform.position.y - floorSize.y / 2;
			matComposite.SetVector("floorPos", new Vector3(0, floorHeight, 0));
			matComposite.SetVector("floorSize", floorSize);
			matComposite.SetColor("tileCol1", environmentSettings.tileCol1);
			matComposite.SetColor("tileCol2", environmentSettings.tileCol2);
			matComposite.SetColor("tileCol3", environmentSettings.tileCol3);
			matComposite.SetColor("tileCol4", environmentSettings.tileCol4);
			matComposite.SetVector("tileColVariation", environmentSettings.tileColVariation);
			matComposite.SetFloat("tileScale", environmentSettings.tileScale);
			matComposite.SetFloat("tileDarkOffset", environmentSettings.tileDarkOffset);
		}

		void HandleDebugDisplayInput()
		{
			// -- Set display mode with num keys --
			for (int i = 0; i <= 9; i++)
			{
				if (Input.GetKeyDown(KeyCode.Alpha0 + i))
				{
					displayMode = (DisplayMode)i;
					Debug.Log("Set display mode: " + displayMode);
				}
			}
		}

		[System.Serializable]
		public struct EnvironmentSettings
		{
			public Color tileCol1;
			public Color tileCol2;
			public Color tileCol3;
			public Color tileCol4;
			public Vector3 tileColVariation;
			public float tileScale;
			public float tileDarkOffset;
		}

		public enum DisplayMode
		{
			Composite,
			Depth,
			SmoothDepth,
		}

		public enum BlurType
		{
			Gaussian,
			Bilateral2D,
			Bilateral1D
		}


		void OnDestroy()
		{
			ComputeHelper.Release(argsBuffer);
			ComputeHelper.Release(depthRt, compRt);
		}
	}
}