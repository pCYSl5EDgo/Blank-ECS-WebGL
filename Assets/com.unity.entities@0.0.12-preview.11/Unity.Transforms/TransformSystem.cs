using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Profiling;

namespace Unity.Transforms
{
    /// <summary>
    /// Parent is added to child by system when Attached is resolved.
    /// Read-only from other systems.
    /// </summary>
    public readonly struct Parent : ISystemStateComponentData
    {
        public Parent(Entity value) => Value = value;
        public readonly Entity Value;
    }

    /// <summary>
    /// Frozen is added by system when Static is resolved.
    /// Signals that LocalToWorld will no longer be updated.
    /// Read-only from other systems.
    /// </summary>
    public readonly struct Frozen : ISystemStateComponentData
    {
    }

    /// <summary>
    /// PendingFrozen is internal to the system and defines the pipeline stage
    /// between Static and Frozen. It allows for LocalToWorld to be updated once
    /// before it is Frozen.
    /// Not intended for use in other systems.
    /// </summary>
    readonly struct PendingFrozen : ISystemStateComponentData
    {
    }

    /// <summary>
    /// Internal grouping of by graph depth for parent components. Transform hierarchy
    /// is processed bredth-first.
    /// Read-only from external systems.
    /// </summary>
    public readonly struct Depth : ISystemStateSharedComponentData
    {
        public Depth(int value) => Value = value;
        public readonly int Value;
    }

    /// <summary>
    /// LocalToWorld is added by system if Rotation +/- Position +/- Scale exist.
    /// Updated by system.
    /// Read-only from external systems.
    /// </summary>
    public struct LocalToWorld : ISystemStateComponentData
    {
        public LocalToWorld(float4x4 value) => Value = value;
        public float4x4 Value;
    }

    /// <summary>
    /// LocalToParent is added by system when Attached is resolved for all children.
    /// Updated by system from Rotation +/- Position +/- Scale.
    /// Read-only from external systems.
    /// </summary>
    public readonly struct LocalToParent : ISystemStateComponentData
    {
        public LocalToParent(float4x4 value) => Value = value;
        public readonly float4x4 Value;
    }

    /// <summary>
    /// Default TransformSystem pass. Transform components updated before EndFrameBarrier.
    /// </summary>
    [UnityEngine.ExecuteInEditMode]
    [UpdateBefore(typeof(EndFrameBarrier))]
    public sealed class EndFrameTransformSystem : TransformSystem<EndFrameBarrier>
    {
    }

    public class TransformSystem<T> : ComponentSystem
    where T : ComponentSystemBase
    {
        uint LastSystemVersion = 0;

        // Internally tracked state of Parent->Child relationships.
        // Child->Parent relationship stored in Parent component.
        NativeMultiHashMap<Entity, Entity> ParentToChildTree;

        EntityArchetypeQuery NewRootQuery;
        EntityArchetypeQuery AttachQuery;
        EntityArchetypeQuery DetachQuery;
        EntityArchetypeQuery RemovedQuery;
        EntityArchetypeQuery PendingFrozenQuery;
        EntityArchetypeQuery FrozenQuery;
        EntityArchetypeQuery ThawQuery;
        EntityArchetypeQuery RootLocalToWorldQuery;
        EntityArchetypeQuery InnerTreeLocalToParentQuery;
        EntityArchetypeQuery LeafLocalToParentQuery;
        EntityArchetypeQuery InnerTreeLocalToWorldQuery;
        EntityArchetypeQuery LeafLocalToWorldQuery;
        EntityArchetypeQuery DepthQuery;

        NativeArray<ArchetypeChunk> NewRootChunks;
        NativeArray<ArchetypeChunk> AttachChunks;
        NativeArray<ArchetypeChunk> DetachChunks;
        NativeArray<ArchetypeChunk> RemovedChunks;
        NativeArray<ArchetypeChunk> PendingFrozenChunks;
        NativeArray<ArchetypeChunk> FrozenChunks;
        NativeArray<ArchetypeChunk> ThawChunks;
        NativeArray<ArchetypeChunk> RootLocalToWorldChunks;
        NativeArray<ArchetypeChunk> InnerTreeLocalToParentChunks;
        NativeArray<ArchetypeChunk> LeafLocalToParentChunks;
        NativeArray<ArchetypeChunk> InnerTreeLocalToWorldChunks;
        NativeArray<ArchetypeChunk> LeafLocalToWorldChunks;
        NativeArray<ArchetypeChunk> DepthChunks;

        ComponentDataFromEntity<LocalToWorld> LocalToWorldFromEntityRW;
        ComponentDataFromEntity<Parent> ParentFromEntityRO;
        ComponentDataFromEntity<Parent> ParentFromEntityRW;
        ArchetypeChunkEntityType EntityTypeRO;
        ArchetypeChunkComponentType<LocalToWorld> LocalToWorldTypeRW;
        ArchetypeChunkComponentType<Parent> ParentTypeRO;
        ArchetypeChunkComponentType<LocalToParent> LocalToParentTypeRO;
        ArchetypeChunkComponentType<LocalToParent> LocalToParentTypeRW;
        ArchetypeChunkComponentType<Scale> ScaleTypeRO;
        ArchetypeChunkComponentType<Rotation> RotationTypeRO;
        ArchetypeChunkComponentType<Position> PositionTypeRO;
        ArchetypeChunkComponentType<Attach> AttachTypeRO;
        ArchetypeChunkComponentType<Frozen> FrozenTypeRO;
        ArchetypeChunkComponentType<PendingFrozen> PendingFrozenTypeRO;
        ArchetypeChunkSharedComponentType<Depth> DepthTypeRO;

        protected override void OnCreateManager(int capacity)
        {
            ParentToChildTree = new NativeMultiHashMap<Entity, Entity>(1024, Allocator.Persistent);
            GatherQueries();
        }

        protected override void OnDestroyManager()
        {
            ParentToChildTree.Dispose();
        }

        bool IsChildTree(Entity entity)
        {
            NativeMultiHashMapIterator<Entity> it;
            Entity foundChild;
            return ParentToChildTree.TryGetFirstValue(entity, out foundChild, out it);
        }

        void AddChildTree(Entity parentEntity, Entity childEntity)
        {
            ParentToChildTree.Add(parentEntity, childEntity);
        }

        void RemoveChildTree(Entity parentEntity, Entity childEntity)
        {
            if (!ParentToChildTree.TryGetFirstValue(parentEntity, out var foundChild, out var it)) return;

            do
            {
                if (foundChild == childEntity)
                {
                    ParentToChildTree.Remove(it);
                    return;
                }
            } while (ParentToChildTree.TryGetNextValue(out foundChild, ref it));

            throw new System.InvalidOperationException(string.Format("Parent not found in Hierarchy hashmap"));
        }

        void UpdateNewRootTransforms()
        {
            try
            {
                if (NewRootChunks.Length == 0) return;

                for (int chunkIndex = 0; chunkIndex < NewRootChunks.Length; chunkIndex++)
                {
                    var chunk = NewRootChunks[chunkIndex];
                    var parentCount = chunk.Count;

                    var chunkEntities = chunk.GetNativeArray(EntityTypeRO);

                    for (int i = 0; i < parentCount; i++)
                        PostUpdateCommands.AddComponent(chunkEntities[i], new LocalToWorld(float4x4.identity));
                }
            }
            finally { NewRootChunks.Dispose(); }
        }

        bool UpdateAttach()
        {
            try
            {
                if (AttachChunks.Length == 0) return false;

                for (int chunkIndex = 0; chunkIndex < AttachChunks.Length; chunkIndex++)
                {
                    var chunk = AttachChunks[chunkIndex];
                    var parentCount = chunk.Count;
                    var entities = chunk.GetNativeArray(EntityTypeRO);
                    var attaches = chunk.GetNativeArray(AttachTypeRO);

                    for (int i = 0; i < parentCount; i++)
                    {
                        var parentEntity = attaches[i].Parent;
                        var childEntity = attaches[i].Child;

                        // Does the child have a previous parent?
                        if (EntityManager.HasComponent<Parent>(childEntity))
                        {
                            var previousParent = ParentFromEntityRW[childEntity];
                            var previousParentEntity = previousParent.Value;

                            if (IsChildTree(previousParentEntity))
                            {
                                RemoveChildTree(previousParentEntity, childEntity);
                                if (!IsChildTree(previousParentEntity))
                                    PostUpdateCommands.RemoveComponent<Depth>(previousParentEntity);
                            }

                            ParentFromEntityRW[childEntity] = new Parent(parentEntity);
                        }
                        else
                        {
                            PostUpdateCommands.AddComponent(childEntity, new Parent(parentEntity));
                            PostUpdateCommands.AddComponent(childEntity, new Attached());
                            PostUpdateCommands.AddComponent(childEntity, new LocalToParent(float4x4.identity));
                        }

                        // parent wasn't previously a tree, so doesn't have depth
                        if (!IsChildTree(parentEntity))
                            PostUpdateCommands.AddSharedComponent(parentEntity, new Depth(0));

                        AddChildTree(parentEntity, childEntity);

                        PostUpdateCommands.DestroyEntity(entities[i]);
                    }
                }

                return true;
            }
            finally { AttachChunks.Dispose(); }
        }

        bool UpdateDetach()
        {
            try
            {
                if (DetachChunks.Length == 0) return false;

                for (int chunkIndex = 0; chunkIndex < DetachChunks.Length; chunkIndex++)
                {
                    var chunk = DetachChunks[chunkIndex];

                    var parentCount = chunk.Count;
                    var chunkEntities = chunk.GetNativeArray(EntityTypeRO);
                    var parents = chunk.GetNativeArray(ParentTypeRO);

                    for (int i = 0; i < parentCount; i++)
                    {
                        var entity = chunkEntities[i];
                        var parentEntity = parents[i].Value;

                        if (IsChildTree(parentEntity))
                        {
                            RemoveChildTree(parentEntity, entity);

                            if (!IsChildTree(parentEntity))
                                PostUpdateCommands.RemoveComponent<Depth>(parentEntity);
                        }

                        PostUpdateCommands.RemoveComponent<LocalToParent>(entity);
                        PostUpdateCommands.RemoveComponent<Parent>(entity);
                    }
                }
                return true;
            }
            finally { DetachChunks.Dispose(); }
        }

        void UpdateRemoved()
        {
            try
            {
                if (RemovedChunks.Length == 0) return;

                for (int chunkIndex = 0; chunkIndex < RemovedChunks.Length; chunkIndex++)
                {
                    var chunk = RemovedChunks[chunkIndex];
                    var parentCount = chunk.Count;

                    var chunkEntities = chunk.GetNativeArray(EntityTypeRO);

                    for (int i = 0; i < parentCount; i++)
                        PostUpdateCommands.RemoveComponent<LocalToWorld>(chunkEntities[i]);
                }
            }
            finally { RemovedChunks.Dispose(); }
        }

        void UpdatePendingFrozen()
        {
            try
            {
                if (PendingFrozenChunks.Length == 0) return;

                for (int chunkIndex = 0; chunkIndex < PendingFrozenChunks.Length; chunkIndex++)
                {
                    var chunk = PendingFrozenChunks[chunkIndex];
                    var parentCount = chunk.Count;

                    var chunkEntities = chunk.GetNativeArray(EntityTypeRO);

                    for (int i = 0; i < parentCount; i++)
                    {
                        var entity = chunkEntities[i];

                        PostUpdateCommands.RemoveComponent<PendingFrozen>(entity);
                        PostUpdateCommands.AddComponent(entity, default(Frozen));
                    }
                }
            }
            finally { PendingFrozenChunks.Dispose(); }
        }

        void UpdateFrozen()
        {
            try
            {
                if (FrozenChunks.Length == 0) return;

                for (int chunkIndex = 0; chunkIndex < FrozenChunks.Length; chunkIndex++)
                {
                    var chunk = FrozenChunks[chunkIndex];
                    var parentCount = chunk.Count;

                    var chunkEntities = chunk.GetNativeArray(EntityTypeRO);

                    for (int i = 0; i < parentCount; i++)
                        PostUpdateCommands.AddComponent(chunkEntities[i], default(PendingFrozen));
                }
            }
            finally { FrozenChunks.Dispose(); }
        }

        void UpdateThaw()
        {
            try
            {
                if (ThawChunks.Length == 0) return;

                for (int chunkIndex = 0; chunkIndex < ThawChunks.Length; chunkIndex++)
                {
                    var chunk = ThawChunks[chunkIndex];
                    var parentCount = chunk.Count;

                    var chunkEntities = chunk.GetNativeArray(EntityTypeRO);
                    var chunkFrozens = chunk.GetNativeArray(FrozenTypeRO);
                    var chunkPendingFrozens = chunk.GetNativeArray(PendingFrozenTypeRO);
                    var hasFrozen = chunkFrozens.Length > 0;
                    var hasPendingFrozen = chunkPendingFrozens.Length > 0;

                    if (hasFrozen && (!hasPendingFrozen))
                        for (int i = 0; i < parentCount; i++)
                            PostUpdateCommands.RemoveComponent<Frozen>(chunkEntities[i]);
                    else if (hasFrozen && hasPendingFrozen)
                    {
                        for (int i = 0; i < parentCount; i++)
                        {
                            var entity = chunkEntities[i];
                            PostUpdateCommands.RemoveComponent<Frozen>(entity);
                            PostUpdateCommands.RemoveComponent<PendingFrozen>(entity);
                        }
                    }
                    else if ((!hasFrozen) && hasPendingFrozen)
                        for (int i = 0; i < parentCount; i++)
                            PostUpdateCommands.RemoveComponent<PendingFrozen>(chunkEntities[i]);
                }
            }
            finally { ThawChunks.Dispose(); }
        }

        private static readonly ProfilerMarker k_ProfileUpdateNewRootTransforms = new ProfilerMarker("UpdateNewRootTransforms");
        private static readonly ProfilerMarker k_ProfileUpdateDAGAttachDetach = new ProfilerMarker("UpdateDAG.AttachDetach");
        private static readonly ProfilerMarker k_ProfileUpdateUpdateRemoved = new ProfilerMarker("UpdateRemoved");
        bool UpdateDAG()
        {
            k_ProfileUpdateNewRootTransforms.Begin();
            UpdateNewRootTransforms();
            k_ProfileUpdateNewRootTransforms.End();

            k_ProfileUpdateDAGAttachDetach.Begin();
            var changedAttached = UpdateAttach();
            var changedDetached = UpdateDetach();
            k_ProfileUpdateDAGAttachDetach.End();

            k_ProfileUpdateUpdateRemoved.Begin();
            UpdateRemoved();
            k_ProfileUpdateUpdateRemoved.End();

            return changedAttached || changedDetached;
        }

        unsafe void UpdateRootLocalToWorld()
        {
            try
            {
                for (int length = RootLocalToWorldChunks.Length, chunkIndex = 0; chunkIndex < length; chunkIndex++)
                {
                    ref var chunk = ref UnsafeUtilityEx.ArrayElementAsRef<ArchetypeChunk>(RootLocalToWorldChunks.GetUnsafePtr(), chunkIndex);
                    var parentCount = chunk.Count;

                    var chunkRotations = chunk.GetNativeArray(RotationTypeRO);
                    var chunkPositions = chunk.GetNativeArray(PositionTypeRO);
                    var chunkScales = chunk.GetNativeArray(ScaleTypeRO);
                    var chunkLocalToWorlds = chunk.GetNativeArray(LocalToWorldTypeRW);

                    var chunkRotationsChanged = ChangeVersionUtility.DidAddOrChange(chunk.GetComponentVersion(RotationTypeRO), LastSystemVersion);
                    var chunkPositionsChanged = ChangeVersionUtility.DidAddOrChange(chunk.GetComponentVersion(PositionTypeRO), LastSystemVersion);
                    var chunkScalesChanged = ChangeVersionUtility.DidAddOrChange(chunk.GetComponentVersion(ScaleTypeRO), LastSystemVersion);
                    var chunkAnyChanged = chunkRotationsChanged || chunkPositionsChanged || chunkScalesChanged;

                    if (!chunkAnyChanged)
                        return;

                    var chunkRotationsExist = chunkRotations.Length > 0;
                    var chunkPositionsExist = chunkPositions.Length > 0;
                    var chunkScalesExist = chunkScales.Length > 0;

                    var chunkLocalToWorldsPtr = (LocalToWorld*)chunkLocalToWorlds.GetUnsafePtr();
                    var chunkPositionsPtr = (float3*)chunkPositions.GetUnsafeReadOnlyPtr();
                    var chunkScalesPtr = (float3*)chunkScales.GetUnsafeReadOnlyPtr();
                    var chunkRotationsPtr = (quaternion*)chunkRotations.GetUnsafeReadOnlyPtr();
                    // 001
                    if ((!chunkPositionsExist) && (!chunkRotationsExist) && (chunkScalesExist))
                        for (int i = 0; i < parentCount; i++)
                        {
                            ref var matrix = ref chunkLocalToWorldsPtr[i].Value;
                            ref var scale = ref chunkScalesPtr[i];
                            matrix.c0.x = scale.x; matrix.c0.y = matrix.c0.z = matrix.c0.w =
                            matrix.c1.x = 0; matrix.c1.y = scale.y; matrix.c1.z = matrix.c1.w =
                            matrix.c2.x = matrix.c2.y = 0; matrix.c2.z = scale.z; matrix.c2.w =
                            matrix.c3.x = matrix.c3.y = matrix.c3.z = 0; matrix.c3.w = 1;
                        }
                    // 010
                    else if ((!chunkPositionsExist) && (chunkRotationsExist) && (!chunkScalesExist))
                        for (int i = 0; i < parentCount; i++)
                        {
                            ref var quaternion = ref chunkRotationsPtr[i].value;
                            ref var matrix = ref chunkLocalToWorldsPtr[i].Value;
                            var rotation = math.normalize(quaternion);
                            var x = rotation.x * 2f;
                            var y = rotation.y * 2f;
                            var z = rotation.z * 2f;
                            var xx = rotation.x * x;
                            var yy = rotation.y * y;
                            var zz = rotation.z * z;
                            var xy = rotation.x * y;
                            var xz = rotation.x * z;
                            var yz = rotation.y * z;
                            var wx = rotation.w * x;
                            var wy = rotation.w * y;
                            var wz = rotation.w * z;
                            matrix.c0.x = 1f - (yy + zz);
                            matrix.c0.y = xy + wz;
                            matrix.c0.z = xz - wy;
                            matrix.c0.w = 0;
                            matrix.c1.x = xy - wz;
                            matrix.c1.y = 1 - (xx + zz);
                            matrix.c1.z = yz + wx;
                            matrix.c1.w = 0;
                            matrix.c2.x = xz + wy;
                            matrix.c2.y = yz - wz;
                            matrix.c2.z = 1f - (xx + yy);
                            matrix.c2.w = 0;
                            matrix.c3.x = matrix.c3.y = matrix.c3.z = 0; matrix.c3.w = 1; //  = new float4x4(chunkRotations[i].Value, new float3());
                        }
                    // 011
                    else if ((!chunkPositionsExist) && (chunkRotationsExist) && (chunkScalesExist))
                        for (int i = 0; i < parentCount; i++)
                        {
                            ref var scale = ref chunkScalesPtr[i];
                            chunkLocalToWorldsPtr[i].Value = math.mul(new float4x4(chunkRotations[i].Value, default(float3)), new float4x4(new float4(scale.x, 0, 0, 0), new float4(0, scale.y, 0, 0), new float4(0, 0, scale.z, 0), new float4(0, 0, 0, 1)));
                        }
                    // 100
                    else if ((chunkPositionsExist) && (!chunkRotationsExist) && (!chunkScalesExist))
                        for (int i = 0; i < parentCount; i++)
                        {
                            ref var position = ref chunkPositionsPtr[i];
                            ref var matrix = ref chunkLocalToWorldsPtr[i].Value; // = float4x4.translate(chunkPositions[i].Value);
                            matrix.c0.x = 1;
                            matrix.c0.y = matrix.c0.z = matrix.c0.w = matrix.c1.x = 0;
                            matrix.c1.y = 1;
                            matrix.c1.z = matrix.c1.w = matrix.c2.x = matrix.c2.y = 0;
                            matrix.c2.z = 1;
                            matrix.c2.w = 0;
                            matrix.c3.x = position.x;
                            matrix.c3.y = position.y;
                            matrix.c3.z = position.z;
                            matrix.c3.w = 1;
                        }
                    // 101
                    else if ((chunkPositionsExist) && (!chunkRotationsExist) && (chunkScalesExist))
                        for (int i = 0; i < parentCount; i++)
                        {
                            ref var position = ref chunkPositionsPtr[i];
                            ref var scale = ref chunkScalesPtr[i];
                            ref var matrix = ref chunkLocalToWorldsPtr[i].Value;
                            matrix.c0.x = scale.x; matrix.c0.y = matrix.c0.z = matrix.c0.w =
                            matrix.c1.x = 0; matrix.c1.y = scale.y; matrix.c1.z = matrix.c1.w =
                            matrix.c2.x = matrix.c2.y = 0; matrix.c2.z = scale.z; matrix.c2.w = 0;
                            matrix.c3.x = position.x; matrix.c3.y = position.y; matrix.c3.z = position.z; matrix.c3.w = 1;
                        }
                    // 110
                    else if ((chunkPositionsExist) && (chunkRotationsExist) && (!chunkScalesExist))
                        for (int i = 0; i < parentCount; i++)
                        {
                            ref var quaternion = ref chunkRotationsPtr[i].value;
                            ref var position = ref chunkPositionsPtr[i];
                            ref var matrix = ref chunkLocalToWorldsPtr[i].Value;
                            var rotation = math.normalize(quaternion);
                            var x = rotation.x * 2f;
                            var y = rotation.y * 2f;
                            var z = rotation.z * 2f;
                            var xx = rotation.x * x;
                            var yy = rotation.y * y;
                            var zz = rotation.z * z;
                            var xy = rotation.x * y;
                            var xz = rotation.x * z;
                            var yz = rotation.y * z;
                            var wx = rotation.w * x;
                            var wy = rotation.w * y;
                            var wz = rotation.w * z;
                            matrix.c0.x = 1f - (yy + zz);
                            matrix.c0.y = xy + wz;
                            matrix.c0.z = xz - wy;
                            matrix.c0.w = 0;
                            matrix.c1.x = xy - wz;
                            matrix.c1.y = 1 - (xx + zz);
                            matrix.c1.z = yz + wx;
                            matrix.c1.w = 0;
                            matrix.c2.x = xz + wy;
                            matrix.c2.y = yz - wz;
                            matrix.c2.z = 1f - (xx + yy);
                            matrix.c2.w = 0;
                            matrix.c3.x = position.x; matrix.c3.y = position.y; matrix.c3.z = position.z; matrix.c3.w = 1;
                        }
                    // 111
                    else if ((chunkPositionsExist) && (chunkRotationsExist) && (chunkScalesExist))
                        for (int i = 0; i < parentCount; i++)
                            chunkLocalToWorldsPtr[i].Value = math.mul(new float4x4(chunkRotations[i].Value, chunkPositions[i].Value), float4x4.scale(chunkScales[i].Value));
                }
            }
            finally { RootLocalToWorldChunks.Dispose(); }
        }

        unsafe void UpdateInnerTreeLocalToParent()
        {
            try
            {
                for (int chunkIndex = 0, length = InnerTreeLocalToParentChunks.Length; chunkIndex < length; chunkIndex++)
                {
                    ref var chunk = ref UnsafeUtilityEx.ArrayElementAsRef<ArchetypeChunk>(InnerTreeLocalToParentChunks.GetUnsafePtr(), chunkIndex);
                    var parentCount = chunk.Count;

                    var chunkRotations = chunk.GetNativeArray(RotationTypeRO);
                    var chunkPositions = chunk.GetNativeArray(PositionTypeRO);
                    var chunkScales = chunk.GetNativeArray(ScaleTypeRO);
                    var chunkLocalToParents = chunk.GetNativeArray(LocalToParentTypeRW);

                    var chunkRotationsChanged = ChangeVersionUtility.DidAddOrChange(chunk.GetComponentVersion(RotationTypeRO), LastSystemVersion);
                    var chunkPositionsChanged = ChangeVersionUtility.DidAddOrChange(chunk.GetComponentVersion(PositionTypeRO), LastSystemVersion);
                    var chunkScalesChanged = ChangeVersionUtility.DidAddOrChange(chunk.GetComponentVersion(ScaleTypeRO), LastSystemVersion);
                    var chunkAnyChanged = chunkRotationsChanged || chunkPositionsChanged || chunkScalesChanged;

                    if (!chunkAnyChanged)
                        return;

                    var chunkRotationsExist = chunkRotations.Length > 0;
                    var chunkPositionsExist = chunkPositions.Length > 0;
                    var chunkScalesExist = chunkScales.Length > 0;

                    // 001
                    if ((!chunkPositionsExist) && (!chunkRotationsExist) && (chunkScalesExist))
                        for (int i = 0; i < parentCount; i++)
                            chunkLocalToParents[i] = new LocalToParent(math.mul(float4x4.translate(chunkPositions[i].Value), float4x4.scale(chunkScales[i].Value)));
                    // 010
                    else if ((!chunkPositionsExist) && (chunkRotationsExist) && (!chunkScalesExist))
                        for (int i = 0; i < parentCount; i++)
                            chunkLocalToParents[i] = new LocalToParent(new float4x4(chunkRotations[i].Value, new float3()));
                    // 011
                    else if ((!chunkPositionsExist) && (chunkRotationsExist) && (chunkScalesExist))
                        for (int i = 0; i < parentCount; i++)
                            chunkLocalToParents[i] = new LocalToParent(math.mul(new float4x4(chunkRotations[i].Value, new float3()), float4x4.scale(chunkScales[i].Value)));
                    // 100
                    else if ((chunkPositionsExist) && (!chunkRotationsExist) && (!chunkScalesExist))
                        for (int i = 0; i < parentCount; i++)
                            chunkLocalToParents[i] = new LocalToParent(float4x4.translate(chunkPositions[i].Value));
                    // 101
                    else if ((chunkPositionsExist) && (!chunkRotationsExist) && (chunkScalesExist))
                        for (int i = 0; i < parentCount; i++)
                            chunkLocalToParents[i] = new LocalToParent(math.mul(float4x4.translate(chunkPositions[i].Value), float4x4.scale(chunkScales[i].Value)));
                    // 110
                    else if ((chunkPositionsExist) && (chunkRotationsExist) && (!chunkScalesExist))
                        for (int i = 0; i < parentCount; i++)
                            chunkLocalToParents[i] = new LocalToParent(new float4x4(chunkRotations[i].Value, chunkPositions[i].Value));
                    // 111
                    else if ((chunkPositionsExist) && (chunkRotationsExist) && (chunkScalesExist))
                        for (int i = 0; i < parentCount; i++)
                            chunkLocalToParents[i] = new LocalToParent(math.mul(new float4x4(chunkRotations[i].Value, chunkPositions[i].Value), float4x4.scale(chunkScales[i].Value)));
                }
            }
            finally { InnerTreeLocalToParentChunks.Dispose(); }
        }

        unsafe void UpdateLeafLocalToParent()
        {
            try
            {
                for (int length = LeafLocalToParentChunks.Length, chunkIndex = 0; chunkIndex < length; chunkIndex++)
                {
                    ref var chunk = ref UnsafeUtilityEx.ArrayElementAsRef<ArchetypeChunk>(LeafLocalToParentChunks.GetUnsafePtr(), chunkIndex);
                    var parentCount = chunk.Count;

                    var chunkRotations = chunk.GetNativeArray(RotationTypeRO);
                    var chunkPositions = chunk.GetNativeArray(PositionTypeRO);
                    var chunkScales = chunk.GetNativeArray(ScaleTypeRO);
                    var chunkLocalToParents = chunk.GetNativeArray(LocalToParentTypeRW);

                    var chunkRotationsChanged = ChangeVersionUtility.DidAddOrChange(chunk.GetComponentVersion(RotationTypeRO), LastSystemVersion);
                    var chunkPositionsChanged = ChangeVersionUtility.DidAddOrChange(chunk.GetComponentVersion(PositionTypeRO), LastSystemVersion);
                    var chunkScalesChanged = ChangeVersionUtility.DidAddOrChange(chunk.GetComponentVersion(ScaleTypeRO), LastSystemVersion);
                    var chunkAnyChanged = chunkRotationsChanged || chunkPositionsChanged || chunkScalesChanged;

                    if (!chunkAnyChanged)
                        return;

                    var chunkRotationsExist = chunkRotations.Length > 0;
                    var chunkPositionsExist = chunkPositions.Length > 0;
                    var chunkScalesExist = chunkScales.Length > 0;

                    // 001
                    if ((!chunkPositionsExist) && (!chunkRotationsExist) && (chunkScalesExist))
                        for (int i = 0; i < parentCount; i++)
                            chunkLocalToParents[i] = new LocalToParent(math.mul(float4x4.translate(chunkPositions[i].Value), float4x4.scale(chunkScales[i].Value)));
                    // 010
                    else if ((!chunkPositionsExist) && (chunkRotationsExist) && (!chunkScalesExist))
                        for (int i = 0; i < parentCount; i++)
                            chunkLocalToParents[i] = new LocalToParent(new float4x4(chunkRotations[i].Value, new float3()));
                    // 011
                    else if ((!chunkPositionsExist) && (chunkRotationsExist) && (chunkScalesExist))
                        for (int i = 0; i < parentCount; i++)
                            chunkLocalToParents[i] = new LocalToParent(math.mul(new float4x4(chunkRotations[i].Value, new float3()), float4x4.scale(chunkScales[i].Value)));
                    // 100
                    else if ((chunkPositionsExist) && (!chunkRotationsExist) && (!chunkScalesExist))
                        for (int i = 0; i < parentCount; i++)
                            chunkLocalToParents[i] = new LocalToParent(float4x4.translate(chunkPositions[i].Value));
                    // 101
                    else if ((chunkPositionsExist) && (!chunkRotationsExist) && (chunkScalesExist))
                        for (int i = 0; i < parentCount; i++)
                            chunkLocalToParents[i] = new LocalToParent(math.mul(float4x4.translate(chunkPositions[i].Value), float4x4.scale(chunkScales[i].Value)));
                    // 110
                    else if ((chunkPositionsExist) && (chunkRotationsExist) && (!chunkScalesExist))
                        for (int i = 0; i < parentCount; i++)
                            chunkLocalToParents[i] = new LocalToParent(new float4x4(chunkRotations[i].Value, chunkPositions[i].Value));
                    // 111
                    else if ((chunkPositionsExist) && (chunkRotationsExist) && (chunkScalesExist))
                        for (int i = 0; i < parentCount; i++)
                            chunkLocalToParents[i] = new LocalToParent(math.mul(new float4x4(chunkRotations[i].Value, chunkPositions[i].Value), float4x4.scale(chunkScales[i].Value)));
                }
            }
            finally { LeafLocalToParentChunks.Dispose(); }
        }

        unsafe void UpdateInnerTreeLocalToWorld()
        {
            try
            {
                var length = InnerTreeLocalToWorldChunks.Length;
                if (length == 0) return;
                var sharedComponentCount = EntityManager.GetSharedComponentCount();
                var sharedDepths = new List<Depth>(sharedComponentCount);
                var sharedDepthIndices = new List<int>(sharedComponentCount);
                EntityManager.GetAllUniqueSharedComponentData(sharedDepths, sharedDepthIndices);
                var depthCount = sharedDepths.Count;
                var depths = new NativeArray<int>(sharedComponentCount, Allocator.Temp);
                int maxDepth = 0;
                for (int i = 0; i < depthCount; i++)
                {
                    var index = sharedDepthIndices[i];
                    var depth = sharedDepths[i].Value;
                    if (depth > maxDepth)
                    {
                        maxDepth = depth;
                    }

                    depths[index] = depth;
                }

                var chunkIndices = new NativeArray<int>(InnerTreeLocalToWorldChunks.Length, Allocator.Temp);
                // Slow and dirty sort inner tree by depth
                for (int depth = -1, chunkIndex = 0; depth < maxDepth; depth++)
                {
                    for (int i = 0; i < InnerTreeLocalToWorldChunks.Length; i++)
                    {
                        var chunk = InnerTreeLocalToWorldChunks[i];
                        var chunkDepthSharedIndex = chunk.GetSharedComponentIndex(DepthTypeRO);
                        var chunkDepth = -1;

                        // -1 = Depth has been removed, but still matching archetype for some reason. #todo
                        if (chunkDepthSharedIndex != -1)
                        {
                            chunkDepth = depths[chunkDepthSharedIndex];
                        }

                        if (chunkDepth == depth)
                        {
                            chunkIndices[chunkIndex] = i;
                            chunkIndex++;
                        }
                    }
                }
                depths.Dispose();

                for (int i = 0; i < length; i++)
                {
                    var chunkIndex = chunkIndices[i];
                    var chunk = InnerTreeLocalToWorldChunks[chunkIndex];
                    var chunkLocalToParents = chunk.GetNativeArray(LocalToParentTypeRO);

                    var chunkParents = chunk.GetNativeArray(ParentTypeRO);
                    var chunkEntities = chunk.GetNativeArray(EntityTypeRO);
                    var previousParentEntity = Entity.Null;
                    var parentLocalToWorldMatrix = new float4x4();

                    for (int j = 0; j < chunk.Count; j++)
                    {
                        var parentEntity = chunkParents[j].Value;
                        if (parentEntity != previousParentEntity)
                        {
                            parentLocalToWorldMatrix = LocalToWorldFromEntityRW[parentEntity].Value;
                            previousParentEntity = parentEntity;
                        }
                        var entity = chunkEntities[j];
                        LocalToWorldFromEntityRW[entity] = new LocalToWorld(math.mul(parentLocalToWorldMatrix, chunkLocalToParents[j].Value));
                    }
                }
                chunkIndices.Dispose();
            }
            finally { InnerTreeLocalToWorldChunks.Dispose(); }
        }

        unsafe void UpdateLeafLocalToWorld()
        {
            try
            {
                var length = LeafLocalToWorldChunks.Length;
                if (length == 0) return;
                for (int i = 0; i < length; i++)
                {
                    ref var chunk = ref UnsafeUtilityEx.ArrayElementAsRef<ArchetypeChunk>(LeafLocalToWorldChunks.GetUnsafePtr(), i);
                    var chunkLocalToParents = chunk.GetNativeArray(LocalToParentTypeRO);
                    var chunkEntities = chunk.GetNativeArray(EntityTypeRO);
                    var chunkParents = chunk.GetNativeArray(ParentTypeRO);
                    var previousParentEntity = Entity.Null;
                    var parentLocalToWorldMatrix = new float4x4();

                    for (int j = 0; j < chunk.Count; j++)
                    {
                        var parentEntity = chunkParents[j].Value;
                        if (parentEntity != previousParentEntity)
                        {
                            parentLocalToWorldMatrix = LocalToWorldFromEntityRW[parentEntity].Value;
                            previousParentEntity = parentEntity;
                        }

                        LocalToWorldFromEntityRW[chunkEntities[j]] = new LocalToWorld(math.mul(parentLocalToWorldMatrix, chunkLocalToParents[j].Value));
                    }
                }
            }
            finally
            {
                LeafLocalToWorldChunks.Dispose();
            }
        }

        int ParentCount(Entity entity) => EntityManager.HasComponent<Parent>(entity) ? 1 + ParentCount(ParentFromEntityRO[entity].Value) : 0;

        private static readonly ProfilerMarker k_ProfileUpdateDepthChunks = new ProfilerMarker("UpdateDepth.Chunks");

        unsafe void UpdateDepth()
        {
            try
            {
                if (DepthChunks.Length == 0) return;

                k_ProfileUpdateDepthChunks.Begin();
                for (int i = 0; i < DepthChunks.Length; i++)
                {
                    ref var chunk = ref UnsafeUtilityEx.ArrayElementAsRef<ArchetypeChunk>(DepthChunks.GetUnsafePtr(), i);
                    var parents = chunk.GetNativeArray(ParentTypeRO);
                    var entities = chunk.GetNativeArray(EntityTypeRO);
                    for (int j = 0, entityCount = chunk.Count; j < entityCount; j++)
                        PostUpdateCommands.SetSharedComponent(entities[j], new Depth(1 + ParentCount(parents[j].Value)));
                }
                k_ProfileUpdateDepthChunks.End();
            }
            finally { DepthChunks.Dispose(); }
        }

        void GatherQueries()
        {
            var _Parent = ComponentType.ReadOnly<Parent>();
            var _Rotation = ComponentType.ReadOnly<Rotation>();
            var _Position = ComponentType.ReadOnly<Position>();
            var _Scale = ComponentType.ReadOnly<Scale>();
            var _Frozen = ComponentType.ReadOnly<Frozen>();
            var _Depth = ComponentType.Create<Depth>();
            var _LocalToWorld = ComponentType.Create<LocalToWorld>();
            var _Static = ComponentType.ReadOnly<Static>();
            var _PendingFrozen = ComponentType.ReadOnly<PendingFrozen>();
            var _LocalToParent = ComponentType.Create<LocalToParent>();
            var componentTypes0_RPS = new[] { _Rotation, _Position, _Scale };
            var componentTypes1_PendF = new[] { _PendingFrozen, _Frozen };
            var componentTypes2_F = new[] { _Frozen };
            var componentTypes3_L2P = new[] { _LocalToParent, _Parent };
            var componentTypes_Empty = Array.Empty<ComponentType>();
            NewRootQuery = new EntityArchetypeQuery
            {
                Any = componentTypes0_RPS,
                None = new[] { _Frozen, _Parent, _LocalToWorld, _Depth },
                All = componentTypes_Empty,
            };
            AttachQuery = new EntityArchetypeQuery
            {
                Any = componentTypes_Empty,
                None = componentTypes_Empty,
                All = new[] { ComponentType.ReadOnly<Attach>() },
            };
            DetachQuery = new EntityArchetypeQuery
            {
                Any = componentTypes_Empty,
                None = new[] { ComponentType.ReadOnly<Attached>() },
                All = new[] { _Parent },
            };
            RemovedQuery = new EntityArchetypeQuery
            {
                Any = componentTypes_Empty,
                None = componentTypes0_RPS,
                All = new[] { _LocalToWorld },
            };
            PendingFrozenQuery = new EntityArchetypeQuery
            {
                Any = componentTypes_Empty,
                None = componentTypes2_F,
                All = new[] { _LocalToWorld, _Static, _PendingFrozen },
            };
            FrozenQuery = new EntityArchetypeQuery
            {
                Any = componentTypes_Empty,
                None = componentTypes1_PendF,
                All = new[] { _LocalToWorld, _Static },
            };
            ThawQuery = new EntityArchetypeQuery
            {
                Any = componentTypes1_PendF,
                None = new[] { _Static },
                All = componentTypes_Empty,
            };
            RootLocalToWorldQuery = new EntityArchetypeQuery
            {
                Any = componentTypes0_RPS,
                None = new[] { _Frozen, _Parent },
                All = new[] { _LocalToWorld },
            };
            InnerTreeLocalToParentQuery = new EntityArchetypeQuery
            {
                Any = componentTypes0_RPS,
                None = componentTypes2_F,
                All = componentTypes3_L2P,
            };
            LeafLocalToParentQuery = new EntityArchetypeQuery
            {
                Any = componentTypes0_RPS,
                None = componentTypes2_F,
                All = componentTypes3_L2P,
            };
            InnerTreeLocalToWorldQuery = new EntityArchetypeQuery
            {
                Any = componentTypes_Empty,
                None = componentTypes2_F,
                All = new[] { _Depth, _LocalToParent, _Parent, _LocalToWorld },
            };
            LeafLocalToWorldQuery = new EntityArchetypeQuery
            {
                Any = componentTypes0_RPS,
                None = new[] { _Frozen, _Depth },
                All = componentTypes3_L2P,
            };
            DepthQuery = new EntityArchetypeQuery
            {
                Any = componentTypes_Empty,
                None = componentTypes_Empty,
                All = new[] { _Depth, _Parent },
            };
        }

        void GatherFrozenChunks()
        {
            PendingFrozenChunks = EntityManager.CreateArchetypeChunkArray(PendingFrozenQuery, Allocator.Temp);
            FrozenChunks = EntityManager.CreateArchetypeChunkArray(FrozenQuery, Allocator.Temp);
            ThawChunks = EntityManager.CreateArchetypeChunkArray(ThawQuery, Allocator.Temp);
        }

        void GatherDAGChunks()
        {
            NewRootChunks = EntityManager.CreateArchetypeChunkArray(NewRootQuery, Allocator.Temp);
            AttachChunks = EntityManager.CreateArchetypeChunkArray(AttachQuery, Allocator.Temp);
            DetachChunks = EntityManager.CreateArchetypeChunkArray(DetachQuery, Allocator.Temp);
            RemovedChunks = EntityManager.CreateArchetypeChunkArray(RemovedQuery, Allocator.Temp);
        }

        void GatherDepthChunks()
        {
            DepthChunks = EntityManager.CreateArchetypeChunkArray(DepthQuery, Allocator.Temp);
        }

        void GatherUpdateChunks()
        {
            RootLocalToWorldChunks = EntityManager.CreateArchetypeChunkArray(RootLocalToWorldQuery, Allocator.Temp);
            InnerTreeLocalToParentChunks = EntityManager.CreateArchetypeChunkArray(InnerTreeLocalToParentQuery, Allocator.Temp);
            LeafLocalToParentChunks = EntityManager.CreateArchetypeChunkArray(LeafLocalToParentQuery, Allocator.Temp);
            InnerTreeLocalToWorldChunks = EntityManager.CreateArchetypeChunkArray(InnerTreeLocalToWorldQuery, Allocator.Temp);
            LeafLocalToWorldChunks = EntityManager.CreateArchetypeChunkArray(LeafLocalToWorldQuery, Allocator.Temp);
        }

        void GatherTypes()
        {
            ParentFromEntityRW = GetComponentDataFromEntity<Parent>(false);
            LocalToWorldFromEntityRW = GetComponentDataFromEntity<LocalToWorld>(false);
            LocalToWorldTypeRW = GetArchetypeChunkComponentType<LocalToWorld>(false);
            LocalToParentTypeRW = GetArchetypeChunkComponentType<LocalToParent>(false);

            ParentFromEntityRO = GetComponentDataFromEntity<Parent>(true);
            EntityTypeRO = GetArchetypeChunkEntityType();
            ParentTypeRO = GetArchetypeChunkComponentType<Parent>(true);
            LocalToParentTypeRO = GetArchetypeChunkComponentType<LocalToParent>(true);
            DepthTypeRO = GetArchetypeChunkSharedComponentType<Depth>();
            RotationTypeRO = GetArchetypeChunkComponentType<Rotation>(true);
            PositionTypeRO = GetArchetypeChunkComponentType<Position>(true);
            ScaleTypeRO = GetArchetypeChunkComponentType<Scale>(true);
            AttachTypeRO = GetArchetypeChunkComponentType<Attach>(true);

            FrozenTypeRO = GetArchetypeChunkComponentType<Frozen>(true);
            PendingFrozenTypeRO = GetArchetypeChunkComponentType<PendingFrozen>(true);
        }

        private static readonly ProfilerMarker k_ProfileGatherDAGChunks = new ProfilerMarker("GatherDAGChunks");
        private static readonly ProfilerMarker k_ProfileUpdateDAG = new ProfilerMarker("UpdateDAG");
        private static readonly ProfilerMarker k_ProfileGatherDepthChunks = new ProfilerMarker("GatherDepthChunks");
        private static readonly ProfilerMarker k_ProfileUpdateDepth = new ProfilerMarker("UpdateDepth");
        private static readonly ProfilerMarker k_ProfileGatherFrozenChunks = new ProfilerMarker("GatherFrozenChunks");
        private static readonly ProfilerMarker k_ProfileUpdateFrozen = new ProfilerMarker("UpdateFrozen");
        private static readonly ProfilerMarker k_ProfileGatherUpdateChunks = new ProfilerMarker("GatherUpdateChunks");
        private static readonly ProfilerMarker k_ProfileUpdateRootLocalToWorld = new ProfilerMarker("UpdateRootLocalToWorld");
        private static readonly ProfilerMarker k_ProfileUpdateInnerTreeLocalToParent = new ProfilerMarker("UpdateInnerTreeLocalToParent");
        private static readonly ProfilerMarker k_ProfileUpdateLeafLocalToParent = new ProfilerMarker("UpdateLeafLocalToParent");
        private static readonly ProfilerMarker k_ProfileUpdateInnerTreeLocalToWorld = new ProfilerMarker("UpdateInnerTreeLocalToWorld");
        private static readonly ProfilerMarker k_ProfileUpdateLeafLocalToWorld = new ProfilerMarker("UpdateLeafLocalToWorld");

        protected override void OnUpdate()
        {
            // Update DAG
            using (k_ProfileGatherDAGChunks.Auto())
            {
                GatherTypes();
                GatherDAGChunks();
            }

            k_ProfileUpdateDAG.Begin();
            var changedDepthStructure = UpdateDAG();
            k_ProfileUpdateDAG.End();

            // Update Transforms

            if (changedDepthStructure)
            {
                using (k_ProfileGatherDepthChunks.Auto())
                {
                    GatherTypes();
                    GatherDepthChunks();
                }

                using (k_ProfileUpdateDepth.Auto())
                    UpdateDepth();
            }

            using (k_ProfileGatherFrozenChunks.Auto())
            {
                GatherTypes();
                GatherFrozenChunks();
            }

            k_ProfileUpdateFrozen.Begin();
            UpdatePendingFrozen();
            UpdateFrozen();
            UpdateThaw();
            k_ProfileUpdateFrozen.End();

            k_ProfileGatherUpdateChunks.Begin();
            GatherTypes();
            GatherUpdateChunks();
            k_ProfileGatherUpdateChunks.End();

            k_ProfileUpdateRootLocalToWorld.Begin();
            UpdateRootLocalToWorld();
            k_ProfileUpdateRootLocalToWorld.End();

            k_ProfileUpdateInnerTreeLocalToParent.Begin();
            UpdateInnerTreeLocalToParent();
            k_ProfileUpdateInnerTreeLocalToParent.End();

            k_ProfileUpdateLeafLocalToParent.Begin();
            UpdateLeafLocalToParent();
            k_ProfileUpdateLeafLocalToParent.End();

            k_ProfileUpdateInnerTreeLocalToWorld.Begin();
            UpdateInnerTreeLocalToWorld();
            k_ProfileUpdateInnerTreeLocalToWorld.End();

            k_ProfileUpdateLeafLocalToWorld.Begin();
            UpdateLeafLocalToWorld();
            k_ProfileUpdateLeafLocalToWorld.End();

            LastSystemVersion = GlobalSystemVersion;
        }
    }
}