using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using UnityEngine;

namespace Unity.Rendering
{
    public sealed class LODGroupSystem : ComponentSystem
    {
        public Camera ActiveCamera;
        ComponentGroup g;

        public LODGroupSystem() { }
        public LODGroupSystem(Camera activeCamera) => ActiveCamera = activeCamera;

        protected override void OnCreateManager(int capacity) => g = GetComponentGroup(ComponentType.ReadOnly<MeshLODGroupComponent>(), ComponentType.Create<ActiveLODGroupMask>());

        protected override unsafe void OnUpdate()
        {
            if (ActiveCamera == null) return;
            var HLODActiveMask = GetComponentDataFromEntity<ActiveLODGroupMask>(true);
            var LODParams = LODGroupExtensions.CalculateLODParams(ActiveCamera);
            var lodGroups = g.GetComponentDataArray<MeshLODGroupComponent>();
            var activeMasks = g.GetComponentDataArray<ActiveLODGroupMask>();
            var length = activeMasks.Length;
            for (int consumed = 0; consumed != length;)
            {
                var chunkLODGroup = lodGroups.GetChunkArray(consumed, length - consumed);
                var chunkActiveMask = activeMasks.GetChunkArray(consumed, length - consumed);
                for (int i = 0, chunkLength = chunkLODGroup.Length; i < chunkLength; i++)
                {
                    ref var activeMask = ref UnsafeUtilityEx.ArrayElementAsRef<ActiveLODGroupMask>(chunkActiveMask.GetUnsafePtr(), i);
                    ref var lodGroup = ref UnsafeUtilityEx.ArrayElementAsRef<MeshLODGroupComponent>(chunkLODGroup.GetUnsafeReadOnlyPtr(), i);
                    activeMask.LODMask = LODGroupExtensions.CalculateCurrentLODMask(lodGroup.LODDistances, lodGroup.WorldReferencePoint, ref LODParams);
                }
                consumed += chunkLODGroup.Length;
            }
            for (int consumed = 0; consumed != length;)
            {
                var chunkLODGroup = lodGroups.GetChunkArray(consumed, length - consumed);
                var chunkActiveMask = activeMasks.GetChunkArray(consumed, length - consumed);
                for (int i = 0, chunkLength = chunkLODGroup.Length; i < chunkLength; i++)
                {
                    ref var activeMask = ref UnsafeUtilityEx.ArrayElementAsRef<ActiveLODGroupMask>(chunkActiveMask.GetUnsafePtr(), i);
                    ref var lodGroup = ref UnsafeUtilityEx.ArrayElementAsRef<MeshLODGroupComponent>(chunkLODGroup.GetUnsafeReadOnlyPtr(), i);
                    if (lodGroup.ParentGroup != Entity.Null)
                    {
                        var parentMask = HLODActiveMask[lodGroup.ParentGroup].LODMask;
                        if ((parentMask & lodGroup.ParentMask) == 0)
                        {
                            activeMask.LODMask = 0;
                            continue;
                        }
                    }
                    activeMask.LODMask = LODGroupExtensions.CalculateCurrentLODMask(lodGroup.LODDistances, lodGroup.WorldReferencePoint, ref LODParams);
                }
                consumed += chunkLODGroup.Length;
            }
        }
    }
}