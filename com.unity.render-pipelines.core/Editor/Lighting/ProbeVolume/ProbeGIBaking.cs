#if UNITY_EDITOR

using System.Collections.Generic;
using Unity.Collections;
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;

using Brick = UnityEngine.Experimental.Rendering.ProbeBrickIndex.Brick;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;

namespace UnityEngine.Experimental.Rendering
{
    struct BakingCell
    {
        public ProbeReferenceVolume.Cell cell;
        public int[] probeIndices;
    }

    class BakingBatch
    {
        public int index;
        public Dictionary<int, List<Scene>> cellIndex2SceneReferences = new Dictionary<int, List<Scene>>();
        public List<BakingCell> cells = new List<BakingCell>();
        public Dictionary<Vector3, int> uniquePositions = new Dictionary<Vector3, int>();

        private BakingBatch() {}

        public BakingBatch(int index)
        {
            this.index = index;
        }

        public void Clear()
        {
            UnityEditor.Experimental.Lightmapping.SetAdditionalBakedProbes(index, null);
            cells.Clear();
            cellIndex2SceneReferences.Clear();
        }

        public int uniqueProbeCount => uniquePositions.Keys.Count;
    }

    [InitializeOnLoad]
    partial class ProbeGIBaking
    {
        static bool m_IsInit = false;
        static BakingBatch m_BakingBatch;
        static ProbeReferenceVolumeAuthoring m_BakingReferenceVolumeAuthoring = null;
        static int m_BakingBatchIndex = 0;

        static Bounds globalBounds = new Bounds();
        static bool hasFoundBounds = false;

        static bool onAdditionalProbesBakeCompletedCalled = false;

        static ProbeGIBaking()
        {
            Init();
        }

        public static void Init()
        {
            if (!m_IsInit)
            {
                m_IsInit = true;
                Lightmapping.lightingDataCleared += OnLightingDataCleared;
                Lightmapping.bakeStarted += OnBakeStarted;
            }
        }

        static public void Clear()
        {
            var refVolAuthList = GameObject.FindObjectsOfType<ProbeReferenceVolumeAuthoring>();

            foreach (var refVolAuthoring in refVolAuthList)
            {
                if (!refVolAuthoring.enabled || !refVolAuthoring.gameObject.activeSelf)
                    continue;

                refVolAuthoring.volumeAsset = null;

                var refVol = ProbeReferenceVolume.instance;
                refVol.Clear();
                refVol.SetTRS(Vector3.zero, Quaternion.identity, refVolAuthoring.brickSize);
                refVol.SetMaxSubdivision(refVolAuthoring.maxSubdivision);
            }

            var probeVolumes = GameObject.FindObjectsOfType<ProbeVolume>();
            foreach (var probeVolume in probeVolumes)
            {
                probeVolume.OnLightingDataAssetCleared();
            }


            if (m_BakingBatch != null)
                m_BakingBatch.Clear();

            m_BakingBatchIndex = 0;
        }

        public static void FindWorldBounds()
        {
            ProbeReferenceVolume.instance.clearAssetsOnVolumeClear = true;


            var sceneBounds = ProbeReferenceVolume.instance.sceneBounds;
            HashSet<string> scenesToConsider = new HashSet<string>();

            for (int i = 0; i < EditorSceneManager.sceneCount; ++i)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                sceneBounds.UpdateSceneBounds(scene);
                // !!! IMPORTANT TODO !!!
                // When we will have the concept of baking set this should be reverted, if a scene is not in the bake set it should not be considered
                // As of now we include all open scenes as the workflow is not nice or clear. When it'll be we should *NOT* do it.
                scenesToConsider.Add(scene.path);
            }


            foreach (var scene in EditorBuildSettings.scenes)
            {
                scenesToConsider.Add(scene.path);
            }


            List<Scene> openedScenes = new List<Scene>();
            hasFoundBounds = false;

            foreach (var scenePath in scenesToConsider)
            {
                bool hasProbeVolumes = false;
                if (sceneBounds.hasProbeVolumes.TryGetValue(scenePath, out hasProbeVolumes))
                {
                    if (hasProbeVolumes)
                    {
                        Bounds localBound;
                        if (sceneBounds.sceneBounds.TryGetValue(scenePath, out localBound))
                        {
                            if (hasFoundBounds)
                            {
                                globalBounds.Encapsulate(localBound);
                            }
                            else
                            {
                                globalBounds = localBound;
                                hasFoundBounds = true;
                            }
                        }
                    }
                }
                else // we need to open the scene to test.
                {
                    var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                    openedScenes.Add(scene);
                    sceneBounds.UpdateSceneBounds(scene);
                    Bounds localBound = sceneBounds.sceneBounds[scene.path];
                    if (hasFoundBounds)
                        globalBounds.Encapsulate(localBound);
                    else
                        globalBounds = localBound;
                }
            }

            if (openedScenes.Count > 0)
            {
                foreach (var scene in openedScenes)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        static ProbeReferenceVolumeAuthoring GetCardinalAuthoringComponent(ProbeReferenceVolumeAuthoring[] refVolAuthList)
        {
            List<ProbeReferenceVolumeAuthoring> enabledVolumes = new List<ProbeReferenceVolumeAuthoring>();

            foreach (var refVolAuthoring in refVolAuthList)
            {
                if (!refVolAuthoring.enabled || !refVolAuthoring.gameObject.activeSelf)
                    continue;

                enabledVolumes.Add(refVolAuthoring);
            }

            int numVols = enabledVolumes.Count;

            if (numVols == 0)
                return null;

            if (numVols == 1)
                return enabledVolumes[0];

            var reference = enabledVolumes[0];
            for (int c = 1; c < numVols; ++c)
            {
                var compare = enabledVolumes[c];
                if (!reference.profile.IsEquivalent(compare.profile))
                    return null;
            }

            return reference;
        }

        static void OnBakeStarted()
        {
            if (!ProbeReferenceVolume.instance.isInitialized) return;

            var refVolAuthList = GameObject.FindObjectsOfType<ProbeReferenceVolumeAuthoring>();
            if (refVolAuthList.Length == 0)
                return;

            FindWorldBounds();
            refVolAuthList = GameObject.FindObjectsOfType<ProbeReferenceVolumeAuthoring>();

            m_BakingReferenceVolumeAuthoring = GetCardinalAuthoringComponent(refVolAuthList);

            if (m_BakingReferenceVolumeAuthoring == null)
            {
                Debug.Log("Scene(s) have multiple inconsistent ProbeReferenceVolumeAuthoring components. Please ensure they use identical profiles and transforms before baking.");
                return;
            }


            RunPlacement();
        }

        static void CellCountInDirections(out Vector3Int cellsInXYZ, float cellSizeInMeters)
        {
            cellsInXYZ = Vector3Int.zero;

            Vector3 center = Vector3.zero;
            var centeredMin = globalBounds.min - center;
            var centeredMax = globalBounds.max - center;

            cellsInXYZ.x = Mathf.Max(Mathf.CeilToInt(Mathf.Abs(centeredMin.x / cellSizeInMeters)), Mathf.CeilToInt(Mathf.Abs(centeredMax.x / cellSizeInMeters))) * 2;
            cellsInXYZ.y = Mathf.Max(Mathf.CeilToInt(Mathf.Abs(centeredMin.y / cellSizeInMeters)), Mathf.CeilToInt(Mathf.Abs(centeredMax.y / cellSizeInMeters))) * 2;
            cellsInXYZ.z = Mathf.Max(Mathf.CeilToInt(Mathf.Abs(centeredMin.z / cellSizeInMeters)), Mathf.CeilToInt(Mathf.Abs(centeredMax.z / cellSizeInMeters))) * 2;
        }

        static void OnAdditionalProbesBakeCompleted()
        {
            UnityEditor.Experimental.Lightmapping.additionalBakedProbesCompleted -= OnAdditionalProbesBakeCompleted;
            UnityEngine.Profiling.Profiler.BeginSample("OnAdditionalProbesBakeCompleted");
            var bakingCells = m_BakingBatch.cells;
            var numCells = bakingCells.Count;

            int numUniqueProbes = m_BakingBatch.uniqueProbeCount;

            var sh = new NativeArray<SphericalHarmonicsL2>(numUniqueProbes, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var validity = new NativeArray<float>(numUniqueProbes, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var bakedProbeOctahedralDepth = new NativeArray<float>(numUniqueProbes * 64, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            bool validBakedProbes = UnityEditor.Experimental.Lightmapping.GetAdditionalBakedProbes(m_BakingBatch.index, sh, validity, bakedProbeOctahedralDepth);

            if (!validBakedProbes)
            {
                Debug.LogError("Lightmapper failed to produce valid probe data.  Please consider clearing lighting data and rebake.");
                return;
            }

            onAdditionalProbesBakeCompletedCalled = true;

            var dilationSettings = m_BakingReferenceVolumeAuthoring.GetDilationSettings();
            // Fetch results of all cells
            for (int c = 0; c < numCells; ++c)
            {
                var cell = bakingCells[c].cell;

                if (cell.probePositions == null)
                    continue;

                int numProbes = cell.probePositions.Length;
                Debug.Assert(numProbes > 0);

                cell.sh = new SphericalHarmonicsL2[numProbes];
                cell.validity = new float[numProbes];

                for (int i = 0; i < numProbes; ++i)
                {
                    int j = bakingCells[c].probeIndices[i];
                    SphericalHarmonicsL2 shv = sh[j];

                    // Compress the range of all coefficients but the DC component to [0..1]
                    // Upper bounds taken from http://ppsloan.org/publications/Sig20_Advances.pptx
                    // Divide each coefficient by DC*f to get to [-1,1] where f is from slide 33
                    for (int rgb = 0; rgb < 3; ++rgb)
                    {
                        var l0 = sh[j][rgb, 0];

                        if (l0 == 0.0f)
                            continue;

                        if (dilationSettings.dilationDistance > 0.0f && validity[j] > dilationSettings.dilationValidityThreshold)
                        {
                            for (int k = 0; k < 9; ++k)
                            {
                                shv[rgb, k] = 0.0f;
                            }
                        }
                        else
                        {
                            // TODO: We're working on irradiance instead of radiance coefficients
                            //       Add safety margin 2 to avoid out-of-bounds values
                            float l1scale = 2.0f; // Should be: 3/(2*sqrt(3)) * 2, but rounding to 2 to issues we are observing.
                            float l2scale = 3.5777088f; // 4/sqrt(5) * 2

                            // L_1^m
                            shv[rgb, 1] = sh[j][rgb, 1] / (l0 * l1scale * 2.0f) + 0.5f;
                            shv[rgb, 2] = sh[j][rgb, 2] / (l0 * l1scale * 2.0f) + 0.5f;
                            shv[rgb, 3] = sh[j][rgb, 3] / (l0 * l1scale * 2.0f) + 0.5f;

                            // L_2^-2
                            shv[rgb, 4] = sh[j][rgb, 4] / (l0 * l2scale * 2.0f) + 0.5f;
                            shv[rgb, 5] = sh[j][rgb, 5] / (l0 * l2scale * 2.0f) + 0.5f;
                            shv[rgb, 6] = sh[j][rgb, 6] / (l0 * l2scale * 2.0f) + 0.5f;
                            shv[rgb, 7] = sh[j][rgb, 7] / (l0 * l2scale * 2.0f) + 0.5f;
                            shv[rgb, 8] = sh[j][rgb, 8] / (l0 * l2scale * 2.0f) + 0.5f;

                            for (int coeff = 1; coeff < 9; ++coeff)
                                Debug.Assert(shv[rgb, coeff] >= 0.0f && shv[rgb, coeff] <= 1.0f);
                        }
                    }

                    SphericalHarmonicsL2Utils.SetL0(ref cell.sh[i], new Vector3(shv[0, 0], shv[1, 0], shv[2, 0]));
                    SphericalHarmonicsL2Utils.SetL1R(ref cell.sh[i], new Vector3(shv[0, 3], shv[0, 1], shv[0, 2]));
                    SphericalHarmonicsL2Utils.SetL1G(ref cell.sh[i], new Vector3(shv[1, 3], shv[1, 1], shv[1, 2]));
                    SphericalHarmonicsL2Utils.SetL1B(ref cell.sh[i], new Vector3(shv[2, 3], shv[2, 1], shv[2, 2]));

                    SphericalHarmonicsL2Utils.SetCoefficient(ref cell.sh[i], 4, new Vector3(shv[0, 4], shv[1, 4], shv[2, 4]));
                    SphericalHarmonicsL2Utils.SetCoefficient(ref cell.sh[i], 5, new Vector3(shv[0, 5], shv[1, 5], shv[2, 5]));
                    SphericalHarmonicsL2Utils.SetCoefficient(ref cell.sh[i], 6, new Vector3(shv[0, 6], shv[1, 6], shv[2, 6]));
                    SphericalHarmonicsL2Utils.SetCoefficient(ref cell.sh[i], 7, new Vector3(shv[0, 7], shv[1, 7], shv[2, 7]));
                    SphericalHarmonicsL2Utils.SetCoefficient(ref cell.sh[i], 8, new Vector3(shv[0, 8], shv[1, 8], shv[2, 8]));

                    cell.validity[i] = validity[j];
                }

                ProbeReferenceVolume.instance.cells[cell.index] = cell;
                UnityEngine.Profiling.Profiler.EndSample();
            }

            m_BakingBatchIndex = 0;

            // Reset index
            UnityEditor.Experimental.Lightmapping.SetAdditionalBakedProbes(m_BakingBatch.index, null);

            // Map from each scene to an existing reference volume
            var scene2RefVol = new Dictionary<Scene, ProbeReferenceVolumeAuthoring>();
            foreach (var refVol in GameObject.FindObjectsOfType<ProbeReferenceVolumeAuthoring>())
                if (refVol.enabled)
                    scene2RefVol[refVol.gameObject.scene] = refVol;

            // Map from each reference volume to its asset
            var refVol2Asset = new Dictionary<ProbeReferenceVolumeAuthoring, ProbeVolumeAsset>();
            foreach (var refVol in scene2RefVol.Values)
            {
                refVol2Asset[refVol] = ProbeVolumeAsset.CreateAsset(refVol.gameObject.scene);
            }

            // Put cells into the respective assets
            foreach (var cell in ProbeReferenceVolume.instance.cells.Values)
            {
                foreach (var scene in m_BakingBatch.cellIndex2SceneReferences[cell.index])
                {
                    // This scene has a reference volume authoring component in it?
                    ProbeReferenceVolumeAuthoring refVol = null;
                    if (scene2RefVol.TryGetValue(scene, out refVol))
                    {
                        var asset = refVol2Asset[refVol];
                        asset.cells.Add(cell);
                        if (hasFoundBounds)
                        {
                            Vector3Int cellsInDir;
                            float cellSizeInMeters = Mathf.CeilToInt(refVol.profile.cellSizeInMeters);
                            CellCountInDirections(out cellsInDir, cellSizeInMeters);

                            asset.maxCellIndex.x = cellsInDir.x * (int)refVol.profile.cellSizeInBricks;
                            asset.maxCellIndex.y = cellsInDir.y * (int)refVol.profile.cellSizeInBricks;
                            asset.maxCellIndex.z = cellsInDir.z * (int)refVol.profile.cellSizeInBricks;
                        }
                        else
                        {
                            foreach (var p in cell.probePositions)
                            {
                                float x = Mathf.Abs((float)p.x + refVol.transform.position.x) / refVol.profile.minBrickSize;
                                float y = Mathf.Abs((float)p.y + refVol.transform.position.y) / refVol.profile.minBrickSize;
                                float z = Mathf.Abs((float)p.z + refVol.transform.position.z) / refVol.profile.minBrickSize;
                                asset.maxCellIndex.x = Mathf.Max(asset.maxCellIndex.x, Mathf.CeilToInt(x * 2));
                                asset.maxCellIndex.y = Mathf.Max(asset.maxCellIndex.y, Mathf.CeilToInt(y * 2));
                                asset.maxCellIndex.z = Mathf.Max(asset.maxCellIndex.z, Mathf.CeilToInt(z * 2));
                            }
                        }
                    }
                }
            }

            // Connect the assets to their components
            foreach (var pair in refVol2Asset)
            {
                var refVol = pair.Key;
                var asset = pair.Value;

                refVol.volumeAsset = asset;

                if (UnityEditor.Lightmapping.giWorkflowMode != UnityEditor.Lightmapping.GIWorkflowMode.Iterative)
                {
                    UnityEditor.EditorUtility.SetDirty(refVol);
                    UnityEditor.EditorUtility.SetDirty(refVol.volumeAsset);
                }
            }

            var probeVolumes = GameObject.FindObjectsOfType<ProbeVolume>();
            foreach (var probeVolume in probeVolumes)
            {
                probeVolume.OnBakeCompleted();
            }

            UnityEditor.AssetDatabase.SaveAssets();
            UnityEditor.AssetDatabase.Refresh();
            ProbeReferenceVolume.instance.clearAssetsOnVolumeClear = false;

            foreach (var refVol in refVol2Asset.Keys)
            {
                if (refVol.enabled && refVol.gameObject.activeSelf)
                    refVol.QueueAssetLoading();
            }

            // ---- Perform dilation ---
            if (dilationSettings.dilationDistance > 0.0f)
            {
                // TODO: This loop is very naive, can be optimized, but let's first verify if we indeed want this or not.
                for (int iterations = 0; iterations < dilationSettings.dilationIterations; ++iterations)
                {
                    // Make sure all is loaded before performing dilation.
                    ProbeReferenceVolume.instance.PerformPendingOperations(loadAllCells: true);

                    // Dilate all cells
                    List<ProbeReferenceVolume.Cell> dilatedCells = new List<ProbeReferenceVolume.Cell>(ProbeReferenceVolume.instance.cells.Values.Count);

                    foreach (var cell in ProbeReferenceVolume.instance.cells.Values)
                    {
                        PerformDilation(cell, dilationSettings);
                        dilatedCells.Add(cell);
                    }

                    foreach (var sceneList in m_BakingBatch.cellIndex2SceneReferences.Values)
                    {
                        foreach (var scene in sceneList)
                        {
                            ProbeReferenceVolumeAuthoring refVol = null;
                            if (scene2RefVol.TryGetValue(scene, out refVol))
                            {
                                ProbeReferenceVolume.instance.AddPendingAssetRemoval(refVol2Asset[refVol]);
                            }
                        }
                    }

                    // Make sure unloading happens.
                    ProbeReferenceVolume.instance.PerformPendingOperations();

                    Dictionary<string, bool> assetCleared = new Dictionary<string, bool>();
                    // Put back cells
                    foreach (var cell in dilatedCells)
                    {
                        foreach (var scene in m_BakingBatch.cellIndex2SceneReferences[cell.index])
                        {
                            // This scene has a reference volume authoring component in it?
                            ProbeReferenceVolumeAuthoring refVol = null;
                            if (scene2RefVol.TryGetValue(scene, out refVol))
                            {
                                var asset = refVol2Asset[refVol];
                                bool valueFound = false;
                                if (!assetCleared.TryGetValue(asset.GetSerializedFullPath(), out valueFound))
                                {
                                    asset.cells.Clear();
                                    assetCleared.Add(asset.GetSerializedFullPath(), true);
                                    UnityEditor.EditorUtility.SetDirty(asset);
                                }
                                asset.cells.Add(cell);
                            }
                        }
                    }
                    UnityEditor.AssetDatabase.SaveAssets();
                    UnityEditor.AssetDatabase.Refresh();

                    foreach (var refVol in refVol2Asset.Keys)
                    {
                        if (refVol.enabled && refVol.gameObject.activeSelf)
                            refVol.QueueAssetLoading();
                    }
                }
            }
        }

        static void OnLightingDataCleared()
        {
            Clear();
        }

        static float CalculateSurfaceArea(Matrix4x4 transform, Mesh mesh)
        {
            var triangles = mesh.triangles;
            var vertices = mesh.vertices;

            for (int i = 0; i < vertices.Length; ++i)
            {
                vertices[i] = transform * vertices[i];
            }

            double sum = 0.0;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                Vector3 corner = vertices[triangles[i]];
                Vector3 a = vertices[triangles[i + 1]] - corner;
                Vector3 b = vertices[triangles[i + 2]] - corner;

                sum += Vector3.Cross(a, b).magnitude;
            }

            return (float)(sum / 2.0);
        }

        private static void DeduplicateProbePositions(in Vector3[] probePositions, Dictionary<Vector3, int> uniquePositions, out int[] indices)
        {
            indices = new int[probePositions.Length];
            int uniqueIndex = uniquePositions.Count;

            for (int i = 0; i < probePositions.Length; i++)
            {
                var pos = probePositions[i];

                if (uniquePositions.TryGetValue(pos, out var index))
                {
                    indices[i] = index;
                }
                else
                {
                    uniquePositions[pos] = uniqueIndex;
                    indices[i] = uniqueIndex;
                    uniqueIndex++;
                }
            }
        }

        public static void OnBakeCompletedCleanup()
        {
            if (!onAdditionalProbesBakeCompletedCalled)
            {
                // Dequeue the call if something has failed.
                UnityEditor.Experimental.Lightmapping.additionalBakedProbesCompleted -= OnAdditionalProbesBakeCompleted;
                UnityEditor.Experimental.Lightmapping.SetAdditionalBakedProbes(m_BakingBatch.index, null);
            }
        }

        public static void RunPlacement()
        {
            onAdditionalProbesBakeCompletedCalled = false;
            UnityEditor.Experimental.Lightmapping.additionalBakedProbesCompleted += OnAdditionalProbesBakeCompleted;
            UnityEditor.Lightmapping.bakeCompleted += OnBakeCompletedCleanup;

            // Clear baked data
            Clear();

            // Subdivide the scene and place the bricks
            var ctx = PrepareProbeSubdivisionContext(m_BakingReferenceVolumeAuthoring);
            var result = BakeBricks(ctx);

            // Compute probe positions and send them to the Lightmapper
            ApplySubdivisionResults(result);
        }

        public static ProbeSubdivisionContext PrepareProbeSubdivisionContext(ProbeReferenceVolumeAuthoring refVolume)
        {
            ProbeSubdivisionContext ctx = new ProbeSubdivisionContext();

            // Prepare all the information in the scene for baking GI.
            ctx.Initialize(refVolume);

            return ctx;
        }

        static void TrackSceneRefs(Scene origin, Dictionary<Scene, int> sceneRefs)
        {
            if (!sceneRefs.ContainsKey(origin))
                sceneRefs[origin] = 0;
            else
                sceneRefs[origin] += 1;
        }

        public static ProbeSubdivisionResult BakeBricks(ProbeSubdivisionContext ctx)
        {
            var result = new ProbeSubdivisionResult();
            var sceneRefs = new Dictionary<Scene, int>();

            bool realtimeSubdivision = ProbeReferenceVolume.instance.debugDisplay.realtimeSubdivision;
            if (realtimeSubdivision)
                ctx.refVolume.realtimeSubdivisionInfo.Clear();

            using (var gpuResources = ProbePlacement.AllocateGPUResources(ctx.probeVolumes.Count, ctx.refVolume.profile.maxSubdivision))
            {
                // subdivide all the cells and generate brick positions
                foreach (var cell in ctx.cells)
                {
                    sceneRefs.Clear();

                    // Calculate overlaping probe volumes to avoid unnecessary work
                    var overlappingProbeVolumes = new List<(ProbeVolume component, ProbeReferenceVolume.Volume volume)>();
                    foreach (var probeVolume in ctx.probeVolumes)
                    {
                        if (ProbeVolumePositioning.OBBIntersect(probeVolume.volume, cell.volume))
                        {
                            overlappingProbeVolumes.Add(probeVolume);
                            TrackSceneRefs(probeVolume.component.gameObject.scene, sceneRefs);
                        }
                    }

                    // Calculate valid renderers to avoid unnecessary work (a renderer needs to overlap a probe volume and match the layer)
                    var validRenderers = new List<(Renderer component, ProbeReferenceVolume.Volume volume)>();
                    foreach (var renderer in ctx.renderers)
                    {
                        var go = renderer.component.gameObject;
                        int rendererLayerMask = 1 << go.layer;

                        foreach (var probeVolume in overlappingProbeVolumes)
                        {
                            if (ProbeVolumePositioning.OBBIntersect(renderer.volume, probeVolume.volume)
                                && ProbeVolumePositioning.OBBIntersect(renderer.volume, cell.volume))
                            {
                                // Check if the renderer has a matching layer with probe volume
                                if ((probeVolume.component.objectLayerMask & rendererLayerMask) != 0)
                                {
                                    validRenderers.Add(renderer);
                                    TrackSceneRefs(go.scene, sceneRefs);
                                }
                            }
                        }
                    }

                    // Skip empty cells
                    if (validRenderers.Count == 0 && overlappingProbeVolumes.Count == 0)
                        continue;

                    var bricks = ProbePlacement.SubdivideCell(cell.volume, ctx, gpuResources, validRenderers, overlappingProbeVolumes);

                    // Each cell keeps a number of references it has to each scene it was influenced by
                    // We use this list to determine which scene's ProbeVolume asset to assign this cells data to
                    var sortedRefs = new SortedDictionary<int, Scene>();
                    foreach (var item in sceneRefs)
                        sortedRefs[-item.Value] = item.Key;

                    result.cellPositions.Add(cell.position);
                    result.bricksPerCells[cell.position] = bricks;
                    result.sortedRefs = sortedRefs;

                    // If realtime subdivision is enabled, we save a copy of the data inside the authoring component for the debug view
                    if (realtimeSubdivision)
                        ctx.refVolume.realtimeSubdivisionInfo[cell.volume] = bricks;
                }
            }

            return result;
        }

        public static void ApplySubdivisionResults(ProbeSubdivisionResult results)
        {
            int index = 0;
            // For now we just have one baking batch. Later we'll have more than one for a set of scenes.
            // All probes need to be baked only once for the whole batch and not once per cell
            // The reason is that the baker is not deterministic so the same probe position baked in two different cells may have different values causing seams artefacts.
            m_BakingBatch = new BakingBatch(m_BakingBatchIndex++);

            foreach (var cellPos in results.cellPositions)
            {
                var bricks = results.bricksPerCells[cellPos];
                var cell = new ProbeReferenceVolume.Cell();

                cell.position = cellPos;
                cell.index = index++;
                if (bricks.Count > 0)
                {
                    // Convert bricks to positions
                    var probePositionsArr = new Vector3[bricks.Count * ProbeBrickPool.kBrickProbeCountTotal];
                    ProbeReferenceVolume.instance.ConvertBricksToPositions(bricks, probePositionsArr);

                    int[] indices = null;
                    DeduplicateProbePositions(in probePositionsArr, m_BakingBatch.uniquePositions, out indices);

                    cell.probePositions = probePositionsArr;
                    cell.bricks = bricks;

                    BakingCell bakingCell = new BakingCell();
                    bakingCell.cell = cell;
                    bakingCell.probeIndices = indices;

                    m_BakingBatch.cells.Add(bakingCell);
                    m_BakingBatch.cellIndex2SceneReferences[cell.index] = new List<Scene>(results.sortedRefs.Values);
                }
            }

            UnityEditor.Experimental.Lightmapping.SetAdditionalBakedProbes(m_BakingBatch.index, m_BakingBatch.uniquePositions.Keys.ToArray());
        }
    }
}

#endif
