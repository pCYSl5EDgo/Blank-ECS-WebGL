using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Profiling;

namespace Unity.Rendering
{
    // struct VisibleLocalToWorld : ISystemStateComponentData
    // #TODO Bulk add/remove SystemStateComponentData
    public struct VisibleLocalToWorld : IComponentData
    {
        public float4x4 Value;
    };

    readonly struct FrustumPlanes
    {
        public readonly float4 Left;
        public readonly float4 Right;
        public readonly float4 Down;
        public readonly float4 Up;
        public readonly float4 Near;
        public readonly float4 Far;

        public enum InsideResult
        {
            Out,
            In,
            Partial
        };

        public FrustumPlanes(Camera camera)
        {
            Plane[] sourcePlanes = GeometryUtility.CalculateFrustumPlanes(camera);
            Left = new float4(sourcePlanes[0].normal.x, sourcePlanes[0].normal.y, sourcePlanes[0].normal.z, sourcePlanes[0].distance);
            Right = new float4(sourcePlanes[1].normal.x, sourcePlanes[1].normal.y, sourcePlanes[1].normal.z, sourcePlanes[1].distance);
            Down = new float4(sourcePlanes[2].normal.x, sourcePlanes[2].normal.y, sourcePlanes[2].normal.z, sourcePlanes[2].distance);
            Up = new float4(sourcePlanes[3].normal.x, sourcePlanes[3].normal.y, sourcePlanes[3].normal.z, sourcePlanes[3].distance);
            Near = new float4(sourcePlanes[4].normal.x, sourcePlanes[4].normal.y, sourcePlanes[4].normal.z, sourcePlanes[4].distance);
            Far = new float4(sourcePlanes[5].normal.x, sourcePlanes[5].normal.y, sourcePlanes[5].normal.z, sourcePlanes[5].distance);
        }

        public InsideResult Inside(in WorldMeshRenderBounds bounds)
        {
            var center = new float4(bounds.Center.x, bounds.Center.y, bounds.Center.z, 1.0f);

            var leftDistance = math.dot(Left, center);
            var rightDistance = math.dot(Right, center);
            var downDistance = math.dot(Down, center);
            var upDistance = math.dot(Up, center);
            var nearDistance = math.dot(Near, center);
            var farDistance = math.dot(Far, center);

            var leftOut = leftDistance < -bounds.Radius;
            var rightOut = rightDistance < -bounds.Radius;
            var downOut = downDistance < -bounds.Radius;
            var upOut = upDistance < -bounds.Radius;
            var nearOut = nearDistance < -bounds.Radius;
            var farOut = farDistance < -bounds.Radius;
            var anyOut = leftOut || rightOut || downOut || upOut || nearOut || farOut;

            var leftIn = leftDistance > bounds.Radius;
            var rightIn = rightDistance > bounds.Radius;
            var downIn = downDistance > bounds.Radius;
            var upIn = upDistance > bounds.Radius;
            var nearIn = nearDistance > bounds.Radius;
            var farIn = farDistance > bounds.Radius;
            var allIn = leftIn && rightIn && downIn && upIn && nearIn && farIn;


            if (anyOut)
                return InsideResult.Out;
            if (allIn)
                return InsideResult.In;
            return InsideResult.Partial;
        }
    }

    /// <summary>
    /// Renders all Entities containing both MeshInstanceRenderer & LocalToWorld components.
    /// </summary>
    [ExecuteInEditMode]
    public sealed class MeshInstanceRendererSystem : ComponentSystem
    {
        public MeshInstanceRendererSystem(Camera activeCamera) => ActiveCamera = activeCamera;
        public MeshInstanceRendererSystem() { }
        public Camera ActiveCamera;

        private int m_LastVisibleLocalToWorldOrderVersion = -1;
        private int m_LastLocalToWorldOrderVersion = -1;
        private int m_LastCustomLocalToWorldOrderVersion = -1;

        private NativeArray<ArchetypeChunk> m_Chunks;
        private NativeArray<WorldMeshRenderBounds> m_ChunkBounds;

        // Instance renderer takes only batches of 1023
        readonly Matrix4x4[] m_MatricesArray = new Matrix4x4[1023];
        private FrustumPlanes m_Planes;
        uint m_LastGlobalSystemVersion = 0;

        EntityArchetypeQuery m_CustomLocalToWorldQuery;
        EntityArchetypeQuery m_LocalToWorldQuery;

        static unsafe void CopyTo(NativeSlice<VisibleLocalToWorld> transforms, int count, Matrix4x4[] outMatrices, int offset)
        {
            // @TODO: This is using unsafe code because the Unity DrawInstances API takes a Matrix4x4[] instead of NativeArray.
            Assert.AreEqual(sizeof(Matrix4x4), sizeof(VisibleLocalToWorld));
            fixed (Matrix4x4* resultMatrices = outMatrices)
            {
                VisibleLocalToWorld* sourceMatrices = (VisibleLocalToWorld*)transforms.GetUnsafeReadOnlyPtr();
                UnsafeUtility.MemCpy(resultMatrices + offset, sourceMatrices, UnsafeUtility.SizeOf<Matrix4x4>() * count);
            }
        }

        protected override void OnCreateManager(int capacity)
        {
            m_CustomLocalToWorldQuery = new EntityArchetypeQuery
            {
                Any = Array.Empty<ComponentType>(),
                None = Array.Empty<ComponentType>(),
                All = new ComponentType[] { ComponentType.Create<CustomLocalToWorld>(), ComponentType.Create<MeshInstanceRenderer>(), ComponentType.Create<VisibleLocalToWorld>() }
            };
            m_LocalToWorldQuery = new EntityArchetypeQuery
            {
                Any = Array.Empty<ComponentType>(),
                None = Array.Empty<ComponentType>(),
                All = new ComponentType[] { ComponentType.Create<LocalToWorld>(), ComponentType.Create<MeshInstanceRenderer>(), ComponentType.Create<VisibleLocalToWorld>() }
            };
        }

        protected override void OnDestroyManager()
        {
            if (m_Chunks.IsCreated)
                m_Chunks.Dispose();
            if (m_ChunkBounds.IsCreated)
                m_ChunkBounds.Dispose();
        }

        unsafe void UpdateInstanceRenderer()
        {
            if (m_Chunks.Length == 0) return;

            Profiler.BeginSample("Gather Types");
            var sharedComponentCount = EntityManager.GetSharedComponentCount();
            var customLocalToWorldType = GetArchetypeChunkComponentType<CustomLocalToWorld>(true);
            var localToWorldType = GetArchetypeChunkComponentType<LocalToWorld>(true);
            var visibleLocalToWorldType = GetArchetypeChunkComponentType<VisibleLocalToWorld>(false);
            var meshInstanceRendererType = GetArchetypeChunkSharedComponentType<MeshInstanceRenderer>();
            var meshInstanceFlippedTagType = GetArchetypeChunkComponentType<MeshInstanceFlippedWindingTag>();
            var worldMeshRenderBoundsType = GetArchetypeChunkComponentType<WorldMeshRenderBounds>(true);
            var meshLODComponentType = GetArchetypeChunkComponentType<MeshLODComponent>(true);
            var activeLODGroupMask = GetComponentDataFromEntity<ActiveLODGroupMask>(true);
            Profiler.EndSample();

            Profiler.BeginSample("Allocate Temp Data");
            var chunkVisibleCount = new NativeArray<int>(m_Chunks.Length, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var packedChunkIndices = new NativeArray<int>(m_Chunks.Length, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            Profiler.EndSample();
            try
            {
                float4x4* GetVisibleOutputBuffer(ref ArchetypeChunk chunk) => (float4x4*)chunk.GetNativeArray(visibleLocalToWorldType).GetUnsafePtr();

                float4x4* GetLocalToWorldSourceBuffer(ref ArchetypeChunk chunk)
                {
                    var chunkCustomLocalToWorld = chunk.GetNativeArray(customLocalToWorldType);
                    var chunkLocalToWorld = chunk.GetNativeArray(localToWorldType);

                    if (chunkCustomLocalToWorld.Length > 0)
                        return (float4x4*)chunkCustomLocalToWorld.GetUnsafeReadOnlyPtr();
                    else if (chunkLocalToWorld.Length > 0)
                        return (float4x4*)chunkLocalToWorld.GetUnsafeReadOnlyPtr();
                    else
                        return null;
                }

                void VisibleIn(int index)
                {
                    var chunk = m_Chunks[index];
                    var chunkEntityCount = chunk.Count;
                    var chunkVisibleCount_ = 0;
                    var chunkLODs = chunk.GetNativeArray(meshLODComponentType);
                    var hasMeshLODComponentType = chunkLODs.Length > 0;

                    var srcPtr = GetLocalToWorldSourceBuffer(ref chunk);
                    if (srcPtr == null) return;
                    var dstPtr = GetVisibleOutputBuffer(ref chunk);

                    if (hasMeshLODComponentType)
                    {
                        for (int i = 0; i < chunkEntityCount; i++)
                        {
                            var instanceLOD = chunkLODs[i];
                            var instanceLODValid = (activeLODGroupMask[instanceLOD.Group].LODMask & instanceLOD.LODMask) != 0;
                            if (instanceLODValid)
                            {
                                UnsafeUtility.MemCpy(dstPtr + chunkVisibleCount_, srcPtr + i, UnsafeUtility.SizeOf<float4x4>());
                                chunkVisibleCount_++;
                            }
                        }
                    }
                    else
                    {
                        for (int i = 0; i < chunkEntityCount; i++)
                            UnsafeUtility.MemCpy(dstPtr + chunkVisibleCount_ + i, srcPtr + i, UnsafeUtility.SizeOf<float4x4>());
                        chunkVisibleCount_ = chunkEntityCount;
                    }

                    chunkVisibleCount[index] = chunkVisibleCount_;
                }

                void VisiblePartial(int index)
                {
                    var chunk = m_Chunks[index];
                    var chunkEntityCount = chunk.Count;
                    var chunkVisibleCount_ = 0;
                    var chunkLODs = chunk.GetNativeArray(meshLODComponentType);
                    var chunkBounds = chunk.GetNativeArray(worldMeshRenderBoundsType);
                    var hasMeshLODComponentType = chunkLODs.Length > 0;
                    var hasWorldMeshRenderBounds = chunkBounds.Length > 0;

                    var srcPtr = GetLocalToWorldSourceBuffer(ref chunk);
                    if (srcPtr == null) return;
                    var dstPtr = GetVisibleOutputBuffer(ref chunk);

                    // 00 (-WorldMeshRenderBounds -MeshLODComponentType)
                    if ((!hasWorldMeshRenderBounds) && (!hasMeshLODComponentType))
                    {
                        for (int i = 0; i < chunkEntityCount; i++)
                            UnsafeUtility.MemCpy(dstPtr + chunkVisibleCount_ + i, srcPtr + i, UnsafeUtility.SizeOf<float4x4>());
                        chunkVisibleCount_ = chunkEntityCount;
                    }
                    // 01 (-WorldMeshRenderBounds +MeshLODComponentType)
                    else if ((!hasWorldMeshRenderBounds) && (hasMeshLODComponentType))
                    {
                        for (int i = 0; i < chunkEntityCount; i++)
                        {
                            var instanceLOD = chunkLODs[i];
                            if ((activeLODGroupMask[instanceLOD.Group].LODMask & instanceLOD.LODMask) == 0) continue;
                            UnsafeUtility.MemCpy(dstPtr + chunkVisibleCount_, srcPtr + i, UnsafeUtility.SizeOf<float4x4>());
                            chunkVisibleCount_++;
                        }
                    }
                    // 10 (+WorldMeshRenderBounds -MeshLODComponentType)
                    else if ((hasWorldMeshRenderBounds) && (!hasMeshLODComponentType))
                    {
                        for (int i = 0; i < chunkEntityCount; i++)
                        {
                            var instanceBounds = chunkBounds[i];
                            if (m_Planes.Inside(instanceBounds) == FrustumPlanes.InsideResult.Out) continue;
                            UnsafeUtility.MemCpy(dstPtr + chunkVisibleCount_, srcPtr + i, UnsafeUtility.SizeOf<float4x4>());
                            chunkVisibleCount_++;
                        }
                    }
                    // 11 (+WorldMeshRenderBounds +MeshLODComponentType)
                    else
                    {
                        for (int i = 0; i < chunkEntityCount; i++)
                        {
                            var instanceLOD = chunkLODs[i];
                            if ((activeLODGroupMask[instanceLOD.Group].LODMask & instanceLOD.LODMask) == 0 || m_Planes.Inside(chunkBounds[i]) == FrustumPlanes.InsideResult.Out)
                                continue;
                            UnsafeUtility.MemCpy(dstPtr + chunkVisibleCount_, srcPtr + i, UnsafeUtility.SizeOf<float4x4>());
                            chunkVisibleCount_++;
                        }
                    }
                    chunkVisibleCount[index] = chunkVisibleCount_;
                }
                for (int index = 0; index < m_Chunks.Length; index++)
                {
                    if (m_Chunks[index].GetNativeArray(worldMeshRenderBoundsType).Length <= 0)
                    {
                        VisibleIn(index);
                        continue;
                    }

                    var chunkInsideResult = m_Planes.Inside(m_ChunkBounds[index]);
                    if (chunkInsideResult == FrustumPlanes.InsideResult.Out)
                        chunkVisibleCount[index] = 0;
                    else if (chunkInsideResult == FrustumPlanes.InsideResult.In)
                        VisibleIn(index);
                    else
                        VisiblePartial(index);
                }

                var packedChunkCount = 0;
                for (int i = 0; i < m_Chunks.Length; i++)
                    if (chunkVisibleCount[i] > 0)
                        packedChunkIndices[packedChunkCount++] = i;

                Profiler.BeginSample("Process DrawMeshInstanced");
                var drawCount = 0;
                var lastRendererIndex = -1;
                var batchCount = 0;
                var flippedWinding = false;

                for (int i = 0; i < packedChunkCount; i++)
                {
                    var chunkIndex = packedChunkIndices[i];
                    var chunk = m_Chunks[chunkIndex];
                    var rendererIndex = chunk.GetSharedComponentIndex(meshInstanceRendererType);
                    var activeCount = chunkVisibleCount[chunkIndex];
                    var rendererChanged = rendererIndex != lastRendererIndex;
                    var fullBatch = ((batchCount + activeCount) > 1023);
                    var visibleTransforms = chunk.GetNativeArray(visibleLocalToWorldType);

                    var newFlippedWinding = chunk.GetNativeArray(meshInstanceFlippedTagType).Length > 0;

                    if ((fullBatch || rendererChanged || (newFlippedWinding != flippedWinding)) && (batchCount > 0))
                    {
                        var renderer = EntityManager.GetSharedComponentData<MeshInstanceRenderer>(lastRendererIndex);
                        if (renderer.mesh && renderer.material)
                        {
                            Graphics.DrawMeshInstanced(renderer.mesh, renderer.subMesh, renderer.material, m_MatricesArray,
                                batchCount, null, renderer.castShadows, renderer.receiveShadows, 0, ActiveCamera);
                        }

                        drawCount++;
                        batchCount = 0;
                    }

                    CopyTo(visibleTransforms, activeCount, m_MatricesArray, batchCount);

                    flippedWinding = newFlippedWinding;
                    batchCount += activeCount;
                    lastRendererIndex = rendererIndex;
                }

                if (batchCount > 0)
                {
                    var renderer = EntityManager.GetSharedComponentData<MeshInstanceRenderer>(lastRendererIndex);
                    if (renderer.mesh && renderer.material)
                    {
                        Graphics.DrawMeshInstanced(renderer.mesh, renderer.subMesh, renderer.material, m_MatricesArray,
                            batchCount, null, renderer.castShadows, renderer.receiveShadows, 0, ActiveCamera);
                    }

                    drawCount++;
                }
                Profiler.EndSample();
            }
            finally
            {
                packedChunkIndices.Dispose();
                chunkVisibleCount.Dispose();
            }
        }

        void UpdateChunkCache()
        {
            var visibleLocalToWorldOrderVersion = EntityManager.GetComponentOrderVersion<VisibleLocalToWorld>();
            if (visibleLocalToWorldOrderVersion == m_LastVisibleLocalToWorldOrderVersion)
                return;

            // Dispose
            if (m_Chunks.IsCreated)
            {
                m_Chunks.Dispose();
            }
            if (m_ChunkBounds.IsCreated)
            {
                m_ChunkBounds.Dispose();
            }

            var sharedComponentCount = EntityManager.GetSharedComponentCount();
            var meshInstanceRendererType = GetArchetypeChunkSharedComponentType<MeshInstanceRenderer>();
            var worldMeshRenderBoundsType = GetArchetypeChunkComponentType<WorldMeshRenderBounds>(true);

            // Allocate temp data
            var chunkRendererMap = new NativeMultiHashMap<int, int>(100000, Allocator.Temp);
            var foundArchetypes = new NativeList<EntityArchetype>(Allocator.Temp);
            try
            {
                Profiler.BeginSample("CreateArchetypeChunkArray");
                EntityManager.AddMatchingArchetypes(m_CustomLocalToWorldQuery, foundArchetypes);
                EntityManager.AddMatchingArchetypes(m_LocalToWorldQuery, foundArchetypes);
                var chunks = EntityManager.CreateArchetypeChunkArray(foundArchetypes, Allocator.Temp);
                Profiler.EndSample();
                try
                {
                    m_Chunks = new NativeArray<ArchetypeChunk>(chunks.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                    m_ChunkBounds = new NativeArray<WorldMeshRenderBounds>(chunks.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

                    for (int index = 0; index < chunks.Length; index++)
                    {
                        var chunk = chunks[index];
                        var rendererSharedComponentIndex = chunk.GetSharedComponentIndex(meshInstanceRendererType);
                        chunkRendererMap.Add(rendererSharedComponentIndex, index);
                    }

                    for (int i = 0, sortedIndex = 0; i < sharedComponentCount; i++)
                    {
                        if (!chunkRendererMap.TryGetFirstValue(i, out var chunkIndex, out var it))
                            continue;
                        do
                        {
                            m_Chunks[sortedIndex] = chunks[chunkIndex];
                            sortedIndex++;
                        } while (chunkRendererMap.TryGetNextValue(out chunkIndex, ref it));
                    }

                    for (int index = 0; index < chunks.Length; index++)
                    {
                        var chunk = m_Chunks[index];

                        var instanceBounds = chunk.GetNativeArray(worldMeshRenderBoundsType);
                        if (instanceBounds.Length == 0)
                            return;

                        // TODO: Improve this approach
                        // See: https://www.inf.ethz.ch/personal/emo/DoctThesisFiles/fischer05.pdf

                        var chunkBounds = new WorldMeshRenderBounds();
                        for (int j = 0; j < instanceBounds.Length; j++)
                        {
                            chunkBounds.Center += instanceBounds[j].Center;
                        }
                        chunkBounds.Center /= instanceBounds.Length;

                        for (int j = 0; j < instanceBounds.Length; j++)
                        {
                            float r = math.distance(chunkBounds.Center, instanceBounds[j].Center) + instanceBounds[j].Radius;
                            chunkBounds.Radius = math.select(chunkBounds.Radius, r, r > chunkBounds.Radius);
                        }

                        m_ChunkBounds[index] = chunkBounds;
                    }
                }
                finally
                {
                    chunks.Dispose();
                }
            }
            finally
            {
                foundArchetypes.Dispose();
                chunkRendererMap.Dispose();
            }

            m_LastVisibleLocalToWorldOrderVersion = visibleLocalToWorldOrderVersion;
        }

        void UpdateMissingVisibleLocalToWorld()
        {
            var localToWorldOrderVersion = EntityManager.GetComponentOrderVersion<LocalToWorld>();
            var customLocalToWorldOrderVersion = EntityManager.GetComponentOrderVersion<CustomLocalToWorld>();

            if ((localToWorldOrderVersion == m_LastLocalToWorldOrderVersion) &&
                (customLocalToWorldOrderVersion == m_LastCustomLocalToWorldOrderVersion))
                return;


            EntityCommandBuffer entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

            var query = new EntityArchetypeQuery
            {
                Any = new ComponentType[] { typeof(LocalToWorld), typeof(CustomLocalToWorld) },
                None = new ComponentType[] { typeof(VisibleLocalToWorld) },
                All = new ComponentType[] { typeof(MeshInstanceRenderer) }
            };
            var entityType = GetArchetypeChunkEntityType();
            var chunks = EntityManager.CreateArchetypeChunkArray(query, Allocator.Temp);
            for (int i = 0; i < chunks.Length; i++)
            {
                var chunk = chunks[i];
                var entities = chunk.GetNativeArray(entityType);
                for (int j = 0; j < chunk.Count; j++)
                {
                    var entity = entities[j];
                    entityCommandBuffer.AddComponent(entity, default(VisibleLocalToWorld));
                }
            }

            entityCommandBuffer.Playback(EntityManager);
            entityCommandBuffer.Dispose();
            chunks.Dispose();

            m_LastLocalToWorldOrderVersion = localToWorldOrderVersion;
            m_LastCustomLocalToWorldOrderVersion = customLocalToWorldOrderVersion;
        }

        protected override void OnUpdate()
        {
            if (ActiveCamera == null) return;
            m_Planes = new FrustumPlanes(ActiveCamera);

            UpdateMissingVisibleLocalToWorld();
            UpdateChunkCache();

            Profiler.BeginSample("UpdateInstanceRenderer");
            UpdateInstanceRenderer();
            Profiler.EndSample();

            m_LastGlobalSystemVersion = GlobalSystemVersion;
        }
    }
}